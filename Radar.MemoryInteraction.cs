using System;
using System.Linq;
using System.Threading.Tasks;
using ExileCore2;
using ExileCore2.Shared.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Convolution;
using SixLabors.ImageSharp.Processing.Processors.Transforms;
using Configuration = SixLabors.ImageSharp.Configuration;
using Vector4 = System.Numerics.Vector4;

namespace Radar;

public partial class Radar
{
    private void GenerateMapTexture()
    {
        var gridHeightData = _heightData;
        var maxX = _areaDimensions.Value.X;
        var maxY = _areaDimensions.Value.Y;
        var configuration = Configuration.Default.Clone();
        configuration.PreferContiguousImageBuffers = true;
        using var image = new Image<Rgba32>(configuration, maxX, maxY);
        if (Settings.Debug.DrawHeightMap)
        {
            var minHeight = gridHeightData.Min(x => x.Min());
            var maxHeight = gridHeightData.Max(x => x.Max());
            image.Mutate(configuration, c => c.ProcessPixelRowsAsVector4((row, i) =>
            {
                for (var x = 0; x < row.Length - 1; x += 2)
                {
                    var cellData = gridHeightData[i.Y][x];
                    for (var x_s = 0; x_s < 2; ++x_s)
                    {
                        row[x + x_s] = new Vector4(0, (cellData - minHeight) / (maxHeight - minHeight), 0, 1);
                    }
                }
            }));
        }
        else
        {
            var unwalkableMask = Vector4.UnitX + Vector4.UnitW;
            var walkableMask = Vector4.UnitY + Vector4.UnitW;
            if (Settings.Debug.DisableHeightAdjust)
            {
                Parallel.For(0, maxY, y =>
                {
                    for (var x = 0; x < maxX; x++)
                    {
                        var terrainType = _processedTerrainData[y][x];
                        image[x, y] = new Rgba32(terrainType is 0 ? unwalkableMask : walkableMask);
                    }
                });
            }
            else
            {
                Parallel.For(0, maxY, y =>
                {
                    for (var x = 0; x < maxX; x++)
                    {
                        var cellData = gridHeightData[y][x / 2 * 2];

                        //basically, offset x and y by half the offset z would cause when rendering in 3d
                        var heightOffset = (int)(cellData / GridToWorldMultiplier / 2);
                        var offsetX = x - heightOffset;
                        var offsetY = y - heightOffset;
                        var terrainType = _processedTerrainData[y][x];
                        if (offsetX >= 0 && offsetX < maxX && offsetY >= 0 && offsetY < maxY)
                        {
                            image[offsetX, offsetY] = new Rgba32(terrainType is 0 ? unwalkableMask : walkableMask);
                        }
                    }
                });
            }

            if (!Settings.Debug.SkipNeighborFill)
            {
                Parallel.For(0, maxY, y =>
                {
                    for (var x = 0; x < maxX; x++)
                    {
                        //this fills in the blanks that are left over from the height projection
                        if (image[x, y].ToVector4() == Vector4.Zero)
                        {
                            var countWalkable = 0;
                            var countUnwalkable = 0;
                            for (var xO = -1; xO < 2; xO++)
                            {
                                for (var yO = -1; yO < 2; yO++)
                                {
                                    var xx = x + xO;
                                    var yy = y + yO;
                                    if (xx >= 0 && xx < maxX && yy >= 0 && yy < maxY)
                                    {
                                        var nPixel = image[x + xO, y + yO].ToVector4();
                                        if (nPixel == walkableMask)
                                            countWalkable++;
                                        else if (nPixel == unwalkableMask)
                                            countUnwalkable++;
                                    }
                                }
                            }

                            image[x, y] = new Rgba32(countWalkable > countUnwalkable ? walkableMask : unwalkableMask);
                        }
                    }
                });
            }

            if (!Settings.Debug.SkipEdgeDetector)
            {
                var edgeDetector = new EdgeDetectorProcessor(EdgeDetectorKernel.Laplacian5x5, false)
                   .CreatePixelSpecificProcessor(configuration, image, image.Bounds());
                edgeDetector.Execute();
            }

            if (!Settings.Debug.SkipRecoloring)
            {
                image.Mutate(configuration, c => c.ProcessPixelRowsAsVector4((row, p) =>
                {
                    for (var x = 0; x < row.Length - 0; x++)
                    {
                        row[x] = row[x] switch
                        {
                            { X: 1 } => Settings.TerrainColor.Value.ToImguiVec4(),
                            { X: 0 } => Vector4.Zero,
                            var s => s
                        };
                    }
                }));
            }
        }

        if (Math.Max(image.Height, image.Width) > Settings.MaximumMapTextureDimension)
        {
            var (newWidth, newHeight) = (image.Width, image.Height);
            if (image.Height > image.Width)
            {
                newWidth = newWidth * Settings.MaximumMapTextureDimension / newHeight;
                newHeight = Settings.MaximumMapTextureDimension;
            }
            else
            {
                newHeight = newHeight * Settings.MaximumMapTextureDimension / newWidth;
                newWidth = Settings.MaximumMapTextureDimension;
            }

            var targetSize = new Size(newWidth, newHeight);
            var resizer = new ResizeProcessor(new ResizeOptions { Size = targetSize }, image.Size())
                .CreatePixelSpecificCloningProcessor(configuration, image, image.Bounds());
            resizer.Execute();
        }

        //unfortunately the library doesn't respect our allocation settings above
        
        using var imageCopy = image.Clone(configuration);
        imageCopy.Save("test.png");
        Graphics.AddOrUpdateImage(TextureName, imageCopy);
    }
}
