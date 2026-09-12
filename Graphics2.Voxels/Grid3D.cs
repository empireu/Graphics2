using System.Runtime.CompilerServices;

namespace Graphics2.Voxels;

public class Grid3D<T> where T : struct
{
    public readonly int SizeX;
    public readonly int SizeY;
    public readonly int SizeZ;

    public readonly T[] Data;
    
    public Grid3D(int sizeX, int sizeY, int sizeZ)
    {
        SizeX = sizeX;
        SizeY = sizeY;
        SizeZ = sizeZ;
        
        Data = new T[SizeX * SizeY * SizeZ];
    }

    public Grid3D(int size) : this(size, size, size) { }
    
    /// <summary>
    ///     Accessor without grid bounds checking.
    /// </summary>
    public ref T this[int x, int y, int z] => ref Data[z * SizeX * SizeY + y * SizeX + x];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetChecked(int x, int y, int z)
    {
        if (x < 0 || x >= SizeX)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }
        
        if (y < 0 || y >= SizeY)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }
        
        if (z < 0 || z >= SizeZ)
        {
            throw new ArgumentOutOfRangeException(nameof(z));
        }

        return ref this[x, y, z];
    }
}