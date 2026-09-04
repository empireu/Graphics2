namespace Graphics2.Allocator;

internal readonly struct AllocationProbe(int rangeIndex, long offset, long size, long waste)
{
    public readonly int RangeIndex = rangeIndex;
    public readonly long Offset = offset;
    public readonly long Size = size;
    public readonly long Waste = waste;
}
