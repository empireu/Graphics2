using System.Runtime.CompilerServices;

namespace Graphics2.Data;

/// <summary>
///     Represents a writable view over a 3D grid of <see cref="T"/>.
/// </summary>
public readonly ref struct Span3D<T> where T : struct
{
    private readonly Span<T> _data;

    public readonly int SizeX;
    public readonly int SizeY;
    public readonly int SizeZ;

    public Span3D(Span<T> data, int sizeX, int sizeY, int sizeZ)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeZ);

        var length = (long)sizeX * sizeY * sizeZ;

        if (data.Length != length)
        {
            throw new ArgumentException(
                $"The span holds {data.Length} elements but a {sizeX}x{sizeY}x{sizeZ} box needs {length}.",
                nameof(data)
            );
        }

        _data = data;
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
    }

    public bool IsEmpty => _data.IsEmpty;

    public int Length => _data.Length;

    public Span<T> Data => _data;

    /// <summary>
    ///     Gets a reference to a cell, without bounds checking for the coordinates.
    /// </summary>
    public ref T this[int x, int y, int z]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _data[x + y * SizeX + z * SizeX * SizeY];
    }

    /// <summary>
    ///     Gets a reference to a cell, with bounds checking for the coordinates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetChecked(int x, int y, int z)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(x);
        ArgumentOutOfRangeException.ThrowIfNegative(y);
        ArgumentOutOfRangeException.ThrowIfNegative(z);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(x, SizeX);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(y, SizeY);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(z, SizeZ);

        return ref this[x, y, z];
    }
}
