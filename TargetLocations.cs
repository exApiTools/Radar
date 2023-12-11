using System.Numerics;

namespace Radar;

public class TargetLocations
{
    public string DisplayName => string.IsNullOrWhiteSpace(Target.DisplayName) ? Target.Name : Target.DisplayName;
    public Vector2[] Locations { get; set; }
    public TargetDescription Target { get; set; }
}
