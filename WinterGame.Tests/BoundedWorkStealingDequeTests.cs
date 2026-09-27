using WinterGame.Jobs;

namespace WinterGame.Tests;

public class BoundedWorkStealingDequeTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(64)]
    [InlineData(1024)]
    [InlineData(65536)]
    public void Capacity_IsTheRequestedPowerOfTwo(int capacity)
    {
        Assert.Equal(capacity, new BoundedWorkStealingDeque(capacity).Capacity);
    }

    [Fact]
    public void Capacity_AcceptsTheMinimum()
    {
        Assert.Equal(BoundedWorkStealingDeque.MinCapacity, new BoundedWorkStealingDeque(BoundedWorkStealingDeque.MinCapacity).Capacity);
    }

    [Fact]
    public void Capacity_RefusesLessThanTheMinimum()
    {
        // One is a power of two, so it reaches the minimum check instead of the power-of-two check.
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedWorkStealingDeque(BoundedWorkStealingDeque.MinCapacity - 1));
    }

    [Fact]
    public void Capacity_DefaultsToASupportedValue()
    {
        Assert.Equal(1024, new BoundedWorkStealingDeque().Capacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(100)]
    public void Capacity_RefusesAValueThatIsNotAPowerOfTwo(int capacity)
    {
        Assert.Throws<ArgumentException>(() => new BoundedWorkStealingDeque(capacity));
    }

    [Theory]
    [InlineData(131072)]
    [InlineData(262144)]
    [InlineData(1048576)]
    public void Capacity_RefusesMoreThanTheSupportedMaximum(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedWorkStealingDeque(capacity));
    }

    [Fact]
    public void NewDeque_IsEmpty()
    {
        var deque = new BoundedWorkStealingDeque(4);

        Assert.Equal(0, deque.ApproximateCount);
        Assert.False(deque.PopBottom(out var popped));
        Assert.Equal(-1L, popped);
        Assert.False(deque.StealTop(out var stolen));
        Assert.Equal(-1L, stolen);
        Assert.Equal(0, deque.ApproximateCount);
    }

    [Fact]
    public void PopBottom_ReturnsTheMostRecentlyPushedHandleFirst()
    {
        var deque = new BoundedWorkStealingDeque(8);

        for (var handle = 1; handle <= 5; handle++)
        {
            Assert.True(deque.TryPushBottom(handle));
        }

        for (var handle = 5; handle >= 1; handle--)
        {
            Assert.True(deque.PopBottom(out var popped));
            Assert.Equal(handle, popped);
        }

        Assert.False(deque.PopBottom(out _));
    }

    [Fact]
    public void StealTop_ReturnsTheOldestHandleFirst()
    {
        var deque = new BoundedWorkStealingDeque(8);

        for (var handle = 1; handle <= 5; handle++)
        {
            Assert.True(deque.TryPushBottom(handle));
        }

        for (var handle = 1; handle <= 5; handle++)
        {
            Assert.True(deque.StealTop(out var stolen));
            Assert.Equal(handle, stolen);
        }

        Assert.False(deque.StealTop(out _));
    }

    [Fact]
    public void PopAndSteal_TakeOppositeEnds()
    {
        var deque = new BoundedWorkStealingDeque(8);

        for (var handle = 1; handle <= 6; handle++)
        {
            Assert.True(deque.TryPushBottom(handle));
        }

        Assert.True(deque.PopBottom(out var newest));
        Assert.Equal(6L, newest);
        Assert.True(deque.StealTop(out var oldest));
        Assert.Equal(1L, oldest);
        Assert.True(deque.PopBottom(out var secondNewest));
        Assert.Equal(5L, secondNewest);
        Assert.True(deque.StealTop(out var secondOldest));
        Assert.Equal(2L, secondOldest);
        Assert.Equal(2, deque.ApproximateCount);
    }

    [Fact]
    public void TryPushBottom_RefusesWhenTheBufferIsFull()
    {
        var deque = new BoundedWorkStealingDeque(4);

        for (var handle = 1; handle <= 4; handle++)
        {
            Assert.True(deque.TryPushBottom(handle));
        }

        Assert.False(deque.TryPushBottom(5));
        Assert.Equal(4, deque.ApproximateCount);

        Assert.True(deque.PopBottom(out var popped));
        Assert.Equal(4L, popped);
        Assert.True(deque.TryPushBottom(5));
        Assert.Equal(4, deque.ApproximateCount);
    }

    [Fact]
    public void FullBuffer_RefillsAfterAThiefDrainsPartOfIt()
    {
        var deque = new BoundedWorkStealingDeque(4);

        // The first four pushes run the cached-top fast path, the fifth refreshes and refuses:
        for (var handle = 1; handle <= 4; handle++)
        {
            Assert.True(deque.TryPushBottom(handle));
        }

        Assert.False(deque.TryPushBottom(5));

        Assert.True(deque.StealTop(out var drained));
        Assert.Equal(1L, drained);

        // The refresh now sees the advanced top, so there is room again:
        Assert.True(deque.TryPushBottom(5));
        Assert.Equal(4, deque.ApproximateCount);
        Assert.False(deque.TryPushBottom(6));

        Assert.True(deque.PopBottom(out var newest));
        Assert.Equal(5L, newest);
        Assert.True(deque.StealTop(out var oldest));
        Assert.Equal(2L, oldest);
    }

    [Fact]
    public void PushAndSteal_AcrossManyFullCycles_KeepsOrderAndBounds()
    {
        var deque = new BoundedWorkStealingDeque(8);
        var next = 0L;

        // Every cycle fills to the bound, drains from the top, and refreshes the cached top once:
        for (var cycle = 0; cycle < 1000; cycle++)
        {
            var start = next;

            while (deque.TryPushBottom(next))
            {
                next++;
            }

            Assert.Equal(8L, next - start);

            while (deque.StealTop(out var stolen))
            {
                Assert.Equal(start++, stolen);
            }

            Assert.Equal(0, deque.ApproximateCount);
        }
    }

    [Fact]
    public void ApproximateCount_TracksPushesAndTakes()
    {
        var deque = new BoundedWorkStealingDeque(8);

        Assert.Equal(0, deque.ApproximateCount);

        Assert.True(deque.TryPushBottom(1));
        Assert.True(deque.TryPushBottom(2));
        Assert.True(deque.TryPushBottom(3));
        Assert.Equal(3, deque.ApproximateCount);

        Assert.True(deque.PopBottom(out _));
        Assert.Equal(2, deque.ApproximateCount);

        Assert.True(deque.StealTop(out _));
        Assert.Equal(1, deque.ApproximateCount);

        Assert.True(deque.PopBottom(out _));
        Assert.Equal(0, deque.ApproximateCount);
    }

    [Fact]
    public void SingleHandle_IsTakenByTheOwnerWhenItWinsTheRace()
    {
        var deque = new BoundedWorkStealingDeque(4);
        Assert.True(deque.TryPushBottom(42));

        Assert.True(deque.PopBottom(out var popped));
        Assert.Equal(42L, popped);
        Assert.False(deque.StealTop(out _));
        Assert.Equal(0, deque.ApproximateCount);
    }

    [Fact]
    public void SingleHandle_IsTakenByAThiefWhenItStealsFirst()
    {
        var deque = new BoundedWorkStealingDeque(4);
        Assert.True(deque.TryPushBottom(42));

        Assert.True(deque.StealTop(out var stolen));
        Assert.Equal(42L, stolen);
        Assert.False(deque.PopBottom(out _));
        Assert.Equal(0, deque.ApproximateCount);
    }

    [Fact]
    public void WrapAround_KeepsPushAndPopOrderedAcrossManyCycles()
    {
        var deque = new BoundedWorkStealingDeque(4);
        var next = 0L;

        for (var cycle = 0; cycle < 100; cycle++)
        {
            for (var i = 0; i < 4; i++)
            {
                Assert.True(deque.TryPushBottom(next + i));
            }

            for (var i = 0; i < 4; i++)
            {
                Assert.True(deque.PopBottom(out var popped));
                Assert.Equal(next + 3 - i, popped);
            }

            next += 4;
            Assert.Equal(0, deque.ApproximateCount);
        }
    }

    [Fact]
    public void WrapAround_KeepsStealOrderedAcrossManyCycles()
    {
        var deque = new BoundedWorkStealingDeque(4);
        var next = 0L;

        for (var cycle = 0; cycle < 100; cycle++)
        {
            for (var i = 0; i < 4; i++)
            {
                Assert.True(deque.TryPushBottom(next + i));
            }

            for (var i = 0; i < 4; i++)
            {
                Assert.True(deque.StealTop(out var stolen));
                Assert.Equal(next + i, stolen);
            }

            next += 4;
            Assert.Equal(0, deque.ApproximateCount);
        }
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 3)]
    [InlineData(64, 7)]
    [InlineData(256, 3)]
    public void ConcurrentOwnerAndThieves_TakeEveryHandleExactlyOnce(int capacity, int thiefCount)
    {
        const int handleCount = 100_000;

        var deque = new BoundedWorkStealingDeque(capacity);
        var counts = new int[handleCount];
        long takeCount = 0;
        var stop = false;
        Exception? failure = null;

        void Record(long handle)
        {
            if (handle is < 0 or >= handleCount)
            {
                Interlocked.CompareExchange(ref failure, new InvalidOperationException($"Took the out-of-range handle {handle}."), null);
                // ReSharper disable once AccessToModifiedClosure
                Volatile.Write(ref stop, true);
                return;
            }

            Interlocked.Increment(ref counts[(int)handle]);
            // ReSharper disable once AccessToModifiedClosure
            Interlocked.Increment(ref takeCount);
        }

        var thieves = new Thread[thiefCount];
        for (var i = 0; i < thieves.Length; i++)
        {
            thieves[i] = new Thread(() =>
            {
                // ReSharper disable AccessToModifiedClosure
                while (!Volatile.Read(ref stop) && Volatile.Read(ref takeCount) < handleCount) 
                // ReSharper restore AccessToModifiedClosure
                {
                    if (deque.StealTop(out var stolen))
                    {
                        Record(stolen);
                    }
                    else
                    {
                        Thread.SpinWait(10);
                    }
                }
            })
            {
                IsBackground = true,
                Name = $"BoundedDequeThief{i}",
            };
        }

        var owner = new Thread(() =>
        {
            for (var handle = 0; handle < handleCount; handle++)
            {
                while (!deque.TryPushBottom(handle))
                {
                    // The buffer is full, so the owner drains its own end to make room:
                    if (deque.PopBottom(out var overflowed))
                    {
                        Record(overflowed);
                    }
                    else
                    {
                        Thread.SpinWait(10);
                    }
                }
            }

            var stallSpins = 0;
            while (!Volatile.Read(ref stop) && Volatile.Read(ref takeCount) < handleCount)
            {
                if (deque.PopBottom(out var taken))
                {
                    stallSpins = 0;
                    Record(taken);
                }
                else if (++stallSpins > 1_000_000)
                {
                    Interlocked.CompareExchange(ref failure, new TimeoutException("The drain stalled with handles left."), null);
                    Volatile.Write(ref stop, true);
                }
                else
                {
                    Thread.SpinWait(10);
                }
            }
        })
        {
            IsBackground = true,
            Name = "BoundedDequeOwner",
        };

        foreach (var thief in thieves)
        {
            thief.Start();
        }

        owner.Start();

        var ownerFinished = owner.Join(TimeSpan.FromSeconds(30));
        Volatile.Write(ref stop, true);

        foreach (var thief in thieves)
        {
            Assert.True(thief.Join(TimeSpan.FromSeconds(30)), "A thief did not finish within the timeout.");
        }

        Assert.True(ownerFinished, "The owner did not finish within the timeout.");
        Assert.Null(failure);
        Assert.Equal(capacity, deque.Capacity);
        Assert.Equal(handleCount, Volatile.Read(ref takeCount));

        for (var handle = 0; handle < handleCount; handle++)
        {
            Assert.Equal(1, Volatile.Read(ref counts[handle]));
        }
    }
}