using ImGuiNET;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using GameOffsets;
using GameOffsets.Native;
using System.Windows.Forms;
using Positioned = ExileCore.PoEMemory.Components.Positioned;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Radar;

public partial class Radar : BaseSettingsPlugin<RadarSettings>
{
    private const string TextureName = "radar_minimap";
    public const float GridToWorldMultiplier = PoeMapExtension.TileToWorldConversion / (float)PoeMapExtension.TileToGridConversion;

    private static readonly List<SharpDX.Color> RainbowColors = new List<Color>
    {
        Color.Red,
        Color.LightGreen,
        Color.White,
        Color.Yellow,
        Color.LightBlue,
        Color.Violet,
        Color.Blue,
        Color.Orange,
        Color.Indigo
    }.Select(x => x.ToSharpDx()).ToList();

    private ConcurrentDictionary<string, List<Vector2i>> _allTargetLocations = new();
    private Vector2i? _areaDimensions;
    private ConcurrentDictionary<string, TargetLocations> _clusteredTargetLocations = new();
    private List<(Regex, TargetDescription x)> _currentZoneTargetEntityPaths = new();
    private CancellationTokenSource _findPathsCts = new();
    private float[][] _heightData;
    private ConcurrentDictionary<Vector2i, List<string>> _locationsByPosition = new();
    private int[][] _processedTerrainData;
    private int[][] _processedTerrainTargetingData;
    private Dictionary<string, TargetDescription> _targetDescriptionsInArea = new();
    private SharpDX.RectangleF _rect;
    private ConcurrentDictionary<Vector2, RouteDescription> _routes = new();
    private bool _middleMouseWasDown;
    private ConcurrentDictionary<string, List<Room>> _rooms = [];

    private ConcurrentDictionary<string, List<TargetDescription>> _targetDescriptions = new();

    public override bool Initialise()
    {
        GameController.PluginBridge.SaveMethod("Radar.LookForRoute",
            (Vector2 target, Action<List<Vector2i>> callback, CancellationToken cancellationToken) =>
                AddRoute(target, callback, cancellationToken));
        GameController.PluginBridge.SaveMethod("Radar.ClusterTarget",
            (string targetName, int expectedCount) => ClusterTarget(targetName, null, expectedCount));

        Input.RegisterKey(Settings.InstanceDumpSettings.ManualDumpHotkey.Value);
        Settings.InstanceDumpSettings.ManualDumpHotkey.OnValueChanged += () => { Input.RegisterKey(Settings.InstanceDumpSettings.ManualDumpHotkey.Value); };
        Settings.InstanceDumpSettings.ManualDumpButton.OnPressed += RunDump;
        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        StopPathFinding();
        if (GameController.Game.IsInGameState || GameController.Game.IsEscapeState)
        {
            _targetDescriptionsInArea = GetTargetDescriptionsInArea().DistinctBy(x => x.EqualityId).ToDictionary(x => x.EqualityId);
            _currentZoneTargetEntityPaths = _targetDescriptionsInArea.Values.Where(x => x.TargetType == TargetType.Entity).DistinctBy(x => x.Name).Select(x => (x.Name.ToLikeRegex(), x)).ToList();
            _heightData = GameController.IngameState.Data.RawTerrainHeightData;
            _allTargetLocations = GetTargets();
            _rooms = GetRooms();
            _locationsByPosition = new ConcurrentDictionary<Vector2i, List<string>>(_allTargetLocations
                .SelectMany(x => x.Value.Select(y => (x.Key, y)))
                .ToLookup(x => x.y, x => x.Key)
                .ToDictionary(x => x.Key, x => x.ToList()));

            _areaDimensions = GameController.IngameState.Data.AreaDimensions;
            _processedTerrainData = Settings.ClearTriggerableBlockades ? GameController.IngameState.Data.GetClearedPathfindingData() : GameController.IngameState.Data.RawPathfindingData;
            _processedTerrainTargetingData = GameController.IngameState.Data.RawTerrainTargetingData;

            if (Settings.InstanceDumpSettings.AutoDumpOnAreaChange)
            {
                RunDump();
            }

            GenerateMapTexture();
            _clusteredTargetLocations = ClusterTargets();
            StartPathFinding();
        }
    }
    
    private string GetInstanceDumpPath() => $@"{DirectoryFullName}\instance_dumps\{GameController.Area.CurrentArea.Area.Id}_{SanitizeAreaName(GameController.Area.CurrentArea.Area.Name)}";

    private static string SanitizeAreaName(string name) => name.Replace(" ", "_").Replace(":", "").Replace("/", "").Replace("\\", "");

    private ConcurrentDictionary<string, List<Room>> GetRooms()
    {
        return new ConcurrentDictionary<string, List<Room>>(GameController.IngameState.Data.AreaGraphs
            .SelectMany(x => x.Rooms)
            .Select(ToRoom)
            .Where(x => !string.IsNullOrEmpty(x.Name))
            .GroupBy(x => x.Name)
            .ToDictionary(x => x.Key, x => x.ToList()));
    }

    public override void DrawSettings()
    {
        Settings.PathfindingSettings.CurrentZoneName.Value = GameController.Area.CurrentArea.Area.Id;
        base.DrawSettings();
    }

    public override void OnLoad()
    {
        LoadTargets();
        Settings.Reload.OnPressed = () =>
        {
            Task.Run(() =>
            {
                LoadTargets();
                AreaChange(GameController.Area.CurrentArea);
            });
        };

        Settings.MaximumPathCount.OnValueChanged += (_, _) => { Task.Run(RestartPathFinding); };
        Settings.TerrainColor.OnValueChanged += (_, _) => { GenerateMapTexture(); };
        Settings.Debug.DrawHeightMap.OnValueChanged += (_, _) => { GenerateMapTexture(); };
        Settings.Debug.StandardEdgeSettings.SkipEdgeDetector.OnValueChanged += (_, _) => { GenerateMapTexture(); };
        Settings.Debug.StandardEdgeSettings.SkipNeighborFill.OnValueChanged += (_, _) => { GenerateMapTexture(); };
        Settings.Debug.StandardEdgeSettings.SkipRecoloring.OnValueChanged += (_, _) => { GenerateMapTexture(); };
        Settings.Debug.DisableHeightAdjust.OnValueChanged += (_, _) => { GenerateMapTexture(); };
        Settings.MaximumMapTextureDimension.OnValueChanged += (_, _) => { GenerateMapTexture(); };
        Settings.Debug.AlternativeEdgeMethod.OnValueChanged += (_, _) => { GenerateMapTexture(); };
        Settings.Debug.AlternativeEdgeSettings.OutlineBlurSigma.OnValueChanged += (_, _) => { GenerateMapTexture(); };
        Settings.Debug.AlternativeEdgeSettings.OutlineTransitionThreshold.OnValueChanged += (_, _) => { GenerateMapTexture(); };
        Settings.Debug.AlternativeEdgeSettings.OutlineFeatherWidth.OnValueChanged += (_, _) => { GenerateMapTexture(); };
    }

    public override void EntityAdded(Entity entity)
    {
        var positioned = entity.GetComponent<Positioned>();
        if (positioned != null)
        {
            var path = entity.Path;
            if (_currentZoneTargetEntityPaths.FirstOrDefault(x => x.Item1.IsMatch(path)).x is { } targetDescription)
            {
                var alreadyContains = false;
                var truncatedPos = positioned.GridPosNum.Truncate();
                _allTargetLocations.AddOrUpdate(path, _ => [truncatedPos],
                    // ReSharper disable once AssignmentInConditionalExpression
                    (_, l) => (alreadyContains = l.Contains(truncatedPos)) ? l : [..l, truncatedPos]);
                _locationsByPosition.AddOrUpdate(truncatedPos, _ => [path],
                    (_, l) => l.Contains(path) ? l : [..l, path]);
                if (!alreadyContains)
                {
                    var oldValue = _clusteredTargetLocations.GetValueOrDefault(targetDescription.EqualityId);
                    var newValue = _clusteredTargetLocations.AddOrUpdate(targetDescription.EqualityId,
                        _ => ClusterTarget(_targetDescriptionsInArea[targetDescription.EqualityId]),
                        (_, _) => ClusterTarget(_targetDescriptionsInArea[targetDescription.EqualityId]));
                    foreach (var newLocation in newValue.Locations.Except(oldValue?.Locations ?? []))
                    {
                        AddRoute(newLocation, [targetDescription], entity);
                    }
                }
            }
        }
    }

    private Vector2 GetPlayerMapGrid()
    {
        var pos = GameController.Game.IngameState.Data.LocalPlayer.PosNum;
        return new Vector2(pos.X * PoeMapExtension.WorldToGridConversion, pos.Y * PoeMapExtension.WorldToGridConversion);
    }

    private Vector2 GetPlayerTerrainGrid()
    {
        var pos = GameController.Game.IngameState.Data.LocalPlayer?.GetComponent<Positioned>();
        return pos == null ? GetPlayerMapGrid() : new Vector2(pos.GridX, pos.GridY);
    }

    private Vector2 GetPlayerPosition() => GetPlayerMapGrid();

    private SubMap GetVisibleSubMap()
    {
        var map = GameController.Game.IngameState.IngameUi.Map;
        return map.VisibleSubMap == VisibleSubMap.Small ? map.SmallMiniMap : map.LargeMap.AsObject<SubMap>();
    }

    public override void Render()
    {
        if (Settings.InstanceDumpSettings.ManualDumpHotkey.PressedOnce())
        {
            RunDump();
        }

        if (!Settings.Debug.RenderInPeacefulZones && GameController.Area.CurrentArea.IsPeaceful) return;

        var ingameUi = GameController.Game.IngameState.IngameUi;
        var anyFullscreenPanelVisible = ingameUi.FullscreenPanels.Any(x => x.IsVisible);
        if (!Settings.Debug.IgnoreFullscreenPanels && anyFullscreenPanelVisible) return;

        var anyLargePanelVisible = ingameUi.LargePanels.Any(x => x.IsVisible);
        if (!Settings.Debug.IgnoreLargePanels && anyLargePanelVisible) return;

        _rect = GameController.Window.GetWindowRectangle() with {Location = SharpDX.Vector2.Zero};
        if (!Settings.Debug.DisableDrawRegionLimiting)
        {
            if (ingameUi.OpenRightPanel.IsVisible)
            {
                _rect.Right = ingameUi.OpenRightPanel.GetClientRectCache.Left;
            }

            if (ingameUi.OpenLeftPanel.IsVisible)
            {
                _rect.Left = ingameUi.OpenLeftPanel.GetClientRectCache.Right;
            }
        }
        
        
        if (ingameUi.Map.VisibleSubMap != VisibleSubMap.None)
        {
            using var clip = ingameUi.Map.VisibleSubMap == VisibleSubMap.Large 
                ? Graphics.BeginRectClip(_rect)
                : Graphics.MapSurfaceClip(VisibleSubMap.Any);
            DrawLargeMap();
            DrawTargets();
            DrawRooms();
        }

        DrawWorldPaths(ingameUi.Map.LargeMap.AsObject<SubMap>());
        DrawPathLegend();
    }

    private void RunDump()
    {
        Task.Run(() => { DumpInstanceData(GetInstanceDumpPath()); });
    }

    private void DrawRooms()
    {
        if (!Settings.PathfindingSettings.ShowRooms)
        {
            return;
        }

        var regex = string.IsNullOrEmpty(Settings.PathfindingSettings.TargetNameFilter)
            ? null
            : new Regex(Settings.PathfindingSettings.TargetNameFilter, RegexOptions.IgnoreCase);
        var rooms = new List<(string Name, string DisplayName, Vector2 MinGrid, Vector2 MaxGrid, Vector2 CenterScreen, Vector2 HoverMin, Vector2 HoverMax)>();
        foreach (var areaGraph in GameController.IngameState.Data.AreaGraphs)
        {
            foreach (var room in areaGraph.Rooms)
            {
                if (string.IsNullOrEmpty(room.Name) || regex != null && !regex.IsMatch(room.Name))
                {
                    continue;
                }

                var minGrid = new Vector2(room.MinCoord.X * PoeMapExtension.TileToGridConversion, room.MinCoord.Y * PoeMapExtension.TileToGridConversion);
                var maxGrid = new Vector2(room.MaxCoord.X * PoeMapExtension.TileToGridConversion, room.MaxCoord.Y * PoeMapExtension.TileToGridConversion);
                var displayName = room.Name.StartsWith("Metadata/Terrain/") ? room.Name["Metadata/Terrain/".Length..] : room.Name;
                var centerGrid = new Vector2((minGrid.X + maxGrid.X) / 2f, (minGrid.Y + maxGrid.Y) / 2f);
                var centerScreen = MapGridToScreen(centerGrid, false);
                GetRoomHoverBounds(minGrid, maxGrid, out var hoverMin, out var hoverMax);
                rooms.Add((room.Name, displayName, minGrid, maxGrid, centerScreen, hoverMin, hoverMax));
            }
        }

        var mousePosition = ImGui.GetMousePos();
        var hoveredRoom = rooms
            .Where(x => IsPointInRect(mousePosition, x.HoverMin, x.HoverMax))
            .OrderBy(x => Vector2.DistanceSquared(mousePosition, x.CenterScreen))
            .FirstOrDefault();
        var hasHoveredRoom = hoveredRoom.Name != null;
        var matchingRoomCount = hasHoveredRoom ? rooms.Count(x => x.Name == hoveredRoom.Name) : 0;
        foreach (var room in rooms)
        {
            var isHovered = hasHoveredRoom && room.Name == hoveredRoom.Name && room.CenterScreen == hoveredRoom.CenterScreen;
            var isMatching = hasHoveredRoom && room.Name == hoveredRoom.Name;
            var lineColor = isHovered
                ? new SharpDX.Color(0, 255, 0, 255)
                : isMatching
                    ? new SharpDX.Color(255, 165, 0, 220)
                    : new SharpDX.Color(255, 255, 255, hasHoveredRoom ? 80 : 110);
            var lineThickness = isHovered ? 3 : isMatching ? 2 : 1;
            DrawRoomOutline(room.MinGrid, room.MaxGrid, lineColor, lineThickness);
            DrawRoomHoverHint(room.CenterScreen);
        }

        var middleDown = (Control.MouseButtons & MouseButtons.Middle) != 0;
        if (hasHoveredRoom && middleDown && !_middleMouseWasDown)
        {
            CopyTextToClipboard(hoveredRoom.Name);
        }

        _middleMouseWasDown = middleDown;

        if (hasHoveredRoom)
        {
            ImGui.BeginTooltip();
            ImGui.TextUnformatted(hoveredRoom.DisplayName);
            ImGui.Separator();
            ImGui.TextUnformatted(hoveredRoom.Name);
            ImGui.Text($"Min: {hoveredRoom.MinGrid.X:0}, {hoveredRoom.MinGrid.Y:0}");
            ImGui.Text($"Max: {hoveredRoom.MaxGrid.X:0}, {hoveredRoom.MaxGrid.Y:0}");
            ImGui.Text($"Size: {hoveredRoom.MaxGrid.X - hoveredRoom.MinGrid.X:0} x {hoveredRoom.MaxGrid.Y - hoveredRoom.MinGrid.Y:0}");
            if (matchingRoomCount > 1)
            {
                ImGui.Text($"Matches: {matchingRoomCount}");
            }

            ImGui.TextDisabled("Middle click to copy path");
            ImGui.EndTooltip();
        }
    }

    private void DrawRoomOutline(Vector2 minGrid, Vector2 maxGrid, SharpDX.Color lineColor, int lineThickness)
    {
        var (topLeft, topRight, bottomRight, bottomLeft) = GetRoomScreenCorners(minGrid, maxGrid);
        Graphics.DrawLine(topLeft, topRight, lineThickness, lineColor);
        Graphics.DrawLine(topRight, bottomRight, lineThickness, lineColor);
        Graphics.DrawLine(bottomRight, bottomLeft, lineThickness, lineColor);
        Graphics.DrawLine(bottomLeft, topLeft, lineThickness, lineColor);
    }

    private (Vector2 TopLeft, Vector2 TopRight, Vector2 BottomRight, Vector2 BottomLeft) GetRoomScreenCorners(Vector2 minGrid, Vector2 maxGrid)
    {
        return (
            MapGridToScreen(new Vector2(minGrid.X, minGrid.Y), false),
            MapGridToScreen(new Vector2(maxGrid.X, minGrid.Y), false),
            MapGridToScreen(new Vector2(maxGrid.X, maxGrid.Y), false),
            MapGridToScreen(new Vector2(minGrid.X, maxGrid.Y), false)
        );
    }

    private void GetRoomHoverBounds(Vector2 minGrid, Vector2 maxGrid, out Vector2 min, out Vector2 max)
    {
        var (topLeft, topRight, bottomRight, bottomLeft) = GetRoomScreenCorners(minGrid, maxGrid);
        var xMin = MathF.Min(MathF.Min(topLeft.X, topRight.X), MathF.Min(bottomRight.X, bottomLeft.X));
        var xMax = MathF.Max(MathF.Max(topLeft.X, topRight.X), MathF.Max(bottomRight.X, bottomLeft.X));
        var yMin = MathF.Min(MathF.Min(topLeft.Y, topRight.Y), MathF.Min(bottomRight.Y, bottomLeft.Y));
        var yMax = MathF.Max(MathF.Max(topLeft.Y, topRight.Y), MathF.Max(bottomRight.Y, bottomLeft.Y));
        var insetX = (xMax - xMin) * 0.25f;
        var insetY = (yMax - yMin) * 0.25f;
        min = new Vector2(xMin + insetX, yMin + insetY);
        max = new Vector2(xMax - insetX, yMax - insetY);
    }

    private static bool IsPointInRect(Vector2 point, Vector2 min, Vector2 max)
    {
        return point.X >= min.X && point.X <= max.X && point.Y >= min.Y && point.Y <= max.Y;
    }

    private static void CopyTextToClipboard(string text)
    {
        var thread = new Thread(() =>
        {
            try
            {
                Clipboard.SetText(text);
            }
            catch
            {
                // ignored
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(250);
    }

    private void DrawRoomHoverHint(Vector2 centerScreen)
    {
        var textSize = Graphics.MeasureText("Room");
        var textPos = centerScreen - new Vector2(textSize.X, textSize.Y) / 2f;
        Graphics.DrawTextWithBackground("Room", textPos, new SharpDX.Color(0, 0, 0, 180));
    }

    private void DrawWorldPaths(SubMap largeMap)
    {
        var worldPathSettings = Settings.PathfindingSettings.WorldPathSettings;
        if (worldPathSettings.ShowPathsToTargets && (!largeMap.IsVisible || !worldPathSettings.ShowPathsToTargetsOnlyWithClosedMap))
        {
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            var playerRender = player?.GetComponent<Render>();
            if (playerRender == null)
                return;
            var initPos = GameController.IngameState.Camera.WorldToScreen(playerRender.PosNum with { Z = playerRender.UnclampedHeight });
            foreach (var (route, offsetAmount) in _routes.Values
                         .GroupBy(x => x.Path.Count < 2 ? 0 : (x.Path[1] - x.Path[0]) switch { var diff => Math.Atan2(diff.Y, diff.X) })
                         .SelectMany(group => group.Select((route, i) => (route, i - group.Count() / 2.0f + 0.5f))))
            {
                var p0 = initPos;
                var p0WithOffset = p0;
                var i = 0;
                foreach (var elem in route.Path)
                {
                    var p1 = GameController.IngameState.Camera.WorldToScreen(
                        new Vector3(elem.X * GridToWorldMultiplier, elem.Y * GridToWorldMultiplier, _heightData[elem.Y][elem.X]));
                    var offsetDirection = worldPathSettings.OffsetPaths 
                        ? (p1 - p0) switch { var s => new Vector2(s.Y, -s.X) / s.Length() }
                        : Vector2.Zero;
                    var finalOffset = offsetDirection * offsetAmount * worldPathSettings.PathThickness;
                    p0 = p1;
                    p1 += finalOffset;
                    if (++i % worldPathSettings.DrawEveryNthSegment == 0)
                    {
                        if (_rect.Contains(p0WithOffset) || _rect.Contains(p1))
                        {
                            Graphics.DrawLine(p0WithOffset, p1, worldPathSettings.PathThickness, route.WorldColor());
                        }
                        else
                        {
                            break;
                        }
                    }

                    p0WithOffset = p1;
                }
            }
        }
    }

    private void DrawPathLegend()
	{
	    if (!Settings.PathfindingSettings.ShowPathLegend) return;
	    if (_clusteredTargetLocations.Count == 0) return;
	
	    var padding = 10f;
	    var lineHeight = 22f;
	    var boxSize = 14f;
	    var legendWidth = 220f;
	    var startX = Settings.PathfindingSettings.PathLegendPositionX.Value;
	    var startY = Settings.PathfindingSettings.PathLegendPositionY.Value;
	
	    // Match each target to its correct route color via location
	    var entries = _clusteredTargetLocations.Values
	        .SelectMany(t => t.Locations.Select(loc => (t.DisplayName, loc)))
	        .Select(x => {
	            var routeKey = new Vector2(x.loc.X, x.loc.Y);
	            var color = _routes.TryGetValue(routeKey, out var route) 
	                ? route.MapColor() 
	                : Color.White.ToSharpDx();
	            return (x.DisplayName, color);
	        })
	        .DistinctBy(x => x.DisplayName)
	        .ToList();
	
	    if (entries.Count == 0) return;
	
	    var bgHeight = padding * 2 + entries.Count * lineHeight;
	
	    Graphics.DrawBox(
	        new Vector2(startX - padding, startY - padding),
	        new Vector2(startX + legendWidth, startY + bgHeight),
            new SharpDX.Color(SharpDX.Vector3.Zero, 180 / 255f));
	
	    for (var i = 0; i < entries.Count; i++)
	    {
	        var (name, color) = entries[i];
	        var y = startY + i * lineHeight;

            Graphics.DrawBox(
	            new Vector2(startX, y + 2),
	            new Vector2(startX + boxSize, y + boxSize + 2),
	            color);
	
	        Graphics.DrawText(name, new Vector2(startX + boxSize + 8, y));
	    }
	}

    private Vector2 MapGridToScreen(Vector2 gridCell, bool perCellTerrain = true)
    {
        var playerGridPosition = GetPlayerMapGrid();
        var playerTerrain = GetPlayerTerrainGrid();
        var playerScreenPosition = Graphics.GridToMap(playerGridPosition, playerTerrain, VisibleSubMap.Any);
        var screenOffset = new Vector2(Settings.Debug.MapCenterOffsetX.Value, Settings.Debug.MapCenterOffsetY.Value);
        var playerRender = GameController.Game.IngameState.Data.LocalPlayer?.GetComponent<Render>();
        if (playerRender == null)
        {
            var cellScreenPosition = Graphics.GridToMap(gridCell, gridCell, VisibleSubMap.Any);
            return playerScreenPosition + (cellScreenPosition - playerScreenPosition) * Settings.CustomScale.Value + screenOffset;
        }

        var ingameData = GameController.IngameState.Data;
        var deltaZ = -playerRender.UnclampedHeight;
        if (perCellTerrain)
            deltaZ += ingameData.GetTerrainHeightAt(gridCell);

        var mapDelta = ingameData.TranslateGridDeltaToMapDelta(GetVisibleSubMap(), gridCell - playerGridPosition, deltaZ);
        return playerScreenPosition + mapDelta * Settings.CustomScale.Value + screenOffset;
    }

    private void DrawLargeMap()
    {
        if (!Settings.DrawWalkableMap || !Graphics.HasImage(TextureName) || _areaDimensions == null) return;
        if (GameController.Game.IngameState.Data.LocalPlayer == null) return;
        var areaWidth = (float)_areaDimensions.Value.X;
        var areaHeight = (float)_areaDimensions.Value.Y;
        var p1 = MapGridToScreen(new Vector2(0, 0), false);
        var p2 = MapGridToScreen(new Vector2(areaWidth, 0), false);
        var p3 = MapGridToScreen(new Vector2(areaWidth, areaHeight), false);
        var p4 = MapGridToScreen(new Vector2(0, areaHeight), false);
        Graphics.DrawQuad(Graphics.GetTextureId(TextureName), p1, p2, p3, p4);
    }

    private void DrawTargets()
    {
        var pathfindingSettings = Settings.PathfindingSettings;
        var color = pathfindingSettings.TargetNameColor.Value;
        if (GameController.Game.IngameState.Data.LocalPlayer?.GetComponent<Positioned>() == null) return;
        var ithElement = 0;
        if (pathfindingSettings.ShowPathsToTargetsOnMap)
        {
            foreach (var route in _routes.Values)
            {
                ithElement++;
                ithElement %= 5;
                foreach (var elem in route.Path.Skip(ithElement).GetEveryNth(5))
                {
                    var mapPos = MapGridToScreen(new Vector2(elem.X, elem.Y));
                    Graphics.DrawBox(mapPos - new Vector2(2, 2), mapPos + new Vector2(2, 2), route.MapColor());
                }
            }
        }

        if (pathfindingSettings.ShowAllTargets)
        {
            var regex = string.IsNullOrEmpty(pathfindingSettings.TargetNameFilter) 
                ? null 
                : new Regex(pathfindingSettings.TargetNameFilter);

            foreach (var (location, texts) in _locationsByPosition)
            {
                bool TargetFilter(string t) =>
                    (regex?.IsMatch(t) ?? true) &&
                    _allTargetLocations.GetValueOrDefault(t) is { } list && 
                    list.Count <= pathfindingSettings.MaxTargetNameCount;

                var text = string.Join("\n", texts.Distinct().Where(TargetFilter));
                var textOffset = Graphics.MeasureText(text) / 2f;
                var mapPos = MapGridToScreen(new Vector2(location.X, location.Y));
                if (pathfindingSettings.EnableTargetNameBackground) Graphics.DrawBox(mapPos - textOffset, mapPos + textOffset, System.Drawing.Color.Black.ToSharpDx());
                Graphics.DrawText(text, mapPos - textOffset, color);
            }
        }
        else if (pathfindingSettings.ShowSelectedTargets)
        {
            foreach (var (_, description) in _clusteredTargetLocations)
            {
                foreach (var clusterPosition in description.Locations)
                {
                    var text = description.DisplayName;
                    var textOffset = Graphics.MeasureText(text) / 2f;
                    var mapPos = MapGridToScreen(new Vector2(clusterPosition.X, clusterPosition.Y));
                    if (pathfindingSettings.EnableTargetNameBackground) Graphics.DrawBox(mapPos - textOffset, mapPos + textOffset, System.Drawing.Color.Black.ToSharpDx());
                    Graphics.DrawText(text, mapPos - textOffset, color);
                }
            }
        }
    }

    private static Room ToRoom(AreaGraphRoomInstance room)
    {
        return new Room
        {
            Name = room.Name,
            MinX = room.MinCoord.X * PoeMapExtension.TileToGridConversion,
            MinY = room.MinCoord.Y * PoeMapExtension.TileToGridConversion,
            MaxX = room.MaxCoord.X * PoeMapExtension.TileToGridConversion,
            MaxY = room.MaxCoord.Y * PoeMapExtension.TileToGridConversion
        };
    }
}
