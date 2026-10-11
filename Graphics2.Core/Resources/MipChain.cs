using System.Numerics;

namespace Graphics2.Core.Resources;

/// <summary>
///     The extents and sizes of a texture's mip levels, and a box filter that builds the smaller levels of a 2D image on the CPU.
///     A layer's chain is its levels one after another, largest first, each in tightly packed rows.
/// </summary>
public static class MipChain
{
    private static readonly float[] SrgbToLinear = CreateSrgbToLinear();

    /// <summary>
    ///     The linear value halfway between each pair of neighbouring sRGB codes, measured in sRGB, so that rounding to a code is counting the thresholds below a value.
    /// </summary>
    private static readonly float[] SrgbThresholds = CreateSrgbThresholds();

    /// <summary>
    ///     The number of levels in a complete chain, which ends at one texel on the longest axis.
    /// </summary>
    public static uint LevelCount(uint width, uint height, uint depth = 1)
    {
        var longest = Math.Max(width, Math.Max(height, depth));

        ArgumentOutOfRangeException.ThrowIfZero(longest);

        return (uint)BitOperations.Log2(longest) + 1;
    }

    /// <summary>
    ///     The extent of one axis at a level. It halves per level, rounding down, and never drops below 1.
    /// </summary>
    public static uint LevelExtent(uint extent, uint level)
    {
        return level >= 32 ? 1 : Math.Max(1, extent >> (int)level);
    }

    /// <summary>
    ///     The bytes of the first <paramref name="levels"/> levels of one layer.
    /// </summary>
    public static long LayerSize(uint width, uint height, uint depth, uint levels, TextureFormat format)
    {
        var texels = 0L;

        for (var level = 0u; level < levels; level++)
        {
            texels += (long)LevelExtent(width, level) * LevelExtent(height, level) * LevelExtent(depth, level);
        }

        return texels * format.TexelSize();
    }

    /// <summary>
    ///     Fills every level of a 2D layer's chain after the first, which <paramref name="chain"/> already holds, each from the level before by averaging 2x2 texels.
    ///     An sRGB color is averaged in linear light. An odd extent leaves out its last row or column.
    ///     Only formats of 8-bit normalized channels can be filtered, because an integer texel is an id and has no average.
    /// </summary>
    public static void Generate(Span<byte> chain, uint width, uint height, uint levels, TextureFormat format)
    {
        var channels = format switch
        {
            TextureFormat.R8Unorm => 1,
            TextureFormat.R8G8B8A8Unorm or TextureFormat.R8G8B8A8Srgb or TextureFormat.B8G8R8A8Srgb => 4,
            _ => throw new ArgumentException($"{format} has no 8-bit normalized channels to average.", nameof(format))
        };

        var colorChannels = format is TextureFormat.R8G8B8A8Srgb or TextureFormat.B8G8R8A8Srgb ? 3 : 0;
        var size = LayerSize(width, height, 1, levels, format);

        if (chain.Length < size)
        {
            throw new ArgumentException($"A chain of {levels} levels of a {width}x{height} {format} image is {size} bytes, but the span holds {chain.Length}.", nameof(chain));
        }

        var sourceOffset = 0;

        for (var level = 1u; level < levels; level++)
        {
            var sourceWidth = (int)LevelExtent(width, level - 1);
            var sourceHeight = (int)LevelExtent(height, level - 1);
            var targetWidth = (int)LevelExtent(width, level);
            var targetHeight = (int)LevelExtent(height, level);
            var targetOffset = sourceOffset + sourceWidth * sourceHeight * channels;

            ReadOnlySpan<byte> source = chain.Slice(sourceOffset, sourceWidth * sourceHeight * channels);
            var target = chain.Slice(targetOffset, targetWidth * targetHeight * channels);

            for (var y = 0; y < targetHeight; y++)
            {
                var row0 = 2 * y * sourceWidth;
                var row1 = Math.Min(2 * y + 1, sourceHeight - 1) * sourceWidth;

                for (var x = 0; x < targetWidth; x++)
                {
                    var column0 = 2 * x;
                    var column1 = Math.Min(2 * x + 1, sourceWidth - 1);
                    var texel00 = (row0 + column0) * channels;
                    var texel01 = (row0 + column1) * channels;
                    var texel10 = (row1 + column0) * channels;
                    var texel11 = (row1 + column1) * channels;
                    var targetTexel = (y * targetWidth + x) * channels;

                    for (var channel = 0; channel < channels; channel++)
                    {
                        if (channel < colorChannels)
                        {
                            var linear = SrgbToLinear[source[texel00 + channel]]
                                         + SrgbToLinear[source[texel01 + channel]]
                                         + SrgbToLinear[source[texel10 + channel]]
                                         + SrgbToLinear[source[texel11 + channel]];

                            target[targetTexel + channel] = LinearToSrgb(0.25f * linear);
                        }
                        else
                        {
                            var sum = source[texel00 + channel] + source[texel01 + channel] + source[texel10 + channel] + source[texel11 + channel];

                            target[targetTexel + channel] = (byte)((sum + 2) / 4);
                        }
                    }
                }
            }

            sourceOffset = targetOffset;
        }
    }

    private static byte LinearToSrgb(float linear)
    {
        var low = 0;
        var high = SrgbThresholds.Length;

        while (low < high)
        {
            var middle = (low + high) / 2;

            if (SrgbThresholds[middle] <= linear)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return (byte)low;
    }

    private static float DecodeSrgb(double encoded)
    {
        return (float)(encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4));
    }

    private static float[] CreateSrgbToLinear()
    {
        var table = new float[256];

        for (var code = 0; code < table.Length; code++)
        {
            table[code] = DecodeSrgb(code / 255.0);
        }

        return table;
    }

    private static float[] CreateSrgbThresholds()
    {
        var table = new float[255];

        for (var code = 0; code < table.Length; code++)
        {
            table[code] = DecodeSrgb((code + 0.5) / 255.0);
        }

        return table;
    }
}
