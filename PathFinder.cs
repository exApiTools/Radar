using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GameOffsets.Native;

namespace Radar;

public class PathFinder
{
    private readonly bool[][] _grid;

    //target->current->next
    private readonly ConcurrentDictionary<Vector2i, Dictionary<Vector2i, float>> ExactDistanceField = new();
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

    private static readonly IReadOnlyList<Vector2i> NeighborOffsets = new List<Vector2i>
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

    private IEnumerable<Vector2i> GetNeighbors(Vector2i tile)
    {
        foreach (var offset in NeighborOffsets)
        {
            var nTile = tile + offset;
            yield return nTile;
        }
    }

    private float GetExactDistance(Vector2i tile, Dictionary<Vector2i, float> dict)
    {
        return dict.GetValueOrDefault(tile, Single.PositiveInfinity);
    }

    public IEnumerable<List<Vector2i>> RunFirstScan(Vector2i start, Vector2i target)
    {
        ExactDistanceField.TryAdd(target, new Dictionary<Vector2i, float>());
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
        while (queue.TryRemoveTop(out var top))
        {
            var current = top.Value;
            var currentDistance = top.Key;
            if (current.Equals(start))
            {
                var reversePath = new List<Vector2i>
                {
                    current
                };
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
                yield return new List<Vector2i>();
                sw.Restart();
            }
        }
    }

    public List<Vector2i> FindPath(Vector2i start, Vector2i target)
    {
        var exactDistanceField = ExactDistanceField[target];
        if (GetExactDistance(start, exactDistanceField) != float.PositiveInfinity)
        {
            var path = new List<Vector2i>();
            var current = start;
            while (current != target)
            {
                var next = GetNeighbors(current).OrderBy(x => GetExactDistance(x, exactDistanceField)).First();
                Debug.Assert(!path.Contains(next));
                path.Add(next);
                current = next;
            }

            return path;
        }

        return null;
    }
}
