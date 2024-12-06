using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using GameOffsets2.Native;

namespace Radar;

public class PathFinder
{
    private readonly bool[][] _grid;

    //target->current->next
    private readonly ConcurrentDictionary<Vector2i, Dictionary<Vector2i, float>> ExactDistanceField = new();
    private readonly ConcurrentDictionary<Vector2i, byte[][]> DirectionField = new();
    private readonly int _dimension2;
    private readonly int _dimension1;

    public PathFinder(int[][] grid, int[] pathableValues)
    {
        var pv = pathableValues.ToHashSet();
        _grid = grid.Select(x => x.Select(y => pv.Contains(y)).ToArray()).ToArray();
        _dimension1 = _grid.Length;
        _dimension2 = _grid[0].Length;
    }

    private bool IsTilePathable(Vector2i tile)
    {
        if (tile.X < 0 || tile.X >= _dimension2)
        {
            return false;
        }

        if (tile.Y < 0 || tile.Y >= _dimension1)
        {
            return false;
        }

        return _grid[tile.Y][tile.X];
    }

    private static readonly List<Vector2i> NeighborOffsets = new List<Vector2i>
    {
        new Vector2i(0, 1),
        new Vector2i(1, 1),
        new Vector2i(1, 0),
        new Vector2i(1, -1),
        new Vector2i(0, -1),
        new Vector2i(-1, -1),
        new Vector2i(-1, 0),
        new Vector2i(-1, 1),
    };

    private static IEnumerable<Vector2i> GetNeighbors(Vector2i tile)
    {
        return NeighborOffsets.Select(offset => tile + offset);
    }

    private static float GetExactDistance(Vector2i tile, Dictionary<Vector2i, float> dict)
    {
        return dict.GetValueOrDefault(tile, float.PositiveInfinity);
    }

    public IEnumerable<List<Vector2i>> RunFirstScan(Vector2i start, Vector2i target)
    {
        if (DirectionField.ContainsKey(target))
        {
            yield break;
        }

        if (!ExactDistanceField.TryAdd(target, new Dictionary<Vector2i, float>()))
        {
            yield break;
        }

        var exactDistanceField = ExactDistanceField[target];
        exactDistanceField[target] = 0;
        var localBacktrackDictionary = new Dictionary<Vector2i, Vector2i>();
        var queue = new BinaryHeap<float, Vector2i>();
        queue.Add(0, target);

        void TryEnqueueTile(Vector2i coord, Vector2i previous, float previousScore)
        {
            if (!IsTilePathable(coord))
            {
                return;
            }

            if (localBacktrackDictionary.ContainsKey(coord))
            {
                return;
            }

            localBacktrackDictionary.Add(coord, previous);
            var exactDistance = previousScore + coord.DistanceF(previous);
            exactDistanceField.TryAdd(coord, exactDistance);
            queue.Add(exactDistance, coord);
        }

        var sw = Stopwatch.StartNew();

        localBacktrackDictionary.Add(target, target);
        var reversePath = new List<Vector2i>();
        while (queue.TryRemoveTop(out var top))
        {
            var current = top.Value;
            var currentDistance = top.Key;
            if (reversePath.Count == 0 && current.Equals(start))
            {
                reversePath.Add(current);
                var it = current;
                while (it != target && localBacktrackDictionary.TryGetValue(it, out var previous))
                {
                    reversePath.Add(previous);
                    it = previous;
                }

                yield return reversePath;
            }

            foreach (var neighbor in GetNeighbors(current))
            {
                TryEnqueueTile(neighbor, current, currentDistance);
            }

            if (sw.ElapsedMilliseconds > 100)
            {
                yield return reversePath;
                sw.Restart();
            }
        }

        localBacktrackDictionary.Clear();

        if (_dimension1 * _dimension2 < exactDistanceField.Count * (sizeof(int) * 2 + Unsafe.SizeOf<Vector2i>() + Unsafe.SizeOf<float>()))
        {
            var directionGrid = _grid
                .AsParallel().AsOrdered().Select((r, y) => r.Select((_, x) =>
                {
                    var coordVec = new Vector2i(x, y);
                    if (float.IsPositiveInfinity(GetExactDistance(coordVec, exactDistanceField)))
                    {
                        return (byte)0;
                    }

                    var neighbors = GetNeighbors(coordVec);
                    var (closestNeighbor, clndistance) = neighbors.Select(n => (n, distance: GetExactDistance(n, exactDistanceField))).MinBy(p => p.distance);
                    if (float.IsPositiveInfinity(clndistance))
                    {
                        return (byte)0;
                    }

                    var bestDirection = closestNeighbor - coordVec;
                    return (byte)(1 + NeighborOffsets.IndexOf(bestDirection));
                }).ToArray())
                .ToArray();

            DirectionField[target] = directionGrid;
            ExactDistanceField.TryRemove(target, out _);
        }
    }

    public List<Vector2i> FindPath(Vector2i start, Vector2i target)
    {
        if (DirectionField.GetValueOrDefault(target) is { } directionField)
        {
            if (directionField[start.Y][start.X] == 0)
                return null;
            var path = new List<Vector2i>();
            var current = start;
            while (current != target)
            {
                var directionIndex = directionField[current.Y][current.X];
                if (directionIndex == 0)
                {
                    return null;
                }

                var next = NeighborOffsets[directionIndex - 1] + current;
                path.Add(next);
                current = next;
            }

            return path;
        }
        else
        {
            var exactDistanceField = ExactDistanceField[target];
            if (float.IsPositiveInfinity(GetExactDistance(start, exactDistanceField))) return null;
            var path = new List<Vector2i>();
            var current = start;
            while (current != target)
            {
                var next = GetNeighbors(current).MinBy(x => GetExactDistance(x, exactDistanceField));
                path.Add(next);
                current = next;
            }

            return path;
        }
    }
}
