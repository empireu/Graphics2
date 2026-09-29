using WinterGame.Jobs;

namespace WinterGame.Tests;

public class BoundedMpmcQueueTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(64)]
    [InlineData(1024)]
    public void Constructor_AcceptsPowerOfTwoCapacities(int capacity)
    {
        var queue = new BoundedMpmcQueue(capacity);

        Assert.Equal(capacity, queue.Capacity);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(100)]
    public void Constructor_RefusesANonPowerOfTwo(int capacity)
    {
        Assert.Throws<ArgumentException>(() => new BoundedMpmcQueue(capacity));
    }

    [Fact]
    public void Constructor_RefusesACapacityBelowTwo()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedMpmcQueue(1));
    }

    [Fact]
    public void TryDequeue_OnAFreshQueue_IsEmpty()
    {
        var queue = new BoundedMpmcQueue(4);

        Assert.False(queue.TryDequeue(out _));
        Assert.Equal(0, queue.ApproximateCount);
    }

    [Fact]
    public void TryEnqueue_ThenTryDequeue_IsFirstInFirstOut()
    {
        var queue = new BoundedMpmcQueue(4);
        Assert.True(queue.TryEnqueue(11));
        Assert.True(queue.TryEnqueue(22));

        Assert.True(queue.TryDequeue(out var first));
        Assert.True(queue.TryDequeue(out var second));

        Assert.Equal(11, first);
        Assert.Equal(22, second);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void TryEnqueue_FillsTheQueueThenReportsFull()
    {
        var queue = new BoundedMpmcQueue(4);

        for (var index = 0; index < 4; index++)
        {
            Assert.True(queue.TryEnqueue(index));
        }

        Assert.False(queue.TryEnqueue(4));
        Assert.Equal(4, queue.ApproximateCount);

        Assert.True(queue.TryDequeue(out var value));
        Assert.Equal(0, value);
        Assert.True(queue.TryEnqueue(4));
    }

    [Fact]
    public void Queue_WrapsAroundItsCells()
    {
        var queue = new BoundedMpmcQueue(4);

        for (var round = 0; round < 100; round++)
        {
            Assert.True(queue.TryEnqueue(round));
            Assert.True(queue.TryEnqueue(round * 7));
            Assert.True(queue.TryDequeue(out var first));
            Assert.True(queue.TryDequeue(out var second));

            Assert.Equal(round, first);
            Assert.Equal(round * 7, second);
        }

        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void ConcurrentProducersAndConsumers_LoseAndDuplicateNothing()
    {
        const int producerCount = 3;
        const int consumerCount = 3;
        const int itemsPerProducer = 20_000;
        const int total = producerCount * itemsPerProducer;

        var queue = new BoundedMpmcQueue(64);
        var seen = new int[total];
        var dequeued = 0;

        var producers = new Thread[producerCount];

        for (var producer = 0; producer < producerCount; producer++)
        {
            var baseValue = producer * itemsPerProducer;
            producers[producer] = new Thread(() =>
            {
                for (var index = 0; index < itemsPerProducer; index++)
                {
                    var item = baseValue + index;

                    while (!queue.TryEnqueue(item))
                    {
                        Thread.Yield();
                    }
                }
            })
            {
                IsBackground = true,
            };
        }

        var consumers = new Thread[consumerCount];

        for (var consumer = 0; consumer < consumerCount; consumer++)
        {
            consumers[consumer] = new Thread(() =>
            {
                // ReSharper disable once AccessToModifiedClosure
                while (Volatile.Read(ref dequeued) < total)
                {
                    if (queue.TryDequeue(out var item))
                    {
                        Interlocked.Increment(ref seen[item]);
                        Interlocked.Increment(ref dequeued);
                    }
                }
            })
            {
                IsBackground = true,
            };
        }

        foreach (var thread in producers)
        {
            thread.Start();
        }

        foreach (var thread in consumers)
        {
            thread.Start();
        }

        foreach (var thread in producers)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "A producer did not finish");
        }

        foreach (var thread in consumers)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "A consumer did not finish");
        }

        Assert.Equal(total, Volatile.Read(ref dequeued));

        var mismatches = 0;

        for (var index = 0; index < total; index++)
        {
            if (seen[index] != 1)
            {
                mismatches++;
            }
        }

        Assert.Equal(0, mismatches);
    }
}
