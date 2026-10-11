using Graphics2.Mathematics;

namespace WinterGame.Materials;

/// <summary>
///     One material's maps in the layout of the game's texture arrays: an albedo map with the height in its alpha, and a normal map.
///     Both are RGBA8 texels row after row and cover <see cref="TileMetres"/> on each axis.
/// </summary>
public sealed class MaterialMaps
{
    public const float TileMetres = 4.0f;

    public readonly int Size;

    /// <summary>
    ///     The albedo in sRGB, and in alpha the height, scaled from the tile's lowest point at 0 to its highest at 255.
    /// </summary>
    public readonly byte[] AlbedoHeight;

    /// <summary>
    ///     The unit surface normal mapped from [-1, 1] to [0, 255], with x along increasing u (the columns), y along increasing v (the rows) and z out of the surface. Alpha is 255.
    /// </summary>
    public readonly byte[] Normal;

    private MaterialMaps(int size, byte[] albedoHeight, byte[] normal)
    {
        Size = size;
        AlbedoHeight = albedoHeight;
        Normal = normal;
    }

    /// <summary>
    ///     Packs the albedo, in linear light, and the height in metres. The normals are the height's slopes by central differences.
    /// </summary>
    public static MaterialMaps Pack(TileImage red, TileImage green, TileImage blue, TileImage height)
    {
        var size = height.Size;

        if (red.Size != size || green.Size != size || blue.Size != size)
        {
            throw new ArgumentException("A material's planes are all one size.");
        }

        var lowest = float.MaxValue;
        var highest = float.MinValue;

        foreach (var value in height.Pixels)
        {
            lowest = MathF.Min(lowest, value);
            highest = MathF.Max(highest, value);
        }

        var heightScale = highest > lowest ? 255.0f / (highest - lowest) : 0.0f;
        var slopeScale = size / (2.0f * TileMetres);
        var albedoHeight = new byte[4 * size * size];
        var normal = new byte[4 * size * size];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var index = y * size + x;
                var texel = 4 * index;

                albedoHeight[texel] = EncodeSrgb(red.Pixels[index]);
                albedoHeight[texel + 1] = EncodeSrgb(green.Pixels[index]);
                albedoHeight[texel + 2] = EncodeSrgb(blue.Pixels[index]);
                albedoHeight[texel + 3] = (byte)((height.Pixels[index] - lowest) * heightScale + 0.5f);

                var slopeX = (height[x + 1, y] - height[x - 1, y]) * slopeScale;
                var slopeY = (height[x, y + 1] - height[x, y - 1]) * slopeScale;
                var inverseLength = 1.0f / MathF.Sqrt(slopeX * slopeX + slopeY * slopeY + 1.0f);

                normal[texel] = EncodeSigned(-slopeX * inverseLength);
                normal[texel + 1] = EncodeSigned(-slopeY * inverseLength);
                normal[texel + 2] = EncodeSigned(inverseLength);
                normal[texel + 3] = 255;
            }
        }

        return new MaterialMaps(size, albedoHeight, normal);
    }

    /// <summary>
    ///     A material of one colour with no relief, the maps <see cref="Pack"/> gives for planes of one albedo, in linear light, and one height.
    /// </summary>
    public static MaterialMaps Flat(int size, float red, float green, float blue)
    {
        if (!MathX.IsPowerOfTwo(size))
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "A material's size is a power of two.");
        }

        var albedoHeight = new byte[4 * size * size];
        var normal = new byte[4 * size * size];
        var redCode = EncodeSrgb(red);
        var greenCode = EncodeSrgb(green);
        var blueCode = EncodeSrgb(blue);
        var level = EncodeSigned(0.0f);
        var up = EncodeSigned(1.0f);

        for (var texel = 0; texel < albedoHeight.Length; texel += 4)
        {
            albedoHeight[texel] = redCode;
            albedoHeight[texel + 1] = greenCode;
            albedoHeight[texel + 2] = blueCode;

            normal[texel] = level;
            normal[texel + 1] = level;
            normal[texel + 2] = up;
            normal[texel + 3] = 255;
        }

        return new MaterialMaps(size, albedoHeight, normal);
    }

    private static byte EncodeSrgb(float linear)
    {
        var clamped = Math.Clamp(linear, 0.0f, 1.0f);
        var encoded = clamped <= 0.0031308f ? 12.92f * clamped : 1.055f * MathF.Pow(clamped, 1.0f / 2.4f) - 0.055f;

        return (byte)(encoded * 255.0f + 0.5f);
    }

    private static byte EncodeSigned(float value)
    {
        return (byte)(Math.Clamp(0.5f * value + 0.5f, 0.0f, 1.0f) * 255.0f + 0.5f);
    }
}
