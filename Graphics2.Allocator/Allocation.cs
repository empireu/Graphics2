namespace Graphics2.Allocator;

public readonly struct Allocation(int blockId, long offset, long size) : IEquatable<Allocation>
{
    public readonly int BlockId = blockId;
    public readonly long Offset = offset;
    public readonly long Size = size;

    public bool Equals(Allocation other) => BlockId == other.BlockId && Offset == other.Offset && Size == other.Size;
    public override bool Equals(object? obj) => obj is Allocation other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(BlockId, Offset, Size);
    public static bool operator ==(Allocation left, Allocation right) => left.Equals(right);
    public static bool operator !=(Allocation left, Allocation right) => !left.Equals(right);
    public override string ToString() => $"Block {BlockId} @ 0x{Offset:X} +{Size}";
}
