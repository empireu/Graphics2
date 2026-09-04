namespace Graphics2.Allocator;

internal readonly struct FreeRange(long offset, long size)
{
    public readonly long Offset = offset;
    public readonly long Size = size;

    public long End => Offset + Size;

    public override string ToString() => $"[0x{Offset:X}..0x{End:X}) +{Size}";
}
