using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace WinterGame.World.Storage;

public readonly struct VoxelArrayHandle(int pageIndex, int address) : IEquatable<VoxelArrayHandle>
{
    public readonly int PageIndex = pageIndex;
    public readonly int Address = address;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint Pack()
    {
        Debug.Assert(PageIndex is >= 0 and <= SectionArena.MaxPageNumber, $"Cannot pack page index {PageIndex}");
        Debug.Assert(Address is >= 0 and <= SectionArena.MaxAddressNumber, $"Cannot pack address {Address}");
            
        var result = 0u;

        result |= (uint)PageIndex;
        result |= (uint)(Address << SectionArena.PageNumberBits);

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static VoxelArrayHandle Unpack(uint data)
    {
        var pageIndex = data & SectionArena.MaxPageNumber;
        var address = (data >> SectionArena.PageNumberBits) & SectionArena.MaxAddressNumber;

        return new VoxelArrayHandle((int)pageIndex, (int)address);
    }
        
    public bool Equals(VoxelArrayHandle other)
    {
        return PageIndex == other.PageIndex && Address == other.Address;
    }

    public override bool Equals(object? obj)
    {
        return obj is VoxelArrayHandle other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(PageIndex, Address);
    }

    public static bool operator ==(VoxelArrayHandle left, VoxelArrayHandle right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(VoxelArrayHandle left, VoxelArrayHandle right)
    {
        return !left.Equals(right);
    }
}

public readonly struct VoxelArray(byte[] array, int pageIndex, int address) : IEquatable<VoxelArray>
{
    private readonly byte[] _array = array;

    public readonly int PageIndex = pageIndex;
    public readonly int Address = address;

    public Span<byte> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_array, Address, Section.VoxelsPerBrick);
    }
    
    public VoxelArrayHandle Handle => new(PageIndex, Address);

    public bool Equals(VoxelArray other)
    {
        return ReferenceEquals(_array, other._array) && PageIndex == other.PageIndex && Address == other.Address;
    }

    public override bool Equals(object? obj)
    {
        return obj is VoxelArray other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(_array, PageIndex, Address);
    }

    public static bool operator ==(VoxelArray left, VoxelArray right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(VoxelArray left, VoxelArray right)
    {
        return !left.Equals(right);
    }
}