namespace WinterGame.Materials;

/// <summary>
///     Operations over whole tile images. Every neighbourhood wraps at the edges, so a tiling image stays tiling.
/// </summary>
public static class TileOps
{
    /// <summary>
    ///     Blurs by <paramref name="passes"/> box filters of 2 * radius + 1 pixels on each axis.
    ///     Three passes come close to a Gaussian whose standard deviation is about the radius.
    /// </summary>
    public static TileImage Blur(TileImage source, int radius, int passes = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius);

        var size = source.Size;
        var result = new TileImage(size);
        var scratch = new TileImage(size);
        var columnSums = new float[size];
        var weight = 1.0f / (2 * radius + 1);

        source.Pixels.CopyTo(result.Pixels, 0);

        for (var pass = 0; pass < passes; pass++)
        {
            BoxRows(result.Pixels, scratch.Pixels, size, radius, weight);
            BoxColumns(scratch.Pixels, result.Pixels, columnSums, size, radius, weight);
        }

        return result;
    }

    private static void BoxRows(float[] source, float[] target, int size, int radius, float weight)
    {
        var mask = size - 1;

        for (var y = 0; y < size; y++)
        {
            var row = y * size;
            var sum = 0.0f;

            for (var offset = -radius; offset <= radius; offset++)
            {
                sum += source[row + (offset & mask)];
            }

            for (var x = 0; x < size; x++)
            {
                target[row + x] = sum * weight;
                sum += source[row + ((x + radius + 1) & mask)] - source[row + ((x - radius) & mask)];
            }
        }
    }

    // Keeps a running sum per column and walks the rows, so every read is a whole row:
    private static void BoxColumns(float[] source, float[] target, float[] sums, int size, int radius, float weight)
    {
        var mask = size - 1;

        Array.Clear(sums);

        for (var offset = -radius; offset <= radius; offset++)
        {
            var row = (offset & mask) * size;

            for (var x = 0; x < size; x++)
            {
                sums[x] += source[row + x];
            }
        }

        for (var y = 0; y < size; y++)
        {
            var row = y * size;
            var entering = ((y + radius + 1) & mask) * size;
            var leaving = ((y - radius) & mask) * size;

            for (var x = 0; x < size; x++)
            {
                target[row + x] = sums[x] * weight;
                sums[x] += source[entering + x] - source[leaving + x];
            }
        }
    }
}
