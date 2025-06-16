using System.Drawing;
using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Helpers;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;

namespace Radar;

[Submenu]
public class DebugSettings
{
    public ToggleNode DrawHeightMap { get; set; } = new ToggleNode(false);
    public ToggleNode DisableHeightAdjust { get; set; } = new ToggleNode(false);
    public ToggleNode AlternativeEdgeMethod { get; set; } = new ToggleNode(false);
    [ConditionalDisplay(nameof(AlternativeEdgeMethod))]
    public AlternativeEdge AlternativeEdgeSettings { get; set; } = new AlternativeEdge();
    [ConditionalDisplay(nameof(AlternativeEdgeMethod), false)]
    public CurrentEdge StandardEdgeSettings { get; set; } = new CurrentEdge();
    public ToggleNode DisableDrawRegionLimiting { get; set; } = new ToggleNode(false);
    public ToggleNode IgnoreFullscreenPanels { get; set; } = new ToggleNode(false);
    public ToggleNode RenderInPeacefulZones { get; set; } = new ToggleNode(true);
    public ToggleNode IgnoreLargePanels { get; set; } = new ToggleNode(false);
    public RangeNode<int> MapCenterOffsetX { get; set; } = new RangeNode<int>(0, -1000, 1000);
    public RangeNode<int> MapCenterOffsetY { get; set; } = new RangeNode<int>(0, -1000, 1000);
}

[Submenu]
public class WorldPathSettings
{
    public ToggleNode ShowPathsToTargets { get; set; } = new ToggleNode(true);
    public ToggleNode ShowPathsToTargetsOnlyWithClosedMap { get; set; } = new ToggleNode(true);
    public ToggleNode UseRainbowColorsForPaths { get; set; } = new ToggleNode(true);
    public ColorNode DefaultPathColor { get; set; } = new ColorNode(Color.Red.ToSharpDx());
    public ToggleNode OffsetPaths { get; set; } = new ToggleNode(true);
    public RangeNode<float> PathThickness { get; set; } = new RangeNode<float>(1, 1, 20);
    public RangeNode<int> DrawEveryNthSegment { get; set; } = new RangeNode<int>(1, 1, 10);
}

[Submenu]
public class AlternativeEdge
{
    public RangeNode<float> OutlineBlurSigma { get; set; } = new RangeNode<float>(0.438f, 0f, 20f);
    public RangeNode<float> OutlineTransitionThreshold { get; set; } = new RangeNode<float>(0.070f, 0f, 1f);
    public RangeNode<float> OutlineFeatherWidth { get; set; } = new RangeNode<float>(0.070f, 0f, 1f);
}

[Submenu]
public class CurrentEdge
{
    public ToggleNode SkipNeighborFill { get; set; } = new ToggleNode(false);
    public ToggleNode SkipEdgeDetector { get; set; } = new ToggleNode(false);
    public ToggleNode SkipRecoloring { get; set; } = new ToggleNode(false);
}

[Submenu]
public class PathfindingSettings
{
    [Menu(null, "For debugging only")]
    [JsonIgnore]
    public TextNode CurrentZoneName { get; set; } = new TextNode("<unknown>");

    [Menu(null, "For debugging only")]
    [JsonIgnore]
    public TextNode TargetNameFilter { get; set; } = new TextNode("");

    public ToggleNode ShowPathsToTargetsOnMap { get; set; } = new ToggleNode(true);
    public ColorNode DefaultMapPathColor { get; set; } = new ColorNode(Color.Green.ToSharpDx());
    public ToggleNode UseRainbowColorsForMapPaths { get; set; } = new ToggleNode(true);
    public ToggleNode ShowAllTargets { get; set; } = new ToggleNode(false);

    [Menu(null, "Do not show targets that occur more than X times per zone")]
    [ConditionalDisplay(nameof(ShowAllTargets))]
    public RangeNode<int> MaxTargetNameCount { get; set; } = new RangeNode<int>(10, 1, 100);

    public ToggleNode IncludeTilePathsAsTargets { get; set; } = new ToggleNode(true);
    public ToggleNode ShowSelectedTargets { get; set; } = new ToggleNode(true);
    public ToggleNode EnableTargetNameBackground { get; set; } = new ToggleNode(true);
    public ColorNode TargetNameColor { get; set; } = new ColorNode(Color.Violet.ToSharpDx());
    public WorldPathSettings WorldPathSettings { get; set; } = new WorldPathSettings();
}

public class RadarSettings : ISettings
{
    [JsonIgnore]
    public ButtonNode Reload { get; set; } = new ButtonNode();
    public ToggleNode AutoDumpInstanceOnAreaChange { get; set; } = new ToggleNode(false);
    public HotkeyNode ManuallyDumpInstance { get; set; } = new HotkeyNode(Keys.None);
    public ToggleNode Enable { get; set; } = new ToggleNode(true);
    public RangeNode<float> CustomScale { get; set; } = new RangeNode<float>(1, 0.1f, 10);
    public ToggleNode DrawWalkableMap { get; set; } = new ToggleNode(true);
    public ColorNode TerrainColor { get; set; } = new ColorNode(Color.FromArgb(150, 150, 150, 150).ToSharpDx());
    public RangeNode<int> MaximumMapTextureDimension { get; set; } = new RangeNode<int>(4096, 100, 4096);
    public RangeNode<int> MaximumPathCount { get; set; } = new RangeNode<int>(1000, 0, 1000);
    public PathfindingSettings PathfindingSettings { get; set; } = new PathfindingSettings();
    public DebugSettings Debug { get; set; } = new DebugSettings();
}