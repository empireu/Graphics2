namespace Graphics2.Data;

/// <summary>
///     Region of a 3D grid of <see cref="T"/> backed by a managed array.
/// </summary>
public readonly struct Memory3D<T> where T : struct
{
    private readonly T[]? _array;
    private readonly int _offset;

    public readonly int SizeX;
    public readonly int SizeY;
    public readonly int SizeZ;

    public Memory3D(T[] array, int sizeX, int sizeY, int sizeZ) : this(array, 0, sizeX, sizeY, sizeZ)
    {
        
    }
    
    public Memory3D(T[] array, int offset, int sizeX, int sizeY, int sizeZ)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sizeZ);

        var length = (long)sizeX * sizeY * sizeZ;

        if (offset + length > array.Length)
        {
            throw new ArgumentException(
                $"The array holds {array.Length} elements but a {sizeX}x{sizeY}x{sizeZ} box at offset {offset} needs {offset + length}.",
                nameof(array)
            );
        }

        _array = array;
        _offset = offset;
        
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
    }

    public bool IsEmpty => _array == null;

    public int Length => _array == null ? 0 : SizeX * SizeY * SizeZ;

    /// <summary>
    ///     Gets a writeable view over the region.
    /// </summary>
    public Span3D<T> Span
    {
        get
        {
            if (_array == null)
            {
                return default;
            }

            return new Span3D<T>(_array.AsSpan(_offset, SizeX * SizeY * SizeZ), SizeX, SizeY, SizeZ);
        }
    }

    /// <summary>
    ///     Allocates a 3D grid as a managed array.
    /// </summary>
    public static Memory3D<T> Allocate(int width, int height, int depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);

        return new Memory3D<T>(new T[(long)width * height * depth], width, height, depth);
    }
}
