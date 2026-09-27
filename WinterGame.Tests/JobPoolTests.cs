using WinterGame.Jobs;

// ReSharper disable RedundantArgumentDefaultValue

namespace WinterGame.Tests;

public class JobPoolTests
{
    private static JobPool CreatePool()
    {
        return new JobPool(new JobPoolDescription { ThreadCount = 1 });
    }

    [Fact]
    public void CreateJob_StartsIncompleteAndCompletesAfterEveryUnit()
    {
        var pool = CreatePool();
        var job = pool.CreateJob(2);

        Assert.False(pool.TryComplete(job));

        Assert.False(pool.Decrement(job.Slot));
        Assert.False(pool.TryComplete(job));

        Assert.True(pool.Decrement(job.Slot));
        Assert.True(pool.TryComplete(job));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateJob_RefusesCountBelowOne(int count)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreatePool().CreateJob(count));
    }

    [Fact]
    public void AddWork_KeepsTheJobIncompleteUntilTheAddedWorkFinishes()
    {
        var pool = CreatePool();
        var job = pool.CreateJob(1);

        pool.AddWork(job, 2);
        Assert.False(pool.TryComplete(job));

        Assert.False(pool.Decrement(job.Slot));
        Assert.False(pool.Decrement(job.Slot));
        Assert.False(pool.TryComplete(job));

        Assert.True(pool.Decrement(job.Slot));
        Assert.True(pool.TryComplete(job));
    }

    [Fact]
    public void AddWork_OnACompletedJob_Throws()
    {
        var pool = CreatePool();
        var job = pool.CreateJob(1);
        Assert.True(pool.Decrement(job.Slot));

        Assert.Throws<InvalidOperationException>(() => pool.AddWork(job, 1));
    }

    [Fact]
    public void Release_BeforeCompletion_Throws()
    {
        var pool = CreatePool();
        var job = pool.CreateJob(1);

        Assert.Throws<InvalidOperationException>(() => pool.Release(job));
    }

    [Fact]
    public void Release_ReusesTheSlotWithANewGeneration()
    {
        var pool = CreatePool();
        var job = pool.CreateJob(1);
        var slot = job.Slot;

        Assert.True(pool.Decrement(slot));
        pool.Release(job);

        var reused = pool.CreateJob(1);

        Assert.Equal(slot, reused.Slot);
        Assert.Equal(job.Generation + 1, reused.Generation);
        Assert.NotEqual(job, reused);
    }

    [Fact]
    public void TryComplete_OnAReleasedHandle_Throws()
    {
        var pool = CreatePool();
        var job = pool.CreateJob(1);
        Assert.True(pool.Decrement(job.Slot));
        pool.Release(job);

        Assert.Throws<InvalidOperationException>(() => pool.TryComplete(job));
    }

    [Fact]
    public void JobFromAnotherPool_Throws()
    {
        var pool = CreatePool();
        var otherPool = CreatePool();
        var job = otherPool.CreateJob(1);

        Assert.Throws<ArgumentException>(() => pool.TryComplete(job));
        Assert.Throws<ArgumentException>(() => pool.Release(job));
    }

    [Fact]
    public void AvailableCounters_FallsOnCreateAndRisesOnRelease()
    {
        var pool = CreatePool();
        var start = pool.AvailableCounters;

        var job = pool.CreateJob(1);
        Assert.Equal(start - 1, pool.AvailableCounters);

        Assert.True(pool.Decrement(job.Slot));
        pool.Release(job);
        Assert.Equal(start, pool.AvailableCounters);
    }

    [Fact]
    public void CreateJob_ExhaustsTheSlabAndThenThrows()
    {
        var pool = CreatePool();

        for (var i = 0; i < JobPool.MaxCounters; i++)
        {
            pool.CreateJob(1);
        }

        Assert.Equal(0, pool.AvailableCounters);
        Assert.Throws<InvalidOperationException>(() => pool.CreateJob(1));
    }
}
