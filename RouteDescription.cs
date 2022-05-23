using System.Collections.Generic;
using GameOffsets.Native;
using SharpDX;

namespace Radar;

public class RouteDescription
{
    public List<Vector2i> Path { get; set; }
    public Color Color { get; set; }
}
