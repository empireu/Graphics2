using WinterGame.Jobs;

namespace WinterGame.Tests;

public class WorkExecutorScratchTests
{
    private readonly struct Output(int token)
    {
        public readonly int Token = token;
    }

    private static WorkExecutorTransformScratch<int, Output> Create(int capacity = 4, bool clearInput = false)
    {
        return new WorkExecutorTransformScratch<int, Output>(new int[capacity], clearInput);
    }

    [Fact]
    public void Constructor_RejectsAMissingOrEmptyArena()
    {
        Assert.Throws<ArgumentException>(() => new WorkExecutorTransformScratch<int, Output>(null!, false));
        Assert.Throws<ArgumentException>(() => new WorkExecutorTransformScratch<int, Output>([], false));
    }

    [Fact]
    public void Constructor_ExposesTheCapacityAndTheClearFlag()
    {
        var scratch = Create(8, true);

        Assert.Equal(8, scratch.Capacity);
        Assert.True(scratch.ClearInputToDefault);
    }

    [Fact]
    public void TryAllocateInputSlot_TakesEverySlotThenReportsFull()
    {
        var scratch = Create(4);
        var slots = new HashSet<int>();

        for (var index = 0; index < 4; index++)
        {
            Assert.True(scratch.TryAllocateInputSlot(out var slot));
            Assert.True(slots.Add(slot), $"The slot {slot} was handed out twice");
        }

        Assert.False(scratch.TryAllocateInputSlot(out _));
    }

    [Fact]
    public void AllocateInputSlot_ThrowsWhenOutOfSpace()
    {
        var scratch = Create(2);
        scratch.AllocateInputSlot(out _);
        scratch.AllocateInputSlot(out _);

        Assert.Throws<InvalidOperationException>(() => scratch.AllocateInputSlot(out _));
    }

    [Fact]
    public void AllocateInputSlot_ReturnsTheInputForWriting()
    {
        var scratch = Create();
        ref var input = ref scratch.AllocateInputSlot(out var slot);
        input = 42;

        Assert.Equal(42, scratch.GetInputSlot(slot));
    }

    [Fact]
    public void WriteOutput_ThenGetOutput_RoundTripsBeforeTheSlotIsFreed()
    {
        var scratch = Create();
        scratch.AllocateInputSlot(out var slot);

        scratch.WriteOutput(slot, new Output(7));

        Assert.Equal(7, scratch.GetOutput(slot).Token);
    }

    [Fact]
    public void ReadOutputAndFree_ReturnsTheValueAndReleasesTheSlot()
    {
        var scratch = Create(1);
        scratch.AllocateInputSlot(out var slot);
        scratch.WriteOutput(slot, new Output(9));

        Assert.Equal(9, scratch.ReadOutputAndFree(slot).Token);

        Assert.True(scratch.TryAllocateInputSlot(out var reused));
        Assert.Equal(slot, reused);
    }

    [Fact]
    public void Free_ReleasesASlotWhoseTaskNeverRan()
    {
        var scratch = Create(1);
        scratch.AllocateInputSlot(out var slot);

        scratch.Free(slot);

        Assert.True(scratch.TryAllocateInputSlot(out var reused));
        Assert.Equal(slot, reused);
    }

    [Fact]
    public void GetOutput_OfASlotWhoseTaskNeverRan_IsDefault()
    {
        var scratch = Create(1);
        scratch.AllocateInputSlot(out var slot);

        Assert.Equal(0, scratch.GetOutput(slot).Token);
    }

    [Fact]
    public void Free_ResetsTheOutputSoAReusedSlotStartsWithoutAResult()
    {
        var scratch = Create(1);
        scratch.AllocateInputSlot(out var slot);
        scratch.WriteOutput(slot, new Output(5));

        scratch.Free(slot);
        scratch.AllocateInputSlot(out var reused);

        Assert.Equal(slot, reused);
        Assert.Equal(0, scratch.GetOutput(reused).Token);
    }

    [Fact]
    public void Free_ClearsTheInputWhenRequested()
    {
        var scratch = new WorkExecutorTransformScratch<object, Output>(new object[1], true);
        ref var input = ref scratch.AllocateInputSlot(out var slot);
        input = new object();

        scratch.Free(slot);
        scratch.AllocateInputSlot(out var reused);

        Assert.Equal(slot, reused);
        Assert.Null(scratch.GetInputSlot(reused));
    }

    [Fact]
    public void Free_KeepsTheInputWhenClearingIsDisabled()
    {
        var scratch = new WorkExecutorTransformScratch<object, Output>(new object[1], false);
        var retained = new object();
        ref var input = ref scratch.AllocateInputSlot(out var slot);
        input = retained;

        scratch.Free(slot);
        scratch.AllocateInputSlot(out var reused);

        Assert.Same(retained, scratch.GetInputSlot(reused));
    }

    [Fact]
    public void ConcurrentSlots_AreNeverHandedOutTwice()
    {
        const int capacity = 8;
        const int threadCount = 4;
        const int iterations = 20_000;

        var scratch = Create(capacity);
        var inUse = new int[capacity];
        var failures = 0;

        var threads = new Thread[threadCount];
        for (var worker = 0; worker < threadCount; worker++)
        {
            var seed = worker;
            threads[worker] = new Thread(() =>
            {
                var token = seed;

                for (var index = 0; index < iterations; index++)
                {
                    token += threadCount;

                    int slot;
                    while (!scratch.TryAllocateInputSlot(out slot))
                    {
                        Thread.Yield();
                    }

                    if (Interlocked.Exchange(ref inUse[slot], 1) != 0)                    {
                        Interlocked.Increment(ref failures);
                    }

                    ref var input = ref scratch.GetInputSlot(slot);
                    input = token;
                    scratch.WriteOutput(slot, new Output(token));

                    var output = scratch.GetOutput(slot);
                    Volatile.Write(ref inUse[slot], 0);
                    scratch.Free(slot);

                    if (output.Token != token)
                    {
                        Interlocked.Increment(ref failures);
                    }
                }
            })
            {
                IsBackground = true,
            };
        }

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "A worker did not finish");
        }

        Assert.Equal(0, failures);
    }
}
