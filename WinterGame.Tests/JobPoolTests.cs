using WinterGame.Jobs;

// ReSharper disable RedundantArgumentDefaultValue

namespace WinterGame.Tests;

public class JobPoolTests
{
    private static JobPool CreatePool()
    {
        return new JobPool();
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
        Assert.False(pool.TryComplete(reused));
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

    [Fact]
    public void Complete_BlocksUntilTheLastUnitFinishes()
    {
        var pool = CreatePool();
        var job = default(Job);
        var created = new ManualResetEventSlim(false);
        var returned = new ManualResetEventSlim(false);

        // Created on the waiter, because only the creating thread may wait on a job.
        var waiter = new Thread(() =>
        {
            job = pool.CreateJob(1);
            created.Set();
            pool.Complete(job);
            returned.Set();
        })
        {
            IsBackground = true,
            Name = "JobPoolWaiter",
        };

        waiter.Start();
        Assert.True(created.Wait(TimeSpan.FromSeconds(10)), "The waiter never created its job.");

        // The counter still holds a unit, so the waiter cannot come back yet.
        Assert.False(returned.Wait(TimeSpan.FromMilliseconds(250)));

        Assert.True(pool.Decrement(job.Slot));

        Assert.True(returned.Wait(TimeSpan.FromSeconds(10)), "Complete did not return after the last unit finished.");
        Assert.True(waiter.Join(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Complete_ReturnsImmediatelyWhenTheJobIsAlreadyDone()
    {
        var pool = CreatePool();

        RunWithTimeout(() =>
        {
            var job = pool.CreateJob(1);
            pool.Decrement(job.Slot);
            pool.Complete(job);
        }, "Complete blocked on a finished job");
    }

    private static void RunWithTimeout(Action action, string message)
    {
        var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            action();
            done.Set();
        })
        {
            IsBackground = true,
        };

        thread.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(10)), message);
    }
}
