namespace Graphics2.Allocator;

/// <summary>
///     Manager for multiple <see cref="MemoryBlock"/>s.
///     Allocating will probe all blocks to see the best-fit across all of them, then commit.
///     When there isn't enough space, a new block is allocated.
/// </summary>
public sealed class BlockAllocator
{
    private readonly List<MemoryBlock> _blocks = [];
    private readonly long _defaultBlockSize;
    private int _nextBlockId;

    public IReadOnlyList<MemoryBlock> Blocks => _blocks;

    public BlockAllocator(long defaultBlockSize)
    {
        if (defaultBlockSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultBlockSize), "Block size must be positive.");
        }

        _defaultBlockSize = defaultBlockSize;
    }

    public Allocation Allocate(long size, long alignment)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be positive.");
        }

        MemoryBlock? bestBlock = null;
        AllocationProbe? bestProbe = null;

        foreach (var block in _blocks)
        {
            var probe = block.Probe(size, alignment);
            if (probe is null)
            {
                continue;
            }

            if (bestProbe is null || probe.Value.Waste < bestProbe.Value.Waste)
            {
                bestBlock = block;
                bestProbe = probe;
            }
        }

        if (bestBlock is not null && bestProbe is not null)
        {
            return bestBlock.Commit(bestProbe.Value);
        }

        var blockSize = Math.Max(_defaultBlockSize, size);
        var blockId = _nextBlockId++;
        var newBlock = new MemoryBlock(blockId, blockSize);
        var allocFromNew = newBlock.Allocate(size, alignment)
            ?? throw new InvalidOperationException("Newly created block could not satisfy the allocation.");

        _blocks.Add(newBlock);
        return allocFromNew;
    }

    public void Free(in Allocation allocation)
    {
        var blockId = allocation.BlockId;
        var block = _blocks.FirstOrDefault(b => b.Id == blockId);
        
        if (block == null)
        {
            throw new ArgumentException("Allocation block not found", nameof(allocation));
        }

        block.Free(in allocation);
    }
}
