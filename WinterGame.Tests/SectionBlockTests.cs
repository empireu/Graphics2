using WinterGame.World;

namespace WinterGame.Tests;

public class SectionBlockTests
{
    [Fact]
    public void FromSection_FloorsNegativeSections()
    {
        Assert.Equal(new SectionBlockPos(0, 0, 0), SectionBlockPos.FromSection(new SectionPos(0, 3, 1)));
        Assert.Equal(new SectionBlockPos(1, 0, 0), SectionBlockPos.FromSection(new SectionPos(4, 0, 0)));
        Assert.Equal(new SectionBlockPos(-1, -1, -1), SectionBlockPos.FromSection(new SectionPos(-1, -4, -3)));
        Assert.Equal(new SectionBlockPos(-2, 0, 0), SectionBlockPos.FromSection(new SectionPos(-5, 0, 0)));
    }

    [Fact]
    public void GetSection_AndGetLocalIndex_RoundTripEverySectionOfBlocksAroundZero()
    {
        for (var z = -9; z <= 9; z++)
        {
            for (var y = -9; y <= 9; y++)
            {
                for (var x = -9; x <= 9; x++)
                {
                    var position = new SectionPos(x, y, z);
                    var block = SectionBlockPos.FromSection(position);
                    var index = SectionBlockPos.GetLocalIndex(position);

                    Assert.InRange(index, 0, SectionBlockPos.SectionsPerBlock - 1);
                    Assert.Equal(position, block.GetSection(index));
                }
            }
        }
    }

    [Fact]
    public void GetLocalIndex_PutsTheSectionsOfARowInAdjacentBits()
    {
        for (var x = 0; x < SectionBlockPos.BlockSize; x++)
        {
            Assert.Equal(SectionBlockPos.GetLocalIndex(0, 2, 3) + x, SectionBlockPos.GetLocalIndex(-4 + x, 2, 3));
        }
    }

    [Fact]
    public void Constructor_FromAKey_UnpacksToTheSameBlock()
    {
        var block = new SectionBlockPos(-7, 3, 9);

        Assert.Equal(block, new SectionBlockPos(block.GetKey()));
    }

    [Fact]
    public void Allocate_HandsOutZeroedBlocksThatDoNotShareStorage()
    {
        var allocator = new SectionBlockAllocator();
        var first = allocator.Allocate();
        var second = allocator.Allocate();

        Assert.NotEqual(first, second);

        var firstView = allocator.Get(first);
        var secondView = allocator.Get(second);

        firstView.TicketCounts.Fill(7);
        firstView.Slots.Fill(9);

        for (var index = 0; index < SectionBlockPos.SectionsPerBlock; index++)
        {
            firstView.SetReady(index, index, SectionContentSides.FromOneSide(ContentSide.Solid));
        }

        Assert.False(secondView.TicketCounts.ContainsAnyExcept(0));
        Assert.False(secondView.ContentSides.ContainsAnyExcept(default(SectionContentSides)));
        Assert.Equal(0UL, secondView.Occupied);
        Assert.Equal(7, allocator.Get(first).TicketCounts[SectionBlockPos.SectionsPerBlock - 1]);
        Assert.Equal(ulong.MaxValue, allocator.Get(first).GetMask(SectionBlockView.ReadyMask));
        Assert.Equal(SectionContentSides.FromOneSide(ContentSide.Solid), allocator.GetContentSides(first, SectionBlockPos.SectionsPerBlock - 1));
    }

    [Fact]
    public void Free_ReturnsTheBlockForTheNextAllocation()
    {
        var allocator = new SectionBlockAllocator();
        var block = allocator.Allocate();

        allocator.Free(block);

        Assert.Equal(block, allocator.Allocate());
    }

    [Fact]
    public void Allocate_PastOnePage_KeepsEveryBlockDistinct()
    {
        var allocator = new SectionBlockAllocator();
        var indices = new HashSet<int>();

        for (var count = 0; count < 3000; count++)
        {
            var block = allocator.Allocate();

            Assert.True(indices.Add(block.Index), $"Block {block.Index} was handed out twice.");

            allocator.Get(block).TicketCounts[0] = block.Index;
        }

        foreach (var index in indices)
        {
            Assert.Equal(index, allocator.Get(new SectionBlock(index)).TicketCounts[0]);
        }
    }
}
