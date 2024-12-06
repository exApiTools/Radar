using System;
using System.Collections.Generic;
using System.Drawing;
using GameOffsets2.Native;

namespace Radar;

public class RouteDescription
{
    public List<Vector2i> Path { get; set; }
    public Func<Color> MapColor { get; set; }
    public Func<Color> WorldColor { get; set; }
}
