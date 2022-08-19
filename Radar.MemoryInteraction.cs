using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ExileCore.Shared.Helpers;
using GameOffsets;
using GameOffsets.Native;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Convolution;
using Configuration = SixLabors.ImageSharp.Configuration;
using Vector4 = System.Numerics.Vector4;

namespace Radar;

public partial class Radar
{
    private byte[] GetRotationSelector()
    {
        var pattern = new Pattern(
            "?? 8D ?? ^ ?? ?? ?? ?? 0F B6 ?? ?? 88 ?? ?? ?? ?? 8D ?? ?? ?? 8D ?? ?? E8 ?? ?? ?? ?? ?? 8B ?? ?? ??",
            "Terrain Rotation Selector");
        var address = GameController.Memory.FindPatterns(pattern)[0];

        var realAddress = GameController.Memory.Read<int>(GameController.Memory.AddressOfProcess + address + pattern.PatternOffset) + address + pattern.PatternOffset + 4;
        return GameController.Memory.ReadBytes(GameController.Memory.AddressOfProcess + realAddress, 8);
    }

    private byte[] GetRotationHelper()
    {
        var pattern = new Pattern("?? 8D ?? ^ ?? ?? ?? ?? ?? 03 ?? 8B ?? ?? 2B ?? 89 ?? ?? ?? 8B ?? ?? ?? FF ??", "Terrain Rotator Helper");
        var address = GameController.Memory.FindPatterns(pattern)[0];

        var realAddress = GameController.Memory.Read<int>(GameController.Memory.AddressOfProcess + address + pattern.PatternOffset) + address + pattern.PatternOffset + 4;
        return GameController.Memory.ReadBytes(GameController.Memory.AddressOfProcess + realAddress, 24);
    }

    private static StdVector Cast(NativePtrArray nativePtrArray)
    {
        //PepeLa
        //this is going to break one day and everyone's gonna be sorry, but I'm leaving this
        return MemoryMarshal.Cast<NativePtrArray, StdVector>(stackalloc NativePtrArray[] { nativePtrArray })[0];
    }

    private float[][] GetTerrainHeight()
    {
        var rotationSelector = RotationSelector;
        var rotationHelper = RotationHelper;
        var tileData = GameController.Memory.ReadStdVector<TileStructure>(Cast(_terrainMetadata.TgtArray));
        var tileHeightCache = tileData.Select(x => x.SubTileDetailsPtr)
           .Distinct()
           .AsParallel()
           .Select(addr => new
            {
                addr,
                data = GameController.Memory.ReadStdVector<sbyte>(GameController.Memory.Read<SubTileStructure>(addr).SubTileHeight)
            })
           .ToDictionary(x => x.addr, x => x.data);
        var gridSizeX = _terrainMetadata.NumCols * TileToGridConversion;
        var toExclusive = _terrainMetadata.NumRows * TileToGridConversion;
        var result = new float[toExclusive][];
        Parallel.For(0, toExclusive, y =>
        {
            result[y] = new float[gridSizeX];
            for (var x = 0; x < gridSizeX; ++x)
            {
                var tileStructure = tileData[y / TileToGridConversion * _terrainMetadata.NumCols + x / TileToGridConversion];
                var tileHeightArray = tileHeightCache[tileStructure.SubTileDetailsPtr];
                var tileHeight = 0;
                if (tileHeightArray.Length != 0)
                {
                    var gridX = x % TileToGridConversion;
                    var gridY = y % TileToGridConversion;
                    var maxCoordInTile = TileToGridConversion - 1;
                    int[] coordHelperArray =
                    {
                        maxCoordInTile - gridX,
                        gridX,
                        maxCoordInTile - gridY,
                        gridY
                    };
                    var rotationIndex = rotationSelector[tileStructure.RotationSelector] * 3;
                    int axisSwitch = rotationHelper[rotationIndex];
                    int smallAxisFlip = rotationHelper[rotationIndex + 1];
                    int largeAxisFlip = rotationHelper[rotationIndex + 2];
                    var smallIndex = coordHelperArray[axisSwitch * 2 + smallAxisFlip];
                    var index = coordHelperArray[largeAxisFlip + (1 - axisSwitch) * 2] * TileToGridConversion + smallIndex;
                    tileHeight = tileHeightArray[index];
                }

                result[y][x] = (float)((tileStructure.TileHeight * (int)_terrainMetadata.TileHeightMultiplier + tileHeight) * TileHeightFinalMultiplier);
            }
        });
        return result;
    }

    private unsafe void GenerateMapTexture()
    {
        var gridHeightData = _heightData;
        var maxX = _areaDimensions.Value.X;
        var maxY = _areaDimensions.Value.Y;
        using var image = new Image<Rgba32>(maxX, maxY);
        if (Settings.Debug.DrawHeightMap)
        {
            var minHeight = gridHeightData.Min(x => x.Min());
            var maxHeight = gridHeightData.Max(x => x.Max());
            image.Mutate(c => c.ProcessPixelRowsAsVector4((row, i) =>
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
                        var offsetX = x + heightOffset;
                        var offsetY = y + heightOffset;
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
                var edgeDetector = new EdgeDetectorProcessor(new EdgeDetectorKernel(new DenseMatrix<float>(new float[,]
                    {
                        { -1, -1, -1, -1, -1 },
                        { -1, -1, -1, -1, -1 },
                        { -1, -1, 24, -1, -1 },
                        { -1, -1, -1, -1, -1 },
                        { -1, -1, -1, -1, -1 },
                    })), false)
                   .CreatePixelSpecificProcessor(Configuration.Default, image, image.Bounds());
                edgeDetector.Execute();
            }

            if (!Settings.Debug.SkipRecoloring)
            {
                image.Mutate(c => c.ProcessPixelRowsAsVector4((row, p) =>
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

        image.TryGetSinglePixelSpan(out var span);
        var width = image.Width;
        var height = image.Height;
        var bytesPerPixel = image.PixelType.BitsPerPixel / 8;
        fixed (Rgba32* rgba32Ptr = &MemoryMarshal.GetReference<Rgba32>(span))
        {
            var rect = new DataRectangle(new IntPtr(rgba32Ptr), width * bytesPerPixel);

            using var tex2D = new Texture2D(Graphics.LowLevel.D11Device,
                new Texture2DDescription
                {
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.R8G8B8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource,
                    CpuAccessFlags = CpuAccessFlags.None,
                    OptionFlags = ResourceOptionFlags.None
                }, rect);

            var shaderResourceView = new ShaderResourceView(Graphics.LowLevel.D11Device, tex2D);
            Graphics.LowLevel.AddOrUpdateTexture(TextureName, shaderResourceView);
        }
    }

    private int[][] ParseTerrainPathData()
    {
        var mapTextureData = _rawTerrainData;
        var bytesPerRow = _terrainMetadata.BytesPerRow;
        var totalRows = mapTextureData.Length / bytesPerRow;
        var processedTerrainData = new int[totalRows][];
        var xSize = bytesPerRow * 2;
        _areaDimensions = new Vector2i(xSize, totalRows);
        for (var i = 0; i < totalRows; i++)
        {
            processedTerrainData[i] = new int[xSize];
        }

        Parallel.For(0, totalRows, y =>
        {
            for (var x = 0; x < xSize; x += 2)
            {
                var dateElement = mapTextureData[y * bytesPerRow + x / 2];
                for (var xs = 0; xs < 2; xs++)
                {
                    var rawPathType = dateElement >> 4 * xs & 15;
                    processedTerrainData[y][x + xs] = rawPathType;
                }
            }
        });

        return processedTerrainData;
    }
}
