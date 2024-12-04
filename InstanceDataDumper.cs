using System.Collections.Generic;
using System;
using System.IO;
using GameOffsets.Native;
using System.Linq;
using Newtonsoft.Json;

namespace Radar;

public class OptimizedInstanceData
{
    public string Name { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public float[] Heights { get; set; }
    public int[] Walk { get; set; } // Just a simple int array
    public TileRef[] Tiles { get; set; }
}

public class TileRef
{
    public string T { get; set; }  // Type
    public int X { get; set; }     // Anchor X
    public int Y { get; set; }     // Anchor Y
    public int W { get; set; }     // Width
    public int H { get; set; }     // Height
}

public partial class Radar
{
    public void DumpInstanceData(string outputPath)
    {
        if (_heightData == null || _processedTerrainData == null || _areaDimensions == null)
        {
            Console.WriteLine("ERROR: Must be in a game area to dump data");
            return;
        }

        try
        {
            var dimensions = _areaDimensions.Value;
            var tgtData = GetTileTargets();
            var tileRefs = new List<TileRef>();

            // Create flattened arrays
            var heights = new float[dimensions.X * dimensions.Y];
            var walk = new int[dimensions.X * dimensions.Y];

            // Fill the arrays
            for (var y = 0; y < dimensions.Y && y < _heightData.Length; y++)
            {
                for (var x = 0; x < dimensions.X && x < _heightData[y].Length; x++)
                {
                    var index = y * dimensions.X + x;
                    heights[index] = _heightData[y][x];
                    walk[index] = _processedTerrainData[y][x];
                }
            }

            // Process tiles
            foreach (var (tileType, positions) in tgtData)
            {
                var tileGroups = positions.GroupBy(pos => new Vector2i(
                    (pos.X / TileToGridConversion) * TileToGridConversion,
                    (pos.Y / TileToGridConversion) * TileToGridConversion
                ));

                foreach (var group in tileGroups)
                {
                    var anchorPoint = group.Key;

                    // Skip tiles outside bounds
                    if (anchorPoint.X >= dimensions.X || anchorPoint.Y >= dimensions.Y)
                        continue;

                    tileRefs.Add(new TileRef
                    {
                        T = tileType,
                        X = anchorPoint.X,
                        Y = anchorPoint.Y,
                        W = TileToGridConversion,
                        H = TileToGridConversion
                    });
                }
            }

            var instanceData = new OptimizedInstanceData
            {
                Name = GameController.Area.CurrentArea.Area.RawName,
                W = dimensions.X,
                H = dimensions.Y,
                Heights = heights,
                Walk = walk,
                Tiles = tileRefs.ToArray()
            };

            // Create directory if it doesn't exist
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Serialize and write with indentation for readability
            var json = JsonConvert.SerializeObject(instanceData, new JsonSerializerSettings
            {
                Formatting = Formatting.None
            });

            File.WriteAllText(outputPath, json);

            Console.WriteLine($"Successfully wrote instance data to {outputPath}");
            Console.WriteLine($"File size: {new FileInfo(outputPath).Length / 1024.0:F2} KB");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error dumping instance data: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
        }
    }
}