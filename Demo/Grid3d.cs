namespace Demo;

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
    
    public ref T this[int x, int y, int z] => ref Data[z * SizeX * SizeY + y * SizeX + x];
}