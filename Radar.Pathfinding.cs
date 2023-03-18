using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Helpers;
using GameOffsets;
using GameOffsets.Native;
using Newtonsoft.Json;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace Radar;

public partial class Radar
{
    private void LoadTargets()
    {
        var fileText = File.ReadAllText(Path.Combine(DirectoryFullName, "targets.json"));
        _targetDescriptions = JsonConvert.DeserializeObject<ConcurrentDictionary<string, List<TargetDescription>>>(fileText);
    }

    private void RestartPathFinding()
    {
        StopPathFinding();
        StartPathFinding();
    }

    private void StartPathFinding()
    {
        if (Settings.PathfindingSettings.ShowPathsToTargetsOnMap)
        {
            FindPaths(_clusteredTargetLocations, _routes, _findPathsCts.Token);
        }
    }

    private void StopPathFinding()
    {
        _findPathsCts.Cancel();
        _findPathsCts = new CancellationTokenSource();
        _routes = new ConcurrentDictionary<Vector2, RouteDescription>();
    }

    private void FindPaths(IReadOnlyDictionary<string, TargetLocations> tiles, ConcurrentDictionary<Vector2, RouteDescription> routes, CancellationToken cancellationToken)
    {
        var targets = tiles.SelectMany(x => x.Value.Locations)
           .Distinct()
           .ToList();
        var pf = new PathFinder(_processedTerrainData, new[] { 1, 2, 3, 4, 5 });
        foreach (var (point, color) in targets.Take(Settings.MaximumPathCount).Zip(Enumerable.Repeat(RainbowColors, 100).SelectMany(x => x)))
        {
            Task.Run(() => FindPath(pf, point, color, routes, cancellationToken));
        }
    }

    private async Task WaitUntilPluginEnabled(CancellationToken cancellationToken)
    {
        while (!Settings.Enable)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task FindPath(PathFinder pf, Vector2 point, Color color, ConcurrentDictionary<Vector2, RouteDescription> routes, CancellationToken cancellationToken)
    {
        Color GetWorldColor() => Settings.PathfindingSettings.WorldPathSettings.UseRainbowColorsForPaths ? color : Settings.PathfindingSettings.WorldPathSettings.DefaultPathColor;
        Color GetMapColor() => Settings.PathfindingSettings.UseRainbowColorsForMapPaths ? color : Settings.PathfindingSettings.DefaultMapPathColor;
        var playerPosition = GetPlayerPosition();
        var pathI = pf.RunFirstScan(new Vector2i((int)playerPosition.X, (int)playerPosition.Y), new Vector2i((int)point.X, (int)point.Y));
        foreach (var elem in pathI)
        {
            await WaitUntilPluginEnabled(cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (elem.Any())
            {
                var rd = new RouteDescription { Path = elem, MapColor = GetMapColor, WorldColor = GetWorldColor };
                routes.AddOrUpdate(point, rd, (_, _) => rd);
            }
        }

        while (true)
        {
            await WaitUntilPluginEnabled(cancellationToken);
            var newPosition = GetPlayerPosition();
            if (playerPosition == newPosition)
            {
                await Task.Delay(100, cancellationToken);
                continue;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            playerPosition = newPosition;
            var path = pf.FindPath(new Vector2i((int)playerPosition.X, (int)playerPosition.Y), new Vector2i((int)point.X, (int)point.Y));
            if (path != null)
            {
                var rd = new RouteDescription { Path = path, MapColor = GetMapColor, WorldColor = GetWorldColor };
                routes.AddOrUpdate(point, rd, (_, _) => rd);
            }
        }
    }

    private ConcurrentDictionary<string, List<Vector2i>> GetTargets()
    {
        return new ConcurrentDictionary<string, List<Vector2i>>(GetTileTargets().Concat(GetEntityTargets())
           .ToLookup(x => x.Key, x => x.Value)
           .ToDictionary(x => x.Key, x => x.SelectMany(v => v).ToList()));
    }

    private Dictionary<string, List<Vector2i>> GetEntityTargets()
    {
        return GameController.Entities.Where(x => x.HasComponent<Positioned>()).Where(x => _currentZoneTargetEntityPaths.Contains(x.Path))
           .ToLookup(x => x.Path, x => x.GetComponent<Positioned>().GridPosNum.Truncate())
           .ToDictionary(x => x.Key, x => x.ToList());
    }

    private Dictionary<string, List<Vector2i>> GetTileTargets()
    {
        var tileData = GameController.Memory.ReadStdVector<TileStructure>(_terrainMetadata.TgtArray);
        var ret = new ConcurrentDictionary<string, ConcurrentQueue<Vector2i>>();
        Parallel.For(0, tileData.Length, tileNumber =>
        {
            var key = GameController.Memory.Read<TgtDetailStruct>
            (GameController.Memory.Read<TgtTileStruct>(tileData[tileNumber].TgtFilePtr)
               .TgtDetailPtr).name.ToString(GameController.Memory);
            if (string.IsNullOrEmpty(key))
                return;
            var stdTuple2D = new Vector2i(
                tileNumber % _terrainMetadata.NumCols * TileToGridConversion,
                tileNumber / _terrainMetadata.NumCols * TileToGridConversion);

            ret.GetOrAdd(key, _ => new ConcurrentQueue<Vector2i>()).Enqueue(stdTuple2D);
        });
        return ret.ToDictionary(k => k.Key, k => k.Value.ToList());
    }

    private bool IsDescriptionInArea(string descriptionAreaPattern)
    {
        return GameController.Area.CurrentArea.Area.RawName.Like(descriptionAreaPattern);
    }

    private IEnumerable<TargetDescription> GetTargetDescriptionsInArea()
    {
        return _targetDescriptions.Where(x => IsDescriptionInArea(x.Key)).SelectMany(x => x.Value);
    }

    private ConcurrentDictionary<string, TargetLocations> ClusterTargets()
    {
        var tileMap = new ConcurrentDictionary<string, TargetLocations>();
        Parallel.ForEach(_targetDescriptionsInArea.Values, new ParallelOptions { MaxDegreeOfParallelism = 1 }, target =>
        {
            var locations = ClusterTarget(target);
            if (locations != null)
            {
                tileMap[target.Name] = locations;
            }
        });
        return tileMap;
    }

    private TargetLocations ClusterTarget(TargetDescription target)
    {
        if (!_allTargetLocations.TryGetValue(target.Name, out var tileList))
            return null;
        var clusterIndexes = KMeans.Cluster(tileList.Select(x => new Vector2d(x.X, x.Y)).ToArray(), target.ExpectedCount);
        var resultList = new List<Vector2>();
        foreach (var tileGroup in tileList.Zip(clusterIndexes).GroupBy(x => x.Second))
        {
            var v = new Vector2();
            var count = 0;
            foreach (var (vector, _) in tileGroup)
            {
                var mult = IsGridWalkable(vector) ? 100 : 1;
                v += mult * vector.ToVector2Num();
                count += mult;
            }

            v /= count;
            var replacement = tileGroup.Select(tile => new Vector2i(tile.First.X, tile.First.Y))
               .Where(IsGridWalkable)
               .OrderBy(x => (x.ToVector2Num() - v).LengthSquared())
               .Select(x => (Vector2i?)x)
               .FirstOrDefault();
            if (replacement != null)
            {
                v = replacement.Value.ToVector2Num();
            }

            if (!IsGridWalkable(v.Truncate()))
            {
                v = GetAllNeighborTiles(v.Truncate()).First(IsGridWalkable).ToVector2Num();
            }

            resultList.Add(v);
        }

        return new TargetLocations
        {
            Locations = resultList.Distinct().ToArray(),
            DisplayName = target.DisplayName
        };
    }

    private bool IsGridWalkable(Vector2i tile)
    {
        return _processedTerrainData[tile.Y][tile.X] is 5 or 4;
    }

    private IEnumerable<Vector2i> GetAllNeighborTiles(Vector2i start)
    {
        foreach (var range in Enumerable.Range(1, 100000))
        {
            var xStart = Math.Max(0, start.X - range);
            var yStart = Math.Max(0, start.Y - range);
            var xEnd = Math.Min(_areaDimensions.Value.X, start.X + range);
            var yEnd = Math.Min(_areaDimensions.Value.Y, start.Y + range);
            for (var x = xStart; x <= xEnd; x++)
            {
                yield return new Vector2i(x, yStart);
                yield return new Vector2i(x, yEnd);
            }

            for (var y = yStart + 1; y <= yEnd - 1; y++)
            {
                yield return new Vector2i(xStart, y);
                yield return new Vector2i(xEnd, y);
            }

            if (xStart == 0 && yStart == 0 && xEnd == _areaDimensions.Value.X && yEnd == _areaDimensions.Value.Y)
            {
                break;
            }
        }
    }
}
