using WinterGame.Jobs;

namespace WinterGame.Tests;

public class WorkItemTests
{
    [Fact]
    public void Pack_RoundTripsEveryField()
    {
        var workItem = WorkItem.Pack(7, 1234, 56789);

        Assert.Equal(7, WorkItem.ProviderId(workItem));
        Assert.Equal(1234, WorkItem.JobSlot(workItem));
        Assert.Equal(56789, WorkItem.PayloadSlot(workItem));
    }

    [Fact]
    public void Pack_IsolatesTheFieldsAtTheirLimits()
    {
        var workItem = WorkItem.Pack(WorkItem.MaxProviderId, WorkItem.MaxJobSlot, WorkItem.MaxPayloadSlot);

        Assert.Equal(WorkItem.MaxProviderId, WorkItem.ProviderId(workItem));
        Assert.Equal(WorkItem.MaxJobSlot, WorkItem.JobSlot(workItem));
        Assert.Equal(WorkItem.MaxPayloadSlot, WorkItem.PayloadSlot(workItem));
    }

    [Fact]
    public void Pack_OfZeroesIsZero()
    {
        Assert.Equal(0, WorkItem.Pack(0, 0, 0));
    }

    [Fact]
    public void Pack_LeavesTheReservedHighBitsClear()
    {
        var workItem = WorkItem.Pack(WorkItem.MaxProviderId, WorkItem.MaxJobSlot, WorkItem.MaxPayloadSlot);

        Assert.Equal(0UL, (ulong)workItem >> 48);
    }

    [Fact]
    public void JobSlotField_CoversThePoolSlab()
    {
        Assert.Equal(JobPool.MaxCounters - 1, WorkItem.MaxJobSlot);
    }
}
