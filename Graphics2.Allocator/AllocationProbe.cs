namespace Graphics2.Allocator;

internal readonly struct AllocationProbe<THandle>(MemoryBlock<THandle> block, int rangeIndex, long offset, long size, long waste)
{
    public readonly MemoryBlock<THandle> Block = block;
    public readonly int RangeIndex = rangeIndex;
    public readonly long Offset = offset;
    public readonly long Size = size;
    public readonly long Waste = waste;
}
