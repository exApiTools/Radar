using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using GameOffsets.Native;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace Radar;

public class JpsPathFinder : IPathFinder
{
    private readonly bool[][] _grid;
    private readonly int _dimension2;
    private readonly int _dimension1;

    public JpsPathFinder(int[][] grid, int[] pathableValues)
    {
        var pv = pathableValues.ToHashSet();
        _grid = grid.Select(x => x.Select(y => pv.Contains(y)).ToArray()).ToArray();
        _dimension2 = _grid.Length;
        _dimension1 = _grid[0].Length;
    }

    bool IsEmpty(int r, int c) => c >= 0 && r >= 0 && r < _dimension1 && c < _dimension2 && _grid[c][r];
    bool IsWall(int r, int c) => !IsEmpty(r, c);
    bool IsWall(Vector2i v) => IsWall(v.X, v.Y);
    bool IsEmpty(Vector2i v) => IsEmpty(v.X, v.Y);

    bool IsJumpPoint(int r, int c, int rowDir, int colDir)
    {
        return
            IsEmpty(r - rowDir, c - colDir) && // Parent not a wall (not necessary)
            ((IsEmpty(r + colDir, c + rowDir) && // 1st forced neighbor
              IsWall(r - rowDir + colDir, c - colDir + rowDir)) || // 1st forced neighbor (continued)
             (IsEmpty(r - colDir, c - rowDir) && // 2nd forced neighbor
              IsWall(r - rowDir - colDir, c - colDir - rowDir))); // 2nd forced neighbor (continued)
    }

    bool IsJumpPoint(Vector2i v, Vector2i d)
    {
        var sw = new Vector2i(d.Y, d.X);
        return IsEmpty(v - d) &&
               ((IsEmpty(v + sw) && IsWall(v - d + sw)) ||
                (IsEmpty(v - sw) && IsWall(v - d - sw)));
    }

    private struct CardinalDirections
    {
        public bool Right;
        public bool Left;
        public bool Top;
        public bool Down;
    }

    private struct AllDirections
    {
        public int Right;
        public int Left;
        public int Top;
        public int Down;
        public int TopRight;
        public int TopLeft;
        public int DownRight;
        public int DownLeft;
    }

    bool Extract(in CardinalDirections d, Vector2i direction)
    {
        return direction switch
        {
            { X: 1, Y: 0 } => d.Right,
            { X: -1, Y: 0 } => d.Left,
            { X: 0, Y: 1 } => d.Top,
            { X: 0, Y: -1 } => d.Down,
        };
    }

    void Set(ref CardinalDirections d, Vector2i direction, bool value)
    {
        bool td = false;
        ref bool t = ref td;
        switch (direction)
        {
            case { X: 1, Y: 0 }:
                t = ref d.Right;
                break;
            case { X: -1, Y: 0 }:
                t = ref d.Left;
                break;
            case { X: 0, Y: 1 }:
                t = ref d.Top;
                break;
            case { X: 0, Y: -1 }:
                t = ref d.Down;
                break;
        }

        t = value;
    }

    int Extract(in AllDirections d, Vector2i direction)
    {
        return direction switch
        {
            { X: 1, Y: 0 } => d.Right,
            { X: -1, Y: 0 } => d.Left,
            { X: 0, Y: 1 } => d.Top,
            { X: 0, Y: -1 } => d.Down,
            { X: 1, Y: 1 } => d.TopRight,
            { X: -1, Y: 1 } => d.TopLeft,
            { X: 1, Y: -1 } => d.DownRight,
            { X: -1, Y: -1 } => d.DownLeft,
        };
    }

    void Set(ref AllDirections d, Vector2i direction, int value)
    {
        int td = 0;
        ref int t = ref td;
        switch (direction)
        {
            case { X: 1, Y: 0 }:
                t = ref d.Right;
                break;
            case { X: -1, Y: 0 }:
                t = ref d.Left;
                break;
            case { X: 0, Y: 1 }:
                t = ref d.Top;
                break;
            case { X: 0, Y: -1 }:
                t = ref d.Down;
                break;
            case { X: 1, Y: 1 }:
                t = ref d.TopRight;
                break;
            case { X: -1, Y: 1 }:
                t = ref d.TopLeft;
                break;
            case { X: 1, Y: -1 }:
                t = ref d.DownRight;
                break;
            case { X: -1, Y: -1 }:
                t = ref d.DownLeft;
                break;
        }

        t = value;
    }

    private static readonly Dictionary<Vector2i, List<Vector2i>> DirectionMap = new()
    {
        [new Vector2i(0, 1)] = new() { new(0, 1), new(1, 1), new(-1, 1) },
        [new Vector2i(0, -1)] = new() { new(0, -1), new(1, -1), new(-1, -1) },
        [new Vector2i(1, 0)] = new() { new(1, 0), new(1, 1), new(1, -1) },
        [new Vector2i(-1, 0)] = new() { new(-1, 0), new(-1, 1), new(-1, -1) },
        [new Vector2i(1, 1)] = new() { new(1, 1), new(1, 0), new(0, 1), new(-1, 1), new(1, -1) },
        [new Vector2i(-1, 1)] = new() { new(-1, 1), new(-1, 0), new(0, 1), new(1, 1), new(-1, -1) },
        [new Vector2i(-1, -1)] = new() { new(-1, -1), new(-1, 0), new(0, -1), new(1, -1), new(-1, 1) },
        [new Vector2i(1, -1)] = new() { new(1, -1), new(1, 0), new(0, -1), new(1, 1), new(-1, -1), },
        [new Vector2i(0, 0)] = new() { new(0, 1), new(1, 1), new(1, 0), new(1, -1), new(0, -1), new(-1, -1), new(-1, 0), new(-1, 1) },
    };

    private static bool IsSameDirection(Vector2i a, Vector2i b)
    {
        return (Math.Sign(a.X), Math.Sign(a.Y)) == (Math.Sign(b.X), Math.Sign(b.Y));
    }

    private static Vector2i GetDirection(Vector2i step)
    {
        return new Vector2i(Math.Sign(step.X), Math.Sign(step.Y));
    }

    private static int MaxAbs(Vector2i v)
    {
        return Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
    }

    private static int MinAbs(Vector2i v)
    {
        return Math.Min(Math.Abs(v.X), Math.Abs(v.Y));
    }

    private static bool IsCardinal(Vector2i v)
    {
        return v.X == 0 || v.Y == 0;
    }

    private static readonly float sqrt2 = MathF.Sqrt(2);
    private AllDirections[][] _distanceField;

    public IEnumerable<List<Vector2i>> RunFirstScan(Vector2i start, Vector2i target)
    {
        _distanceField = GetDistanceField();
        yield return FindPath(start, target);
    }

    public List<Vector2i> FindPath(Vector2i start, Vector2i target)
    {
        var path = new List<Vector2i>();
        var queue = new BinaryHeap<float, (Vector2i Coord, Vector2i Parent, float GivenCost)>();
        var localBacktrackDictionary = new Dictionary<Vector2i, Vector2i> { [start] = start };

        queue.Add(Heur(start, target), (start, start, 0));
        while (queue.TryRemoveTop(out var top))
        {
            var coord = top.Value.Coord;
            if (coord == target)
            {
                var prev = localBacktrackDictionary[coord];
                do
                {
                    var diff = prev - coord;
                    var dir = GetDirection(diff);
                    var steps = MaxAbs(diff);
                    path.AddRange(Enumerable.Range(0, steps).Select(i => coord + dir * i));
                    coord = prev;
                    prev = localBacktrackDictionary[prev];
                } while (coord != start);

                path.Add(start);
                path.Reverse();
                return path;
            }

            var diffToTarget = target - coord;
            var previousDirection = GetDirection(coord - top.Value.Parent);
            foreach (var newDirection in DirectionMap[previousDirection])
            {
                Vector2i? successor = null;
                var isCardinal = IsCardinal(newDirection);
                var jumpPointDistance = Extract(_distanceField[coord.X][coord.Y], newDirection);
                if (isCardinal &&
                    IsSameDirection(newDirection, diffToTarget) &&
                    MaxAbs(diffToTarget) <= Math.Abs(jumpPointDistance))
                {
                    successor = target;
                }
                else if (!isCardinal &&
                         IsSameDirection(newDirection, diffToTarget) &&
                         MinAbs(diffToTarget) <= Math.Abs(jumpPointDistance))
                {
                    var minAbs = MinAbs(diffToTarget);
                    successor = coord + newDirection * minAbs;
                }
                else if (jumpPointDistance > 0)
                {
                    successor = coord + newDirection * jumpPointDistance;
                }
                //else
                //if (jumpPointDistance != 0)
                //{
                //    successor = coord + newDirection;
                //}

                if (successor != null && localBacktrackDictionary.TryAdd(successor.Value, coord))
                {
                    var givenCost = top.Value.GivenCost + (isCardinal ? 1 : sqrt2) * MaxAbs(successor.Value - coord);
                    var f = Heur(successor.Value, target) + givenCost;
                    queue.Add(f, (successor.Value, coord, givenCost));
                }
            }
        }

        return null;
    }

    private static float Heur(Vector2i a, Vector2i b)
    {
        var xDiff = Math.Abs(a.X - b.X);
        var yDiff = Math.Abs(a.Y - b.Y);
        var (min, max) = xDiff < yDiff ? (xDiff, yDiff) : (yDiff, xDiff);
        return max - min + min * sqrt2;
    }

    private AllDirections[][] GetDistanceField()
    {
        var jp = GetJumpPoints();
        var distance = Enumerable.Repeat(0, _dimension1).Select(_ => Enumerable.Repeat(0, _dimension2).Select(_ => new AllDirections()).ToArray()).ToArray();

        for (int y = 0; y < _dimension2; ++y)
        {
            int count = -1;
            bool jumpPointLastSeen = false;
            for (int x = 0; x < _dimension1; ++x)
            {
                count = ProcessCardinal(x, y, count, distance, new Vector2i(-1, 0), jp, ref jumpPointLastSeen);
            }
        }

        for (int y = 0; y < _dimension2; ++y)
        {
            int count = -1;
            bool jumpPointLastSeen = false;
            for (int x = _dimension1 - 1; x >= 0; --x)
            {
                count = ProcessCardinal(x, y, count, distance, new Vector2i(1, 0), jp, ref jumpPointLastSeen);
            }
        }

        for (int x = 0; x < _dimension1; ++x)
        {
            int count = -1;
            bool jumpPointLastSeen = false;
            for (int y = 0; y < _dimension2; ++y)
            {
                count = ProcessCardinal(x, y, count, distance, new Vector2i(0, -1), jp, ref jumpPointLastSeen);
            }
        }

        for (int x = 0; x < _dimension1; ++x)
        {
            int count = -1;
            bool jumpPointLastSeen = false;
            for (int y = _dimension2 - 1; y >= 0; --y)
            {
                count = ProcessCardinal(x, y, count, distance, new Vector2i(0, 1), jp, ref jumpPointLastSeen);
            }
        }

        for (int x = 0; x < _dimension1; ++x)
        {
            for (int y = 0; y < _dimension2; ++y)
            {
                ProcessDiagonal(x, y, new Vector2i(-1, -1), distance);
            }

            for (int y = _dimension2 - 1; y >= 0; --y)
            {
                ProcessDiagonal(x, y, new Vector2i(-1, 1), distance);
            }
        }

        for (int x = _dimension1 - 1; x >= 0; --x)
        {
            for (int y = 0; y < _dimension2; ++y)
            {
                ProcessDiagonal(x, y, new Vector2i(1, -1), distance);
            }

            for (int y = _dimension2 - 1; y >= 0; --y)
            {
                ProcessDiagonal(x, y, new Vector2i(1, 1), distance);
            }
        }

        var configuration = Configuration.Default.Clone();
        configuration.PreferContiguousImageBuffers = true;
        using var image = new Image<Rgba32>(configuration, _dimension1, _dimension2);
        image.Mutate(i => i.ProcessPixelRowsAsVector4((r, i) =>
        {
            Span<AllDirections> d = stackalloc AllDirections[1];
            for (int j = 0; j < r.Length; j++)
            {
                var z = distance[j][i.Y];
                d[0] = z;
                var fs = MemoryMarshal.Cast<AllDirections, int>(d).ToArray();
                var min = fs.Where(x => x > 0).DefaultIfEmpty(0).Min();
                r[j] = IsWall(j, i.Y) ? new Vector4(0, 0, 1, 1) : new Vector4(min == 0 ? 1 : 0, 1f / min, 0, 1);
            }
        }));
        image.SaveAsBmp($"{Guid.NewGuid()}.bmp");
        return distance;
    }

    private void ProcessDiagonal(int x, int y, Vector2i direction, AllDirections[][] distance)
    {
        var coord = new Vector2i(x, y);
        if (IsWall(coord)) return;
        var (v1, v2) = Split(direction);
        if (IsWall(coord + direction))
        {
            //Wall one away
            Set(ref distance[x][y], direction, 0);
        }
        else if (Extract(distance[x + direction.X][y + direction.Y], v1) > 0 ||
                 Extract(distance[x + direction.X][y + direction.Y], v2) > 0)
        {
            //Straight jump point one away
            Set(ref distance[x][y], direction, 1);
        }
        else
        {
            //Increment from last
            int jumpDistance = Extract(distance[x + direction.X][y + direction.Y], direction);
            if (jumpDistance > 0)
            {
                Set(ref distance[x][y], direction, 1 + jumpDistance);
            }
            else
            {
                Set(ref distance[x][y], direction, -1 + jumpDistance);
            }
        }
    }

    private static (Vector2i, Vector2i) Split(Vector2i v)
    {
        return (new Vector2i(0, v.Y), new Vector2i(v.X, 0));
    }

    private int ProcessCardinal(int x, int y, int count, AllDirections[][] distance, Vector2i direction, CardinalDirections[][] jp, ref bool jumpPointLastSeen)
    {
        if (IsWall(x, y))
        {
            jumpPointLastSeen = false;
            Set(ref distance[x][y], direction, 0);
            return -1;
        }

        count++;
        if (jumpPointLastSeen)
        {
            Set(ref distance[x][y], direction, count);
        }
        else //Wall last seen
        {
            Set(ref distance[x][y], direction, -count);
        }

        if (Extract(jp[x][y], direction))
        {
            count = 0;
            jumpPointLastSeen = true;
        }

        return count;
    }

    private CardinalDirections[][] GetJumpPoints()
    {
        var jp = Enumerable.Repeat(0, _dimension1).Select(_ => Enumerable.Repeat(0, _dimension2).Select(_ => new CardinalDirections()).ToArray()).ToArray();
        var v1 = new Vector2i(1, 0);
        var v2 = new Vector2i(-1, 0);
        var v3 = new Vector2i(0, 1);
        var v4 = new Vector2i(0, -1);
        for (int x = 0; x < _dimension1; ++x)
        {
            for (int y = 0; y < _dimension2; ++y)
            {
                var v = new Vector2i(x, y);
                if (IsEmpty(v))
                {
                    if (IsJumpPoint(v, v1))
                    {
                        Set(ref jp[x][y], v1, true);
                    }

                    if (IsJumpPoint(v, v2))
                    {
                        Set(ref jp[x][y], v2, true);
                    }

                    if (IsJumpPoint(v, v3))
                    {
                        Set(ref jp[x][y], v3, true);
                    }

                    if (IsJumpPoint(v, v4))
                    {
                        Set(ref jp[x][y], v4, true);
                    }
                }
            }
        }

        return jp;
    }
}