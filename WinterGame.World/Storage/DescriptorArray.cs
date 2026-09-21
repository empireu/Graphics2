using System.Runtime.CompilerServices;

namespace WinterGame.World.Storage;

public readonly struct DescriptorArray(BrickDescriptor[] array, int pageIndex, int address) : IEquatable<DescriptorArray>
{
    private readonly BrickDescriptor[] _array = array;
        
    public readonly int PageIndex = pageIndex;
    public readonly int Address = address;

    public Span<BrickDescriptor> Span
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => new(_array, Address, Section.DescriptorsPerSection);
    }

    public bool Equals(DescriptorArray other)
    {
        return ReferenceEquals(_array, other._array) && PageIndex == other.PageIndex && Address == other.Address;
    }

    public override bool Equals(object? obj)
    {
        return obj is DescriptorArray other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(_array, PageIndex, Address);
    }

    public static bool operator ==(DescriptorArray left, DescriptorArray right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(DescriptorArray left, DescriptorArray right)
    {
        return !left.Equals(right);
    }
}