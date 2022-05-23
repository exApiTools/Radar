using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;
using SharpDX;

namespace Radar;

public class RadarSettings : ISettings
{
    [JsonIgnore]
    public ButtonNode Reload { get; set; } = new ButtonNode();

    public ToggleNode Enable { get; set; } = new ToggleNode(true);
    public RangeNode<float> CustomScale { get; set; } = new RangeNode<float>(1, 0.1f, 10);
    public ToggleNode DrawWalkableMap { get; set; } = new ToggleNode(true);
    public ToggleNode EnableTargetNameBackground { get; set; } = new ToggleNode(true);
    public ToggleNode ShowAllTargets { get; set; } = new ToggleNode(true);
    public ToggleNode ShowSelectedTargets { get; set; } = new ToggleNode(true);
    public ColorNode TargetNameColor { get; set; } = new ColorNode(Color.Violet);
    public ToggleNode ShowPathsToTargets { get; set; } = new ToggleNode(true);
    public RangeNode<int> MaximumPathCount { get; set; } = new RangeNode<int>(1000, 0, 100);
    public ColorNode DefaultPathColor { get; set; } = new ColorNode(Color.Green);
    public bool DrawHeightMap { get; set; } = false;
}
