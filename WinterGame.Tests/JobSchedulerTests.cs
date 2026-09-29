using System.Collections.Concurrent;
using System.Diagnostics;
using WinterGame.Jobs;
using Xunit.Abstractions;
// ReSharper disable RedundantArgumentDefaultValue

namespace WinterGame.Tests;

public class JobSchedulerTests(ITestOutputHelper output)
{
    private sealed class CountingExecutor : IWorkExecutor
    {
        public int Executed;

        public Job LastJob;

        public void Execute(in WorkExecutionContext context, int item)
        {
            LastJob = context.Handle;
            Interlocked.Increment(ref Executed);
        }
    }

    private sealed class ThrowingExecutor : IWorkExecutor
    {
        public void Execute(in WorkExecutionContext context, int item)
        {
            throw new InvalidOperationException("The executor failed on purpose");
        }
    }

    private sealed class RecordingExecutor : IWorkExecutor
    {
        public int Executed;
        public int OffPoolExecutions;

        public readonly ConcurrentDictionary<int, int> Payloads = new();
        public readonly ConcurrentDictionary<Thread, byte> Threads = new();

        public void Execute(in WorkExecutionContext context, int item)
        {
            Interlocked.Increment(ref Executed);

            if (Thread.CurrentThread.Name?.StartsWith(JobSchedulerDescription.DefaultName) != true)
            {
                Interlocked.Increment(ref OffPoolExecutions);
            }

            Threads.TryAdd(Thread.CurrentThread, 0);
            Payloads.AddOrUpdate(item, 1, (_, count) => count + 1);
        }
    }

    private sealed class FanOutExecutor(JobScheduler scheduler, int childrenPerTask) : IWorkExecutor
    {
        public int ProviderId;

        private int _spawned;

        public void Execute(in WorkExecutionContext context, int item)
        {
            if (Interlocked.Exchange(ref _spawned, 1) != 0)
            {
                return;
            }

            context.Handle.Pool.AddWork(context.Handle, childrenPerTask);

            for (var index = 0; index < childrenPerTask; index++)
            {
                if (!scheduler.Publish(context.Handle, ProviderId, index))
                {
                    throw new InvalidOperationException("The scheduler refused a child");
                }
            }
        }
    }

    private sealed class SpawnExecutor(JobScheduler scheduler, int childProviderId, int childrenPerTask) : IWorkExecutor
    {
        public int Executed;

        public void Execute(in WorkExecutionContext context, int item)
        {
            Interlocked.Increment(ref Executed);
            context.Handle.Pool.AddWork(context.Handle, childrenPerTask);

            for (var index = 0; index < childrenPerTask; index++)
            {
                if (!scheduler.Publish(context.Handle, childProviderId, index))
                {
                    throw new InvalidOperationException("The scheduler refused a child");
                }
            }
        }
    }

    private sealed class CrossPublishExecutor(CountdownEvent rendezvous, JobScheduler target, Job targetJob, int targetProviderId) : IWorkExecutor
    {
        public void Execute(in WorkExecutionContext context, int item)
        {
            rendezvous.Signal();
            rendezvous.Wait(TimeSpan.FromSeconds(10));

            if (!target.Publish(targetJob, targetProviderId, item))
            {
                throw new InvalidOperationException("The target scheduler refused the item");
            }
        }
    }

    private sealed class TimestampExecutor(long[] started) : IWorkExecutor
    {
        public void Execute(in WorkExecutionContext context, int item)
        {
            started[item] = Stopwatch.GetTimestamp();
        }
    }

    private sealed class RendezvousExecutor(CountdownEvent rendezvous) : IWorkExecutor
    {
        public int AllRendezvoused;

        public void Execute(in WorkExecutionContext context, int item)
        {
            rendezvous.Signal();

            if (rendezvous.Wait(TimeSpan.FromSeconds(10)))
            {
                Volatile.Write(ref AllRendezvoused, 1);
            }
        }
    }

    [Fact]
    public void Publish_ThenDrain_RunsEveryItemAndCompletesTheJob()
    {
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 2 });
        var executor = new CountingExecutor();
        var providerId = scheduler.RegisterExecutor(executor);

        var job = scheduler.Pool.CreateJob(3);
        Assert.True(scheduler.Publish(job, providerId, 0));
        Assert.True(scheduler.Publish(job, providerId, 1));
        Assert.True(scheduler.Publish(job, providerId, 2));

        Assert.False(scheduler.Pool.TryComplete(job));

        Assert.Equal(3, scheduler.Drain());

        Assert.Equal(3, executor.Executed);
        Assert.True(scheduler.Pool.TryComplete(job));
        Assert.Equal(job, executor.LastJob);

        scheduler.Pool.Release(job);
    }

    [Fact]
    public void Drain_OnAnEmptyScheduler_RunsNothing()
    {
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });

        Assert.Equal(0, scheduler.Drain());
    }

    [Fact]
    public void Publish_WithUnregisteredSlots_PassesThemThroughUnchanged()
    {
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        var executor = new RecordingExecutor();
        var providerId = scheduler.RegisterExecutor(executor);

        var job = scheduler.Pool.CreateJob(2);
        Assert.True(scheduler.Publish(job, providerId, 900_001));
        Assert.True(scheduler.Publish(job, providerId, 12));

        scheduler.Drain();

        Assert.Equal(1, executor.Payloads[900_001]);
        Assert.Equal(1, executor.Payloads[12]);

        scheduler.Pool.Release(job);
    }

    [Fact]
    public void Publish_WithAForeignJob_Throws()
    {
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        var providerId = scheduler.RegisterExecutor(new CountingExecutor());
        using var other = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        var foreign = other.Pool.CreateJob(1);

        Assert.Throws<ArgumentException>(() => scheduler.Publish(foreign, providerId, 0));
    }

    [Fact]
    public void Publish_WithAReleasedJob_Throws()
    {
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        var providerId = scheduler.RegisterExecutor(new CountingExecutor());
        var job = scheduler.Pool.CreateJob(1);

        Assert.True(scheduler.Publish(job, providerId, 0));
        scheduler.Drain();
        scheduler.Pool.Release(job);

        Assert.Throws<InvalidOperationException>(() => scheduler.Publish(job, providerId, 0));
    }

    [Fact]
    public void Publish_WithAnUnregisteredProvider_Throws()
    {
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        var providerId = scheduler.RegisterExecutor(new CountingExecutor());
        var job = scheduler.Pool.CreateJob(1);

        Assert.Throws<InvalidOperationException>(() => scheduler.Publish(job, 5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Publish(job, WorkItem.MaxProviderId + 1, 0));

        Assert.True(scheduler.Publish(job, providerId, 0));
        scheduler.Drain();
        scheduler.Pool.Release(job);
    }

    [Fact]
    public void Publish_ToACompletedJob_Throws()
    {
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        var providerId = scheduler.RegisterExecutor(new CountingExecutor());
        var job = scheduler.Pool.CreateJob(1);

        Assert.True(scheduler.Publish(job, providerId, 0));
        scheduler.Drain();

        Assert.Throws<InvalidOperationException>(() => scheduler.Publish(job, providerId, 1));

        scheduler.Pool.Release(job);
    }

    [Fact]
    public void Publish_AfterDispose_Throws()
    {
        var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        var providerId = scheduler.RegisterExecutor(new CountingExecutor());
        var job = scheduler.Pool.CreateJob(1);
        scheduler.Dispose();

        Assert.Throws<ObjectDisposedException>(() => scheduler.Publish(job, providerId, 0));
    }

    [Fact]
    public void Complete_AfterDispose_OnAJobThatNeverRan_Throws()
    {
        var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        var providerId = scheduler.RegisterExecutor(new CountingExecutor());
        var job = scheduler.Pool.CreateJob(1);
        Assert.True(scheduler.Publish(job, providerId, 0));

        scheduler.Dispose();

        Assert.False(scheduler.Pool.TryComplete(job));
        Assert.Throws<ObjectDisposedException>(() => scheduler.Pool.Complete(job));
    }

    [Fact]
    public void Dispose_ReleasesAThreadBlockedInComplete()
    {
        var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        var providerId = scheduler.RegisterExecutor(new CountingExecutor());

        Exception? observed = null;
        var waiter = new Thread(() =>
        {
            try
            {
                // Created on the waiter, because only the creating thread may wait on a job.
                // ReSharper disable AccessToDisposedClosure
                var job = scheduler.Pool.CreateJob(1);
                scheduler.Publish(job, providerId, 0);
                scheduler.Pool.Complete(job);
                // ReSharper restore AccessToDisposedClosure
            }
            catch (Exception exception)
            {
                observed = exception;
            }
        })
        {
            IsBackground = true,
        };

        waiter.Start();

        var deadline = Environment.TickCount64 + 10_000;

        while ((waiter.ThreadState & System.Threading.ThreadState.WaitSleepJoin) == 0 && Environment.TickCount64 < deadline)
        {
            Thread.Yield();
        }

        scheduler.Dispose();

        Assert.True(waiter.Join(TimeSpan.FromSeconds(10)), "The waiter was never released");
        Assert.IsType<ObjectDisposedException>(observed);
    }

    [Fact]
    public void RegisterExecutor_AfterStart_Throws()
    {
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        scheduler.Start();

        Assert.Throws<InvalidOperationException>(() => scheduler.RegisterExecutor(new CountingExecutor()));
    }

    [Fact]
    public void TryExecuteOne_AfterStart_Throws()
    {
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        scheduler.Start();

        Assert.Throws<InvalidOperationException>(() => scheduler.TryExecuteOne());
    }

    [Fact]
    public void Start_IsIdempotent()
    {
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });

        scheduler.Start();
        scheduler.Start();

        Assert.True(scheduler.IsStarted);
    }

    [Fact]
    public void Workers_RunEveryPublishedItemOnAPoolThread()
    {
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 4 });
        var executor = new RecordingExecutor();
        var providerId = scheduler.RegisterExecutor(executor);
        scheduler.Start();

        const int itemCount = 2000;
        var job = scheduler.Pool.CreateJob(itemCount);

        for (var index = 0; index < itemCount; index++)
        {
            Assert.True(scheduler.Publish(job, providerId, index));
        }

        Assert.True(WaitForCompletion(scheduler.Pool, job, 30_000), "The job did not complete in time");

        Assert.Equal(itemCount, Volatile.Read(ref executor.Executed));
        Assert.Equal(0, Volatile.Read(ref executor.OffPoolExecutions));
        Assert.Equal(itemCount, executor.Payloads.Count);

        foreach (var payload in executor.Payloads)
        {
            Assert.Equal(1, payload.Value);
        }

        scheduler.Pool.Release(job);
    }

    [Fact]
    public void Workers_RunItemsOnSeveralThreadsAtOnce()
    {
        const int threadCount = 4;
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = threadCount });
        using var rendezvous = new CountdownEvent(threadCount);
        var executor = new RendezvousExecutor(rendezvous);
        var providerId = scheduler.RegisterExecutor(executor);
        scheduler.Start();

        var job = scheduler.Pool.CreateJob(threadCount);

        for (var index = 0; index < threadCount; index++)
        {
            Assert.True(scheduler.Publish(job, providerId, index));
        }

        Assert.True(WaitForCompletion(scheduler.Pool, job, 30_000), "The job did not complete in time");
        Assert.Equal(1, Volatile.Read(ref executor.AllRendezvoused));

        scheduler.Pool.Release(job);
    }

    [Fact]
    public void FanOut_FromAWorker_CompletesOnceTheChildrenHaveRun()
    {
        const int children = 8;
        using var scheduler = new JobScheduler(new JobSchedulerDescription
        {
            ThreadCount = 2,
            LocalQueueCapacity = 2,
            GlobalQueueCapacity = 64,
        });

        var executor = new FanOutExecutor(scheduler, children);
        executor.ProviderId = scheduler.RegisterExecutor(executor);
        scheduler.Start();

        var job = scheduler.Pool.CreateJob(1);
        Assert.True(scheduler.Publish(job, executor.ProviderId, 0));

        Assert.True(WaitForCompletion(scheduler.Pool, job, 30_000), "The children never completed the job");

#if DEBUG
        Assert.Equal(0, scheduler.InlineRuns);
#endif

        scheduler.Pool.Release(job);
    }

    [Fact]
    public void FanOut_FromAWorkerWithBothQueuesFull_RunsTheOverflowInline()
    {
        const int children = 64;
        using var scheduler = new JobScheduler(new JobSchedulerDescription
        {
            ThreadCount = 1,
            LocalQueueCapacity = 2,
            GlobalQueueCapacity = 2,
        });

        var executor = new FanOutExecutor(scheduler, children);
        executor.ProviderId = scheduler.RegisterExecutor(executor);
        scheduler.Start();

        var job = scheduler.Pool.CreateJob(1);
        Assert.True(scheduler.Publish(job, executor.ProviderId, 0));

        Assert.True(WaitForCompletion(scheduler.Pool, job, 30_000), "The children never completed the job");
        scheduler.Pool.ThrowIfFaulted();

#if DEBUG
        Assert.True(scheduler.InlineRuns > 0, "The overflow never ran inline");
#endif

        scheduler.Pool.Release(job);
    }

    [Fact]
    public void AWorkerOfOneScheduler_PublishesIntoAnotherThroughItsSharedQueue()
    {
        const int sourceThreads = 4;
        using var target = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        var targetExecutor = new CountingExecutor();
        var targetProviderId = target.RegisterExecutor(targetExecutor);
        target.Start();
        var targetJob = target.Pool.CreateJob(sourceThreads);

        using var source = new JobScheduler(new JobSchedulerDescription { ThreadCount = sourceThreads });
        using var rendezvous = new CountdownEvent(sourceThreads);
        var sourceProviderId = source.RegisterExecutor(new CrossPublishExecutor(rendezvous, target, targetJob, targetProviderId));
        source.Start();
        var sourceJob = source.Pool.CreateJob(sourceThreads);

        for (var index = 0; index < sourceThreads; index++)
        {
            Assert.True(source.Publish(sourceJob, sourceProviderId, index));
        }

        Assert.True(WaitForCompletion(source.Pool, sourceJob, 30_000), "The source job did not complete in time");
        Assert.True(WaitForCompletion(target.Pool, targetJob, 30_000), "The target job did not complete in time");
        source.Pool.ThrowIfFaulted();
        target.Pool.ThrowIfFaulted();
        Assert.Equal(sourceThreads, Volatile.Read(ref targetExecutor.Executed));

        source.Pool.Release(sourceJob);
        target.Pool.Release(targetJob);
    }

    [Fact]
    public void ChildrenQueuedOnWorkerDeques_AreStolenByTheOtherWorkers()
    {
        const int parentCount = 32;
        const int childrenPerParent = 64;
        using var scheduler = new JobScheduler(new JobSchedulerDescription
        {
            ThreadCount = 4,
            LocalQueueCapacity = 1024,
            GlobalQueueCapacity = 256,
        });

        var children = new CountingExecutor();
        var childProviderId = scheduler.RegisterExecutor(children);
        var parents = new SpawnExecutor(scheduler, childProviderId, childrenPerParent);
        var providerId = scheduler.RegisterExecutor(parents);
        scheduler.Start();

        var job = scheduler.Pool.CreateJob(parentCount);

        for (var index = 0; index < parentCount; index++)
        {
            Assert.True(scheduler.Publish(job, providerId, index));
        }

        Assert.True(WaitForCompletion(scheduler.Pool, job, 60_000), "The fan-out never completed");
        Assert.Equal(parentCount, parents.Executed);
        Assert.Equal(parentCount * childrenPerParent, children.Executed);

        scheduler.Pool.Release(job);
    }

    [Fact]
    public void AnExecutorThatThrows_FaultsThePoolWithItsOriginalStackTrace()
    {
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        var providerId = scheduler.RegisterExecutor(new ThrowingExecutor());
        scheduler.Start();

        var job = scheduler.Pool.CreateJob(1);
        Assert.True(scheduler.Publish(job, providerId, 0));

        var fault = WaitForFault(scheduler.Pool, 30_000);

        Assert.IsType<InvalidOperationException>(fault);
        Assert.Contains(nameof(ThrowingExecutor), fault.StackTrace);
        Assert.Throws<InvalidOperationException>(() => scheduler.Pool.TryComplete(job));
        Assert.Throws<InvalidOperationException>(() => scheduler.Pool.Complete(job));
    }

    [Fact]
    public void ParkAndWake_OverManyRounds_NeverLosesAWakeOrLeavesAPermit()
    {
        const int threadCount = 4;
        const int rounds = 2000;
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = threadCount });
        var leaves = new CountingExecutor();
        var leafProviderId = scheduler.RegisterExecutor(leaves);
        var parentProviderId = scheduler.RegisterExecutor(new SpawnExecutor(scheduler, leafProviderId, 4));
        scheduler.Start();

        var random = new Random(1234);

        for (var round = 0; round < rounds; round++)
        {
            // Every round starts from a fully parked pool, so every round crosses the park and wake handshake.
            Assert.True(WaitForParked(scheduler, 10_000), $"The workers never parked before round {round}");
            Assert.Equal(0, scheduler.PendingWakes);

            var count = random.Next(1, threadCount * 2 + 1);
            var providerId = random.Next(2) == 0 ? leafProviderId : parentProviderId;
            var job = scheduler.Pool.CreateJob(count);

            for (var index = 0; index < count; index++)
            {
                Assert.True(scheduler.Publish(job, providerId, index));
            }

            Assert.True(WaitForCompletion(scheduler.Pool, job, 10_000), $"Round {round} never completed, so a wake was lost");
            scheduler.Pool.Release(job);
        }

        Assert.True(WaitForParked(scheduler, 10_000), "The workers never parked after the last round");
        Assert.Equal(0, scheduler.PendingWakes);
    }

    [Fact]
    public void EndToEndLatency_IsReported()
    {
        const int samples = 1000;
        const int warmup = 50;
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 4 });
        var started = new long[samples + 1];
        var providerId = scheduler.RegisterExecutor(new TimestampExecutor(started));
        scheduler.Start();

        var latencies = new double[samples];

        for (var index = 0; index < samples; index++)
        {
            latencies[index] = MeasureDispatchToStart(scheduler, providerId, started, index);
        }

        Thread.Sleep(100);

        var parked = MeasureDispatchToStart(scheduler, providerId, started, samples);

        var steady = latencies[warmup..];
        Array.Sort(steady);

        output.WriteLine(
            $"Back-to-back dispatch to start over {steady.Length} tasks: mean {steady.Average():F1} us, " +
            $"median {steady[steady.Length / 2]:F1} us, p99 {steady[(int)(steady.Length * 0.99)]:F1} us, " +
            $"max {steady[^1]:F1} us"
        );
        
        output.WriteLine($"Dispatch to start after 100 ms idle: {parked:F1} us");
    }

    [Fact]
    public void Dispose_StopsTheWorkers()
    {
        const int threadCount = 3;
        var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = threadCount });
        using var rendezvous = new CountdownEvent(threadCount);
        var rendezvousProviderId = scheduler.RegisterExecutor(new RendezvousExecutor(rendezvous));
        var recorder = new RecordingExecutor();
        var recorderProviderId = scheduler.RegisterExecutor(recorder);
        scheduler.Start();

        var rendezvousJob = scheduler.Pool.CreateJob(threadCount);

        for (var index = 0; index < threadCount; index++)
        {
            Assert.True(scheduler.Publish(rendezvousJob, rendezvousProviderId, index));
        }

        Assert.True(WaitForCompletion(scheduler.Pool, rendezvousJob, 30_000), "The rendezvous did not complete in time");

        var recordJob = scheduler.Pool.CreateJob(threadCount);

        for (var index = 0; index < threadCount; index++)
        {
            Assert.True(scheduler.Publish(recordJob, recorderProviderId, index));
        }

        Assert.True(WaitForCompletion(scheduler.Pool, recordJob, 30_000), "The recording job did not complete in time");

        scheduler.Dispose();
        scheduler.Dispose();

        Assert.NotEmpty(recorder.Threads);

        foreach (var thread in recorder.Threads.Keys)
        {
            Assert.False(thread.IsAlive);
        }
    }

    /// <summary>
    ///     Measures microseconds from publish to the moment the executor starts.
    /// </summary>
    private static double MeasureDispatchToStart(JobScheduler scheduler, int providerId, long[] started, int item)
    {
        var job = scheduler.Pool.CreateJob(1);
        var start = Stopwatch.GetTimestamp();

        Assert.True(scheduler.Publish(job, providerId, item));
        Assert.True(WaitForCompletion(scheduler.Pool, job, 10_000), "A latency sample never completed");
        scheduler.Pool.Release(job);

        return Stopwatch.GetElapsedTime(start, started[item]).TotalMicroseconds;
    }

    private static bool WaitForParked(JobScheduler scheduler, int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;

        while (Environment.TickCount64 < deadline)
        {
            if (scheduler.IdleWorkers == scheduler.ThreadCount)
            {
                return true;
            }

            Thread.Yield();
        }

        return scheduler.IdleWorkers == scheduler.ThreadCount;
    }

    private static Exception WaitForFault(JobPool pool, int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;

        while (Environment.TickCount64 < deadline)
        {
            try
            {
                pool.ThrowIfFaulted();
            }
            catch (Exception exception)
            {
                return exception;
            }

            Thread.Yield();
        }

        throw new TimeoutException("The pool never faulted");
    }

    private static bool WaitForCompletion(JobPool pool, Job job, int milliseconds)
    {
        var spinWait = new SpinWait();
        var deadline = Environment.TickCount64 + milliseconds;

        while (Environment.TickCount64 < deadline)
        {
            if (pool.TryComplete(job))
            {
                return true;
            }

            spinWait.SpinOnce(sleep1Threshold: -1);
        }

        return pool.TryComplete(job);
    }
}
