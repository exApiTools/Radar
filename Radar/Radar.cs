using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Helpers;
using GameOffsets;
using GameOffsets.Components;
using GameOffsets.Native;
using Newtonsoft.Json;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Convolution;
using Color = SharpDX.Color;
using Configuration = SixLabors.ImageSharp.Configuration;
using Positioned = ExileCore.PoEMemory.Components.Positioned;
using RectangleF = SixLabors.ImageSharp.RectangleF;
using Vector4 = System.Numerics.Vector4;

namespace Radar
{
    public class Radar : BaseSettingsPlugin<RadarSettings>
    {
        private const string TextureName = "radar_minimap";
        private const float GridToWorldMultiplier = 250 / 23f;
        private const double TileHeightFinalMultiplier = 125 / 16.0; //this translates the height in the tile metadata to
        private const double CameraAngle = 38.7 * Math.PI / 180;
        private static readonly float CameraAngleCos = (float)Math.Cos(CameraAngle);
        private static readonly float CameraAngleSin = (float)Math.Sin(CameraAngle);
        private double _mapScale;

        private ConcurrentDictionary<string, List<TargetDescription>> _targetDescriptions = new();
        private Vector2i? _areaDimensions;
        private TerrainData _terrainMetadata;
        private float[][] _heightData;
        private byte[] _rawTerrainData;
        private int[][] _processedTerrainData;
        private Dictionary<string, TargetDescription> _targetDescriptionsInArea = new();
        private HashSet<string> _currentZoneTargetEntityPaths = new();
        private CancellationTokenSource _findPathsCts = new CancellationTokenSource();
        private ConcurrentDictionary<string, TargetLocations> _clusteredTargetLocations = new();
        private ConcurrentDictionary<string, List<Vector2i>> _allTargetLocations = new();
        private ConcurrentDictionary<string, RouteDescription> _routes = new();

        public override void AreaChange(AreaInstance area)
        {
            StopPathFinding();
            if (GameController.Game.IsInGameState || GameController.Game.IsEscapeState)
            {
                _targetDescriptionsInArea = GetTargetDescriptionsInArea().ToDictionary(x => x.Name);
                _currentZoneTargetEntityPaths = _targetDescriptionsInArea.Values.Where(x => x.TargetType == TargetType.Entity).Select(x => x.Name).ToHashSet();
                _terrainMetadata = GameController.IngameState.Data.DataStruct.Terrain;
                _rawTerrainData = GameController.Memory.ReadStdVector<byte>(_terrainMetadata.LayerMelee);
                _heightData = GetTerrainHeight();
                _allTargetLocations = GetTargets();
                _processedTerrainData = GenerateMapTextureAndTerrainData();
                _clusteredTargetLocations = ClusterTargets();
                StartPathFinding();
            }
        }

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
            if (Settings.ShowPathsToTargets)
            {
                FindPaths(_clusteredTargetLocations, _routes, _findPathsCts.Token);
            }
        }

        private void StopPathFinding()
        {
            _findPathsCts.Cancel();
            _findPathsCts = new CancellationTokenSource();
            _routes = new ConcurrentDictionary<string, RouteDescription>();
        }

        private void FindPaths(IReadOnlyDictionary<string, TargetLocations> tiles, ConcurrentDictionary<string, RouteDescription> routes, CancellationToken cancellationToken)
        {
            var numbers = tiles.SelectMany(x => x.Value.Locations.Select((y, i) => (x.Key, i, y)))
               .ToDictionary(x => $"{x.Key}_{x.i}", x => x.y);
            var pf = new PathFinder(_processedTerrainData, new[] { 1, 2, 3, 4, 5 });
            foreach (var (name, point) in numbers.OrderBy(x => x.Key).Take(Settings.MaximumPathCount))
            {
                Task.Run(() => FindPath(pf, name, point, routes, cancellationToken));
            }
        }

        private async Task WaitUntilPluginEnabled(CancellationToken cancellationToken)
        {
            while (!Settings.Enable)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }

        private async Task FindPath(PathFinder pf, string name, Vector2 point,
                                    ConcurrentDictionary<string, RouteDescription> routes, CancellationToken cancellationToken)
        {
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
                    var rd = new RouteDescription { Path = elem, Color = Settings.DefaultPathColor };
                    _routes.AddOrUpdate(name, rd, (_, _) => rd);
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

                playerPosition = newPosition;
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var path = pf.FindPath(new Vector2i((int)playerPosition.X, (int)playerPosition.Y), new Vector2i((int)point.X, (int)point.Y));
                if (path != null)
                {
                    var rd = new RouteDescription { Path = path, Color = Settings.DefaultPathColor };
                    routes.AddOrUpdate(name, rd, (_, _) => rd);
                }
            }
        }


        public override void OnLoad()
        {
            LoadTargets();
            Settings.Reload.OnPressed = () =>
            {
                Core.MainRunner.Run(new Coroutine(() =>
                {
                    LoadTargets();
                    AreaChange(GameController.Area.CurrentArea);
                }, new WaitTime(0), this, "RestartPathfinding", false, true));
            };
            Settings.MaximumPathCount.OnValueChanged += (_, _) =>
            {
                Core.MainRunner.Run(new Coroutine(RestartPathFinding, new WaitTime(0), this, "RestartPathfinding", false, true));
            };
        }

        public override void EntityAdded(Entity entity)
        {
            var positioned = entity.GetComponent<Positioned>();
            if (positioned != null)
            {
                var path = entity.Path;
                if (_currentZoneTargetEntityPaths.Contains(path))
                {
                    bool alreadyContains = false;
                    _allTargetLocations.AddOrUpdate(path, _ => new List<Vector2i> { positioned.GridPos.Truncate() },
                        // ReSharper disable once AssignmentInConditionalExpression
                        (_, l) => (alreadyContains = l.Contains(positioned.GridPos.Truncate())) ? l : l.Append(positioned.GridPos.Truncate()).ToList());
                    if (!alreadyContains)
                    {
                        var oldValue = _clusteredTargetLocations.GetValueOrDefault(path);
                        var newValue = _clusteredTargetLocations.AddOrUpdate(path,
                            _ => ClusterTarget(_targetDescriptionsInArea[path]),
                            (_, _) => ClusterTarget(_targetDescriptionsInArea[path]));
                        //restarting PF is a VERY expensive option, so spare some cycles to check we actually need it
                        if (oldValue == null || !newValue.Locations.ToHashSet().SetEquals(oldValue.Locations))
                        {
                            RestartPathFinding();
                        }
                    }
                }
            }
        }

        private float[][] GetTerrainHeight()
        {
            var rotationSelector = GameController.Game.RotationSelectorValues;
            var rotationHelper = GameController.Game.RotationHelperValues;
            var tileData = GameController.Memory.ReadStdVector<TileStructure>(_terrainMetadata.TileDetails);
            var tileHeightCache = tileData.Select(x => x.SubTileDetailsPtr)
               .Distinct()
               .AsParallel()
               .Select(addr => new
                {
                    addr,
                    data = GameController.Memory.ReadStdVector<sbyte>(GameController.Memory.Read<SubTileStructure>(addr).SubTileHeight)
                })
               .ToDictionary(x => x.addr, x => x.data);
            var gridSizeX = (int)_terrainMetadata.NumCols * TileStructure.TileToGridConversion;
            var toExclusive = (int)_terrainMetadata.NumRows * TileStructure.TileToGridConversion;
            var result = new float[toExclusive][];
            Parallel.For(0, toExclusive, y =>
            {
                result[y] = new float[gridSizeX];
                for (var x = 0; x < gridSizeX; ++x)
                {
                    var tileStructure = tileData[y / TileStructure.TileToGridConversion * (int)_terrainMetadata.NumCols + x / TileStructure.TileToGridConversion];
                    var tileHeightArray = tileHeightCache[tileStructure.SubTileDetailsPtr];
                    var num1 = 0;
                    if (tileHeightArray.Length != 0)
                    {
                        var gridX = x % TileStructure.TileToGridConversion;
                        var gridY = y % TileStructure.TileToGridConversion;
                        var maxCoordInTile = TileStructure.TileToGridConversion - 1;
                        int[] coordHelperArray =
                        {
                            maxCoordInTile - gridX,
                            gridX,
                            maxCoordInTile - gridY,
                            gridY
                        };
                        var rotationIndex = rotationSelector[tileStructure.RotationSelector] * 3;
                        int axisSwitch = rotationHelper[rotationIndex];
                        int smallAxisFlip = rotationHelper[rotationIndex + 1];
                        int largeAxisFlip = rotationHelper[rotationIndex + 2];
                        var smallIndex = coordHelperArray[axisSwitch * 2 + smallAxisFlip];
                        var index = coordHelperArray[largeAxisFlip + (1 - axisSwitch) * 2] * TileStructure.TileToGridConversion + smallIndex;
                        num1 = tileHeightArray[index];
                    }

                    result[y][x] = (float)((tileStructure.TileHeight * (int)_terrainMetadata.TileHeightMultiplier + num1) * TileHeightFinalMultiplier);
                }
            });
            return result;
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
               .ToLookup(x => x.Path, x => x.GetComponent<Positioned>().GridPos.Truncate())
               .ToDictionary(x => x.Key, x => x.ToList());
        }

        private Dictionary<string, List<Vector2i>> GetTileTargets()
        {
            var tileData = GameController.Memory.ReadStdVector<TileStructure>(_terrainMetadata.TileDetails);
            var ret = new ConcurrentDictionary<string, ConcurrentQueue<Vector2i>>();
            Parallel.For(0, tileData.Length, tileNumber =>
            {
                var key = GameController.Memory.Read<TgtDetailStruct>
                (GameController.Memory.Read<TgtTileStruct>(tileData[tileNumber].TgtFilePtr)
                   .TgtDetailPtr).name.ToString(GameController.Memory);
                if (string.IsNullOrEmpty(key))
                    return;
                var stdTuple2D = new Vector2i(
                    (int)(tileNumber % _terrainMetadata.NumCols) * TileStructure.TileToGridConversion,
                    (int)(tileNumber / _terrainMetadata.NumCols) * TileStructure.TileToGridConversion);

                ret.GetOrAdd(key, _ => new ConcurrentQueue<Vector2i>()).Enqueue(stdTuple2D);
            });
            return ret.ToDictionary(k => k.Key, k => k.Value.ToList());
        }

        private unsafe int[][] GenerateMapTextureAndTerrainData()
        {
            var gridHeightData = _heightData;
            var mapTextureData = _rawTerrainData;
            var bytesPerRow = _terrainMetadata.BytesPerRow;
            var totalRows = mapTextureData.Length / bytesPerRow;
            var processedTerrainData = new int[totalRows][];
            var xSize = bytesPerRow * 2;
            for (var i = 0; i < totalRows; i++)
            {
                processedTerrainData[i] = new int[xSize];
            }

            using var image = new Image<Rgba32>(xSize, totalRows);
            if (Settings.DrawHeightMap)
            {
                var minHeight = gridHeightData.Min(x => x.Min());
                var maxHeight = gridHeightData.Max(x => x.Max());
                image.Mutate(c => c.ProcessPixelRowsAsVector4((row, i) =>
                {
                    for (var x = 0; x < row.Length - 1; x += 2)
                    {
                        var cellData = gridHeightData[i.Y][x];
                        var rawTerrainType = mapTextureData[i.Y * bytesPerRow + x / 2];
                        for (var x_s = 0; x_s < 2; ++x_s)
                        {
                            var rawPathType = rawTerrainType >> 4 * x_s & 15;
                            processedTerrainData[i.Y][x + x_s] = rawPathType;
                            row[x + x_s] = new Vector4(0, (cellData - minHeight) / (maxHeight - minHeight), 0, 1);
                        }
                    }
                }));
            }
            else
            {
                var unwalkableMask = Vector4.One;
                var walkableMask = Vector4.UnitY;
                Parallel.For(0, totalRows, y =>
                {
                    for (var x = 0; x < xSize; x += 2)
                    {
                        var cellData = gridHeightData[y][x];

                        //basically, offset x and y by half the offset z would cause when rendering in 3d
                        var heightOffset = (int)(cellData / GridToWorldMultiplier / 2);
                        var offsetX = x + heightOffset;
                        var offsetY = y + heightOffset;
                        var rawTerrainType = mapTextureData[y * bytesPerRow + x / 2];
                        for (var xs = 0; xs < 2; xs++)
                        {
                            var rawPathType = rawTerrainType >> 4 * xs & 15;
                            processedTerrainData[y][x + xs] = rawPathType;
                            var offsetXa = offsetX + xs;
                            if (offsetXa >= 0 && offsetXa < xSize && offsetY >= 0 && offsetY < totalRows)
                            {
                                image[offsetXa, offsetY] = new Rgba32(rawPathType is 0 ? unwalkableMask : walkableMask);
                            }
                        }
                    }
                });
                Parallel.For(1, totalRows - 1, y =>
                {
                    for (var x = 1; x < xSize - 1; x++)
                    {
                        //this fills in the blanks that are left over from the height projection
                        if (image[x, y].ToVector4() == Vector4.Zero)
                        {
                            var countWalkable = 0;
                            var countUnwalkable = 0;
                            for (var xO = -1; xO < 2; xO++)
                            {
                                for (var yO = -1; yO < 2; yO++)
                                {
                                    var nPixel = image[x + xO, y + yO].ToVector4();
                                    if (nPixel == walkableMask)
                                        countWalkable++;
                                    else if (nPixel == unwalkableMask)
                                        countUnwalkable++;
                                }
                            }

                            image[x, y] = new Rgba32(countWalkable > countUnwalkable ? walkableMask : unwalkableMask);
                        }
                    }
                });

                var edgeDetector = new EdgeDetectorProcessor(new EdgeDetectorKernel(new DenseMatrix<float>(new float[,]
                    {
                        { -1, -1, -1, -1, -1 },
                        { -1, -1, -1, -1, -1 },
                        { -1, -1, 24, -1, -1 },
                        { -1, -1, -1, -1, -1 },
                        { -1, -1, -1, -1, -1 },
                    })), false)
                   .CreatePixelSpecificProcessor(Configuration.Default, image, image.Bounds());
                edgeDetector.Execute();
                image.Mutate(c => c.ProcessPixelRowsAsVector4(row =>
                {
                    for (var i = 0; i < row.Length; i++)
                    {
                        row[i] = row[i] switch
                        {
                            { X: 1 } => new Vector4(150f) / byte.MaxValue,
                            { X: 0 } => Vector4.Zero,
                            var s => s
                        };
                    }
                }));
            }

            image.TryGetSinglePixelSpan(out var span);
            var width = image.Width;
            var height = image.Height;
            var bytesPerPixel = image.PixelType.BitsPerPixel / 8;
            fixed (Rgba32* rgba32Ptr = &MemoryMarshal.GetReference<Rgba32>(span))
            {
                var rect = new DataRectangle(new IntPtr(rgba32Ptr), width * bytesPerPixel);

                using var tex2D = new Texture2D(Graphics.LowLevel.D11Device,
                    new Texture2DDescription
                    {
                        Width = width,
                        Height = height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = Format.R8G8B8A8_UNorm,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.ShaderResource,
                        CpuAccessFlags = CpuAccessFlags.None,
                        OptionFlags = ResourceOptionFlags.None
                    }, rect);

                var shaderResourceView = new ShaderResourceView(Graphics.LowLevel.D11Device, tex2D);
                Graphics.LowLevel.AddOrUpdateTexture(TextureName, shaderResourceView);
                _areaDimensions = new Vector2i(width, height);
            }

            return processedTerrainData;
        }

        private Vector2 GetPlayerPosition()
        {
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            var playerPositionComponent = player.GetComponent<Positioned>();
            if (playerPositionComponent == null)
                return new Vector2(0, 0);
            var playerPosition = new Vector2(playerPositionComponent.GridX, playerPositionComponent.GridY);
            return playerPosition;
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
                    v += mult * vector.ToVector2();
                    count += mult;
                }

                v /= count;
                var replacement = tileGroup.Select(tile => new Vector2i(tile.First.X, tile.First.Y))
                   .Where(IsGridWalkable)
                   .OrderBy(x => (x.ToVector2() - v).LengthSquared())
                   .Select(x => (Vector2i?)x)
                   .FirstOrDefault();
                if (replacement != null)
                {
                    v = replacement.Value.ToVector2();
                }

                if (!IsGridWalkable(v.Truncate()))
                {
                    v = GetAllNeighborTiles(v.Truncate()).First(IsGridWalkable).ToVector2();
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

        public override void Render()
        {
            var ingameUi = GameController.Game.IngameState.IngameUi;
            if (ingameUi.DelveWindow.IsVisible ||
                ingameUi.AtlasPanel.IsVisible ||
                ingameUi.SellWindow.IsVisible ||
                ingameUi.NewSellWindow.IsVisible ||
                ingameUi.HeistLockerWindow.IsVisible)
            {
                return;
            }

            var map = ingameUi.Map;
            var largeMap = map.LargeMap;
            if (largeMap.IsVisible)
            {
                var mapCenter = largeMap.GetClientRect().TopLeft + largeMap.Shift + largeMap.DefaultShift;
                //I have ABSOLUTELY NO IDEA where 677 comes from, but it works perfectly in all configurations I was able to test. Aspect ratio doesn't matter, just camera height
                _mapScale = GameController.IngameState.Camera.Height / 677f * largeMap.Zoom * Settings.CustomScale;
                DrawLargeMap(mapCenter);
                DrawTargets(mapCenter);
            }
        }

        private Vector2 TranslateGridDeltaToMapDelta(Vector2 delta, float deltaZ)
        {
            deltaZ /= GridToWorldMultiplier; //z is normally "world" units, translate to grid
            return (float)_mapScale * new Vector2((delta.X - delta.Y) * CameraAngleCos, (deltaZ - (delta.X + delta.Y)) * CameraAngleSin);
        }

        private void DrawLargeMap(Vector2 mapCenter)
        {
            if (!Settings.DrawWalkableMap || !Graphics.LowLevel.HasTexture(TextureName) || _areaDimensions == null)
                return;
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            var playerRender = player.GetComponent<ExileCore.PoEMemory.Components.Render>();
            if (playerRender == null)
                return;
            var rectangleF = new RectangleF(-playerRender.GridPos.X, -playerRender.GridPos.Y, _areaDimensions.Value.X, _areaDimensions.Value.Y);
            var playerHeight = -playerRender.RenderStruct.Height;
            var p1 = mapCenter + TranslateGridDeltaToMapDelta(new Vector2(rectangleF.Left, rectangleF.Top), playerHeight);
            var p2 = mapCenter + TranslateGridDeltaToMapDelta(new Vector2(rectangleF.Right, rectangleF.Top), playerHeight);
            var p3 = mapCenter + TranslateGridDeltaToMapDelta(new Vector2(rectangleF.Right, rectangleF.Bottom), playerHeight);
            var p4 = mapCenter + TranslateGridDeltaToMapDelta(new Vector2(rectangleF.Left, rectangleF.Bottom), playerHeight);
            Graphics.DrawTexture(Graphics.LowLevel.GetTexture(TextureName).NativePointer, p1, p2, p3, p4);
        }

        private void DrawTargets(Vector2 mapCenter)
        {
            var col = Settings.TargetNameColor.Value;
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            var playerRender = player.GetComponent<ExileCore.PoEMemory.Components.Render>();
            if (playerRender == null)
                return;
            var playerPosition = new Vector2(playerRender.GridPos.X, playerRender.GridPos.Y);
            var playerHeight = -playerRender.RenderStruct.Height;
            var ithElement = 0;
            if (Settings.ShowPathsToTargets && (Settings.ShowAllTargets || Settings.ShowSelectedTargets))
            {
                foreach (var route in _routes.Values)
                {
                    ithElement++;
                    ithElement %= 5;
                    foreach (var elem in route.Path.Skip(ithElement).GetEveryNth(5))
                    {
                        var mapDelta = TranslateGridDeltaToMapDelta(new Vector2(elem.X, elem.Y) - playerPosition, playerHeight - _heightData[elem.Y][elem.X]);
                        Graphics.DrawBox(mapCenter + mapDelta - new Vector2(2, 2), mapCenter + mapDelta + new Vector2(2, 2), Settings.DefaultPathColor);
                    }
                }
            }

            if (Settings.ShowAllTargets)
            {
                foreach (var (tileName, targetList) in _allTargetLocations)
                {
                    var textOffset = (Graphics.MeasureText(tileName) / 2f).ToSdx();
                    foreach (var vector in targetList)
                    {
                        var mapDelta = TranslateGridDeltaToMapDelta(vector.ToVector2() - playerPosition, playerHeight - _heightData[vector.Y][vector.X]);
                        if (Settings.EnableTargetNameBackground)
                            Graphics.DrawBox(mapCenter + mapDelta - textOffset, mapCenter + mapDelta + textOffset, Color.Black);
                        Graphics.DrawText(tileName, mapCenter + mapDelta - textOffset, col);
                    }
                }
            }
            else if (Settings.ShowSelectedTargets)
            {
                foreach (var (name, description) in _clusteredTargetLocations)
                {
                    foreach (var clusterPosition in description.Locations)
                    {
                        float clusterHeight = 0;
                        if (clusterPosition.X < _heightData[0].Length && clusterPosition.Y < _heightData.Length)
                            clusterHeight = _heightData[(int)clusterPosition.Y][(int)clusterPosition.X];
                        var text = string.IsNullOrWhiteSpace(description.DisplayName) ? name : description.DisplayName;
                        var textOffset = (Graphics.MeasureText(text) / 2f).ToSdx();
                        var mapDelta = TranslateGridDeltaToMapDelta(clusterPosition - playerPosition, playerHeight - clusterHeight);
                        if (Settings.EnableTargetNameBackground)
                            Graphics.DrawBox(mapCenter + mapDelta - textOffset, mapCenter + mapDelta + textOffset, Color.Black);
                        Graphics.DrawText(text, mapCenter + mapDelta - textOffset, col);
                    }
                }
            }
        }
    }
}
