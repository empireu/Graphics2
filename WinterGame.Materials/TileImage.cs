using System.Numerics;
using System.Runtime.CompilerServices;
using Graphics2.Mathematics;

namespace WinterGame.Materials;

/// <summary>
///     A square image of floats whose edges wrap, so that an image built from it tiles.
///     The size is a power of two. Pixel (x, y) is centred at the tile coordinates ((x + 0.5) / Size, (y + 0.5) / Size), where 1 is the whole tile.
/// </summary>
public sealed class TileImage
{
    public readonly int Size;

    /// <summary>
    ///     The pixels row after row, x varying fastest.
    /// </summary>
    public readonly float[] Pixels;

    private readonly int _log;
    private readonly int _mask;

    public TileImage(int size)
    {
        if (!MathX.IsPowerOfTwo(size))
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "A tile image's size is a power of two.");
        }

        Size = size;
        Pixels = new float[size * size];
        _log = BitOperations.Log2((uint)size);
        _mask = size - 1;
    }

    /// <summary>
    ///     The pixel at (x, y), each coordinate wrapped into the image.
    /// </summary>
    public float this[int x, int y]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Pixels[((y & _mask) << _log) | (x & _mask)];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => Pixels[((y & _mask) << _log) | (x & _mask)] = value;
    }
}
