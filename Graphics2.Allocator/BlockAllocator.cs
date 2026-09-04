// ReSharper disable ForCanBeConvertedToForeach
namespace Graphics2.Allocator;

/// <summary>
///     Manager for multiple <see cref="MemoryBlock{T}"/>s.
///     Allocating will probe all blocks to see the best-fit across all of them, then commit.
///     When there isn't enough space, a new block is allocated.
/// </summary>
public sealed class BlockAllocator<THandle> : IDisposable
{
    private readonly long _defaultBlockSize;
    private readonly IAllocationLayer<THandle> _allocationLayer;
    private readonly List<MemoryBlock<THandle>> _blocks = [];
    private readonly Dictionary<int, MemoryBlock<THandle>> _blocksById = new();
    private int _nextBlockId;
    private bool _disposed;
    
    public BlockAllocator(long defaultBlockSize, IAllocationLayer<THandle> allocationLayer)
    {
        if (defaultBlockSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(defaultBlockSize), "Block size must be positive.");
        }

        _defaultBlockSize = defaultBlockSize;
        _allocationLayer = allocationLayer;
    }

    public IReadOnlyList<MemoryBlock<THandle>> Blocks => _blocks;
    
    private MemoryBlock<THandle> GetBlock(int blockId) => !_blocksById.TryGetValue(blockId, out var block) 
        ? throw new ArgumentException($"Allocation block {blockId} not found", nameof(blockId)) 
        : block;

    public THandle GetBase(Allocation allocation) => GetBlock(allocation.BlockId).Handle;
    
    public Allocation Allocate(long size, long alignment)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Size must be positive.");
        }

        AllocationProbe<THandle>? bestProbe = null;
        for (var blockIndex = 0; blockIndex < _blocks.Count; blockIndex++)
        {
            var block = _blocks[blockIndex];
            var probe = block.Probe(size, alignment);

            if (!probe.HasValue || (bestProbe.HasValue && probe.Value.Waste >= bestProbe.Value.Waste))
            {
                continue;
            }
            
            bestProbe = probe;
        }

        if (bestProbe.HasValue)
        {
            return bestProbe.Value.Block.Commit(bestProbe.Value);
        }
        
        var blockSize = Math.Max(_defaultBlockSize, size);
        var blockId = _nextBlockId++;

        var handle = _allocationLayer.AllocatePage(blockSize);
        var newBlock = new MemoryBlock<THandle>(blockId, blockSize, handle);
        
        _blocks.Add(newBlock);
        _blocksById.Add(blockId, newBlock);

        return newBlock.Allocate(size, alignment) ?? throw new InvalidOperationException("Newly created block could not satisfy the allocation.");
    }

    public void Free(Allocation allocation)
    {
        var block = GetBlock(allocation.BlockId);
        
        block.Free(allocation);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var block in _blocks)
        {
            _allocationLayer.FreePage(block.Handle, block.Capacity);
        }

        _blocks.Clear();
        _blocksById.Clear();
    }
}
