using System.Collections.Generic;
using GameOffsets.Native;

namespace Radar;

public interface IPathFinder
{
    IEnumerable<List<Vector2i>> RunFirstScan(Vector2i start, Vector2i target);
    List<Vector2i> FindPath(Vector2i start, Vector2i target);
}