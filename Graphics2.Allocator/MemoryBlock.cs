using System.Runtime.CompilerServices;

namespace Graphics2.Allocator;

/// <summary>
///     Large, continuous page of memory.
///     Has routines for checking what the best-fit allocation would be for a request, committing it and freeing the allocations.
///     The algorithm uses a free-list over the block. The best-fit allocation minimizes the leftover space in the region after the allocation.
///     The padding is returned to the free list as another free-range.
///     Freeing an allocation will coalesce the neighboring free-ranges into a larger final range. 
/// </summary>
public sealed class MemoryBlock<THandle>
{
    private readonly List<FreeRange> _freeRanges = [];

    public readonly int Id;
    public readonly long Capacity;
    public readonly THandle Handle;
    
    public long UsedBytes => Capacity - FreeBytes;

    public long FreeBytes { get; private set; }

    internal IReadOnlyList<FreeRange> FreeRanges => _freeRanges;

    public int FreeRangeCount => _freeRanges.Count;
    
    public MemoryBlock(int id, long capacity, THandle handle)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        Id = id;
        Capacity = capacity;
        FreeBytes = capacity;
        Handle = handle;

        _freeRanges.Add(new FreeRange(0, capacity));
    }

    public Allocation? Allocate(long size, long alignment)
    {
        var probe = Probe(size, alignment);
        
        return probe.HasValue 
            ? Commit(probe.Value)
            : null;
    }

    internal AllocationProbe<THandle>? Probe(long size, long alignment)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be positive.");
        }

        if (alignment <= 0 || !IsPowerOfTwo(alignment))
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be a positive power of two.");
        }

        var bestIndex = -1;
        var bestWaste = long.MaxValue;
        var bestAlignedOffset = 0L;

        for (var i = 0; i < _freeRanges.Count; i++)
        {
            var range = _freeRanges[i];
            var alignedOffset = AlignUp(range.Offset, alignment);
            var padding = alignedOffset - range.Offset;

            if (padding >= range.Size)
            {
                continue;
            }

            var usable = range.Size - padding;
            if (usable < size)
            {
                continue;
            }

            var waste = usable - size;
            if (waste < bestWaste)
            {
                bestIndex = i;
                bestWaste = waste;
                bestAlignedOffset = alignedOffset;
            }
        }

        if (bestIndex < 0)
        {
            return null;
        }

        return new AllocationProbe<THandle>(this, bestIndex, bestAlignedOffset, size, bestWaste);
    }

    internal Allocation Commit(in AllocationProbe<THandle> probe)
    {
        var chosen = _freeRanges[probe.RangeIndex];
        var allocEnd = probe.Offset + probe.Size;

        var hasBefore = probe.Offset > chosen.Offset;
        var hasAfter = allocEnd < chosen.End;

        if (hasBefore && hasAfter)
        {
            _freeRanges[probe.RangeIndex] = new FreeRange(chosen.Offset, probe.Offset - chosen.Offset);
            _freeRanges.Insert(probe.RangeIndex + 1, new FreeRange(allocEnd, chosen.End - allocEnd));
        }
        else if (hasAfter)
        {
            _freeRanges[probe.RangeIndex] = new FreeRange(allocEnd, chosen.End - allocEnd);
        }
        else if (hasBefore)
        {
            _freeRanges[probe.RangeIndex] = new FreeRange(chosen.Offset, probe.Offset - chosen.Offset);
        }
        else
        {
            _freeRanges.RemoveAt(probe.RangeIndex);
        }

        FreeBytes -= probe.Size;

        return new Allocation(Id, probe.Offset, probe.Size);
    }

    public void Free(in Allocation allocation)
    {
        if (allocation.BlockId != Id)
        {
            throw new ArgumentException("Allocation does not belong to this block.", nameof(allocation));
        }

        var offset = allocation.Offset;
        var end = offset + allocation.Size;

        var insertIndex = _freeRanges.FindIndex(r => r.Offset > offset);
        if (insertIndex < 0)
        {
            insertIndex = _freeRanges.Count;
        }

        var coalescedLeft = false;
        if (insertIndex > 0 && _freeRanges[insertIndex - 1].End == offset)
        {
            var left = _freeRanges[insertIndex - 1];
            _freeRanges[insertIndex - 1] = new FreeRange(left.Offset, left.Size + allocation.Size);

            if (insertIndex < _freeRanges.Count && _freeRanges[insertIndex].Offset == end)
            {
                var right = _freeRanges[insertIndex];
                _freeRanges[insertIndex - 1] = new FreeRange(left.Offset, left.Size + allocation.Size + right.Size);
                _freeRanges.RemoveAt(insertIndex);
            }

            coalescedLeft = true;
        }

        if (coalescedLeft)
        {
            FreeBytes += allocation.Size;
            return;
        }

        if (insertIndex < _freeRanges.Count && _freeRanges[insertIndex].Offset == end)
        {
            var right = _freeRanges[insertIndex];
            _freeRanges[insertIndex] = new FreeRange(offset, allocation.Size + right.Size);
        }
        else
        {
            _freeRanges.Insert(insertIndex, new FreeRange(offset, allocation.Size));
        }

        FreeBytes += allocation.Size;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long AlignUp(long value, long alignment)
    {
        var mask = alignment - 1;
        return (value + mask) & ~mask;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsPowerOfTwo(long value)
    {
        return value > 0 && (value & (value - 1)) == 0;
    }
}