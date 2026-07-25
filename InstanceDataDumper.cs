using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Compression;

namespace Radar;

public class Room
{
    public string Name { get; set; }
    public int MinX { get; set; }
    public int MaxX { get; set; }
    public int MinY { get; set; }
    public int MaxY { get; set; }
}

public class TilePosition
{
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public List<string> Tiles { get; set; }
}

public class OptimizedInstanceData
{
    public string Name { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public float[] Heights { get; set; }
    public int[] Walk { get; set; }
    public int[] Target { get; set; }
    public List<TilePosition> Tiles { get; set; }
    public List<Room> Rooms { get; set; }
}

public partial class Radar
{
    public void DumpInstanceData(string outputPath)
    {
        if (_heightData == null || _processedTerrainData == null || _processedTerrainTargetingData == null || _areaDimensions == null)
        {
            return;
        }

        try
        {
            var dimensions = _areaDimensions.Value;

            // Create flattened arrays
            var heights = new float[dimensions.X * dimensions.Y];
            var walk = new int[dimensions.X * dimensions.Y];
            var target = new int[dimensions.X * dimensions.Y];

            // Fill the arrays
            for (var y = 0; y < dimensions.Y && y < _heightData.Length; y++)
            {
                for (var x = 0; x < dimensions.X && x < _heightData[y].Length; x++)
                {
                    var index = y * dimensions.X + x;
                    heights[index] = _heightData[y][x];
                    walk[index] = _processedTerrainData[y][x];
                    target[index] = _processedTerrainTargetingData[y][x];
                }
            }

            // Convert to list of TilePositions
            var tilePositions = _locationsByPosition.Select(kvp => new TilePosition
            {
                X = kvp.Key.X,
                Y = kvp.Key.Y,
                W = ExileCore.Shared.Helpers.PoeMapExtension.TileToGridConversion,
                H = ExileCore.Shared.Helpers.PoeMapExtension.TileToGridConversion,
                Tiles = kvp.Value
            }).ToList();

            var instanceData = new OptimizedInstanceData
            {
                Name = GameController.Area.CurrentArea.Area.RawName,
                W = dimensions.X,
                H = dimensions.Y,
                Heights = heights,
                Walk = walk,
                Target = target,
                Tiles = tilePositions,
                Rooms = GameController.IngameState.Data.AreaGraphs.SelectMany(x => x.Rooms).Select(ToRoom).ToList()
            };

            if (!Settings.InstanceDumpSettings.IncludeGrids)
            {
                instanceData.Target = null;
                instanceData.Walk = null;
                instanceData.Heights = null;
            }

            // Create directory if it doesn't exist
            var extension = Settings.InstanceDumpSettings.CompressDumps ? ".json.gz" : ".json";
            var fullPath = Path.GetFullPath(outputPath + extension);
            var directory = Path.GetDirectoryName(fullPath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Serialize
            var json = JsonConvert.SerializeObject(
                instanceData, new JsonSerializerSettings
                {
                    Formatting = Settings.InstanceDumpSettings.CompressDumps ? Formatting.None : Formatting.Indented
                });

            using var fileStream = File.Create(fullPath);
            if (Settings.InstanceDumpSettings.CompressDumps)
            {
                using var gzipStream = new GZipStream(fileStream, CompressionLevel.Optimal);
                using var writer = new StreamWriter(gzipStream);
                writer.Write(json);
            }
            else
            {
                using var writer = new StreamWriter(fileStream);
                writer.Write(json);
            }
        }
        catch (Exception ex)
        {
            _ = ex;
        }
    }
}