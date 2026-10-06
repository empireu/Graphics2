using Graphics2.Allocator;

namespace Graphics2.Tests;

public class AllocatorTests
{
    private static MemoryBlock<int> NewBlock(int id, long capacity) => new(id, capacity, id);

    private static BlockAllocator<int> NewAllocator(long defaultBlockSize, out MockAllocationLayer layer)
    {
        layer = new MockAllocationLayer();
        return new BlockAllocator<int>(defaultBlockSize, layer);
    }

    [Fact]
    public void MemoryBlock_AllocateAtOffsetZero()
    {
        var block = NewBlock(0, 1024);
        var a = block.Allocate(100, 1);

        Assert.NotNull(a);
        Assert.Equal(0, a.Value.Offset);
        Assert.Equal(100, a.Value.Size);
        Assert.Equal(0, a.Value.BlockId);
        Assert.Equal(100, block.UsedBytes);
        Assert.Equal(924, block.FreeBytes);
    }

    [Fact]
    public void MemoryBlock_AllocateSequential()
    {
        var block = NewBlock(0, 1024);

        var a = block.Allocate(100, 1);
        var b = block.Allocate(200, 1);
        var c = block.Allocate(300, 1);

        Assert.Equal(0, a!.Value.Offset);
        Assert.Equal(100, b!.Value.Offset);
        Assert.Equal(300, c!.Value.Offset);
        Assert.Equal(600, block.UsedBytes);
    }

    [Fact]
    public void MemoryBlock_AllocateUntilFull()
    {
        var block = NewBlock(0, 1000);
        var a = block.Allocate(500, 1);
        var b = block.Allocate(500, 1);
        var c = block.Allocate(1, 1);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Null(c);
        Assert.Equal(1000, block.UsedBytes);
        Assert.Equal(0, block.FreeBytes);
    }

    [Fact]
    public void MemoryBlock_AllocateReturnsNullWhenDoesNotFit()
    {
        var block = NewBlock(0, 100);
        var alloc = block.Allocate(200, 1);

        Assert.Null(alloc);
    }

    [Fact]
    public void MemoryBlock_AlignmentRoundsUpOffset()
    {
        var block = NewBlock(0, 1024);
        _ = block.Allocate(10, 1); // [0, 10)
        var b = block.Allocate(1, 256); // aligned to 256

        Assert.Equal(256, b!.Value.Offset);
    }

    [Fact]
    public void MemoryBlock_AlignmentExactFit()
    {
        var block = NewBlock(0, 256);
        var alloc = block.Allocate(256, 256);

        Assert.NotNull(alloc);
        Assert.Equal(0, alloc.Value.Offset);
        Assert.Equal(0, block.FreeBytes);
    }

    [Fact]
    public void MemoryBlock_AlignmentNoFitAfterRounding()
    {
        var block = NewBlock(0, 100);
        _ = block.Allocate(90, 1); // [0, 90), 10 bytes free
        var b = block.Allocate(1, 256); // would align to 256, doesn't fit

        Assert.Null(b);
    }

    [Fact]
    public void MemoryBlock_AlignmentLeavesPaddingFreeRange()
    {
        var block = NewBlock(0, 512);
        _ = block.Allocate(10, 1); // [0, 10)
        var b = block.Allocate(4, 256); // aligned to 256 -> [256, 260)

        Assert.Equal(256, b!.Value.Offset);

        // Free ranges are [10, 256) padding, [260, 512) leftover
        Assert.Equal(2, block.FreeRanges.Count);
        Assert.Equal(10, block.FreeRanges[0].Offset);
        Assert.Equal(246, block.FreeRanges[0].Size);
        Assert.Equal(260, block.FreeRanges[1].Offset);
        Assert.Equal(252, block.FreeRanges[1].Size);
    }

    [Fact]
    public void MemoryBlock_BestFitPicksSmallestWaste()
    {
        var block = NewBlock(0, 1000);

        // Create two free ranges, [0, 200) and [600, 1000) (size 400)
        var a = block.Allocate(200, 1); // [0, 200)
        _ = block.Allocate(400, 1); // [200, 600)
        // Free a gives [0, 200) free, [600, 1000) free
        block.Free(a!.Value);

        // Request 100. Best fit should pick [0, 200) (wasts 100) over [600, 1000) (wastes 300)
        var c = block.Allocate(100, 1);

        Assert.Equal(0, c!.Value.Offset);

        // [100, 200) remains free, [600, 1000) untouched
        Assert.Equal(2, block.FreeRanges.Count);
        Assert.Equal(100, block.FreeRanges[0].Offset);
        Assert.Equal(100, block.FreeRanges[0].Size);
        Assert.Equal(600, block.FreeRanges[1].Offset);
        Assert.Equal(400, block.FreeRanges[1].Size);
    }

    [Fact]
    public void MemoryBlock_FreeCoalescesLeft()
    {
        var block = NewBlock(0, 300);
        var a = block.Allocate(100, 1);
        var b = block.Allocate(100, 1);
        _ = block.Allocate(100, 1);

        // Free a then b should coalesce with the free-range before it
        block.Free(a!.Value);
        block.Free(b!.Value);

        // Should have one free-range [0, 200) and one used [200, 300)
        Assert.Single(block.FreeRanges);
        Assert.Equal(0, block.FreeRanges[0].Offset);
        Assert.Equal(200, block.FreeRanges[0].Size);
    }

    [Fact]
    public void MemoryBlock_FreeCoalescesRight()
    {
        var block = NewBlock(0, 300);
        _ = block.Allocate(100, 1);
        var b = block.Allocate(100, 1);
        var c = block.Allocate(100, 1);

        // Free c, then b should coalesce with the free-range after it
        block.Free(c!.Value);
        block.Free(b!.Value);

        Assert.Single(block.FreeRanges);
        Assert.Equal(100, block.FreeRanges[0].Offset);
        Assert.Equal(200, block.FreeRanges[0].Size);
    }

    [Fact]
    public void MemoryBlock_FreeCoalescesBoth()
    {
        var block = NewBlock(0, 300);
        var a = block.Allocate(100, 1);
        var b = block.Allocate(100, 1);
        var c = block.Allocate(100, 1);

        // Free a and c first, then b should coalesce with both
        block.Free(a!.Value);
        block.Free(c!.Value);
        block.Free(b!.Value);

        Assert.Single(block.FreeRanges);
        Assert.Equal(0, block.FreeRanges[0].Offset);
        Assert.Equal(300, block.FreeRanges[0].Size);
        Assert.Equal(0, block.UsedBytes);
    }

    [Fact]
    public void MemoryBlock_FreeNonAdjacentDoesNotCoalesce()
    {
        var block = NewBlock(0, 300);
        var a = block.Allocate(100, 1);
        _ = block.Allocate(100, 1);
        var c = block.Allocate(100, 1);

        // Free a and c. Not adjacent, should be two separate free-ranges
        block.Free(a!.Value);
        block.Free(c!.Value);

        Assert.Equal(2, block.FreeRanges.Count);
        Assert.Equal(0, block.FreeRanges[0].Offset);
        Assert.Equal(100, block.FreeRanges[0].Size);
        Assert.Equal(200, block.FreeRanges[1].Offset);
        Assert.Equal(100, block.FreeRanges[1].Size);
    }

    [Fact]
    public void MemoryBlock_FullFreeRestoresSingleRange()
    {
        var block = NewBlock(0, 1000);
        var a = block.Allocate(300, 1);
        var b = block.Allocate(400, 1);
        var c = block.Allocate(300, 1);

        block.Free(a!.Value);
        block.Free(b!.Value);
        block.Free(c!.Value);

        Assert.Single(block.FreeRanges);
        Assert.Equal(0, block.FreeRanges[0].Offset);
        Assert.Equal(1000, block.FreeRanges[0].Size);
        Assert.Equal(0, block.UsedBytes);
    }

    [Fact]
    public void MemoryBlock_ReusesFreedSpace()
    {
        var block = NewBlock(0, 1000);
        var a = block.Allocate(500, 1);
        block.Free(a!.Value);
        var b = block.Allocate(500, 1);

        Assert.Equal(0, b!.Value.Offset);
        Assert.Equal(500, block.UsedBytes);
    }

    [Fact]
    public void MemoryBlock_RejectsZeroSize()
    {
        var block = NewBlock(0, 1024);
        Assert.Throws<ArgumentOutOfRangeException>(() => block.Allocate(0, 1));
    }

    [Fact]
    public void MemoryBlock_RejectsZeroAlignment()
    {
        var block = NewBlock(0, 1024);
        Assert.Throws<ArgumentOutOfRangeException>(() => block.Allocate(1, 0));
    }

    [Fact]
    public void MemoryBlock_RejectsNonPowerOfTwoAlignment()
    {
        var block = NewBlock(0, 1024);
        Assert.Throws<ArgumentOutOfRangeException>(() => block.Allocate(1, 3));
    }

    [Fact]
    public void MemoryBlock_FreeRejectsForeignAllocation()
    {
        var block = NewBlock(0, 1024);
        var foreign = new Allocation(999, 0, 10);
        Assert.Throws<ArgumentException>(() => block.Free(foreign));
    }

    [Fact]
    public void BlockAllocator_CreatesBlockOnFirstAllocate()
    {
        var allocator = NewAllocator(1024, out var layer);
        var alloc = allocator.Allocate(100, 1);

        Assert.Single(allocator.Blocks);
        Assert.Equal(0, alloc.BlockId);
        Assert.Equal(0, alloc.Offset);
        Assert.Single(layer.Pages);
        Assert.Equal(1024, layer.Pages[0].Size);
    }

    [Fact]
    public void BlockAllocator_ReusesExistingBlock()
    {
        var allocator = NewAllocator(1024, out var layer);
        var a = allocator.Allocate(100, 1);
        var b = allocator.Allocate(200, 1);

        Assert.Single(allocator.Blocks);
        Assert.Equal(0, a.BlockId);
        Assert.Equal(0, b.BlockId);
        Assert.Equal(100, b.Offset);
        Assert.Single(layer.Pages);
    }

    [Fact]
    public void BlockAllocator_CreatesNewBlockWhenFull()
    {
        var allocator = NewAllocator(1000, out var layer);
        var a = allocator.Allocate(600, 1);
        var b = allocator.Allocate(600, 1);

        Assert.Equal(2, allocator.Blocks.Count);
        Assert.Equal(0, a.BlockId);
        Assert.Equal(1, b.BlockId);
        Assert.Equal(2, layer.Pages.Count);
        Assert.Equal(1000, layer.Pages[0].Size);
        Assert.Equal(1000, layer.Pages[1].Size);
    }

    [Fact]
    public void BlockAllocator_DedicatedAllocationForLargeSize()
    {
        var allocator = NewAllocator(256, out var layer);
        var alloc = allocator.Allocate(512, 1);

        // Should create a dedicated block sized exactly 512
        Assert.Single(allocator.Blocks);
        Assert.Equal(512, allocator.Blocks[0].Capacity);
        Assert.Equal(0, alloc.Offset);
        Assert.Single(layer.Pages);
        Assert.Equal(512, layer.Pages[0].Size);
    }

    [Fact]
    public void BlockAllocator_BestFitAcrossBlocks()
    {
        var allocator = NewAllocator(1000, out _);

        // Fill block 0 almost entirely
        var a = allocator.Allocate(900, 1);
        // Block 1 is created for the next allocation
        _ = allocator.Allocate(900, 1);

        // Free a. Block 0 now has 900 free, block 1 has 100 free
        allocator.Free(a);

        // Allocate 50. Best fit picks the tightest block.
        // Block 0: 900 free. After 50 alloc, 850 free
        // Block 1: 100 free. After 50 alloc, 50 free
        // So, best fit picks block 1
        var c = allocator.Allocate(50, 1);

        Assert.Equal(1, c.BlockId);
    }

    [Fact]
    public void BlockAllocator_FreeAndReuse()
    {
        var allocator = NewAllocator(1024, out var layer);
        var a = allocator.Allocate(512, 1);
        allocator.Free(a);
        var b = allocator.Allocate(512, 1);

        Assert.Single(allocator.Blocks);
        Assert.Equal(0, b.BlockId);
        Assert.Equal(0, b.Offset);
        Assert.Single(layer.Pages);
    }

    [Fact]
    public void BlockAllocator_FreeRejectsUnknownBlock()
    {
        var allocator = NewAllocator(1024, out _);
        var foreign = new Allocation(999, 0, 10);
        Assert.Throws<ArgumentException>(() => allocator.Free(foreign));
    }

    [Fact]
    public void BlockAllocator_GetBaseReturnsBlockHandle()
    {
        var allocator = NewAllocator(1024, out var layer);
        var alloc = allocator.Allocate(100, 1);

        Assert.Equal(layer.Pages[0].Handle, allocator.GetBase(alloc));
    }

    [Fact]
    public void BlockAllocator_DisposeFreesAllPages()
    {
        var allocator = NewAllocator(1000, out var layer);
        _ = allocator.Allocate(600, 1);
        _ = allocator.Allocate(600, 1);

        Assert.Equal(2, layer.Pages.Count);
        Assert.Empty(layer.FreedPages);

        allocator.Dispose();

        Assert.Equal(2, layer.FreedPages.Count);
        Assert.Equal(layer.Pages[0].Handle, layer.FreedPages[0].Handle);
        Assert.Equal(layer.Pages[0].Size, layer.FreedPages[0].Size);
        Assert.Equal(layer.Pages[1].Handle, layer.FreedPages[1].Handle);
        Assert.Equal(layer.Pages[1].Size, layer.FreedPages[1].Size);
        Assert.Empty(allocator.Blocks);
    }

    [Fact]
    public void BlockAllocator_DisposeIsIdempotent()
    {
        var allocator = NewAllocator(1024, out var layer);
        _ = allocator.Allocate(100, 1);

        allocator.Dispose();
        allocator.Dispose();

        Assert.Single(layer.FreedPages);
    }
}

