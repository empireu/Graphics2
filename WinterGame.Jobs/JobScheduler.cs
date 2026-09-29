// ReSharper disable ForCanBeConvertedToForeach
// ReSharper disable LoopCanBeConvertedToQuery
namespace WinterGame.Jobs;

/// <summary>
///     Describes a <see cref="JobScheduler"/> before it starts.
/// </summary>
public sealed class JobSchedulerDescription
{
    internal const string DefaultName = "WinterGame.Scheduler";
    
    /// <summary>
    ///     Number of pool workers.
    /// </summary>
    public int ThreadCount { get; init; } = 4;

    /// <summary>
    ///     Work items the shared queue holds.
    ///     A push onto a full queue is refused.
    /// </summary>
    public int GlobalQueueCapacity { get; init; } = 4096;

    /// <summary>
    ///     Work items one worker holds locally before it spills to the shared queue.
    /// </summary>
    public int LocalQueueCapacity { get; init; } = 1024;

    /// <summary>
    ///     Name used to identify the worker threads.
    /// </summary>
    public string Name { get; init; } = DefaultName;
}

/// <summary>
///     Runs work items on a fixed set of threads.
///     Disposing stops the workers without draining the queues, so work still queued at that point never runs and its jobs never complete.
/// </summary>
public sealed class JobScheduler : IDisposable
{
    private readonly BoundedMpmcQueue _globalQueue;
    private readonly BoundedWorkStealingDeque[] _localQueues;
    private readonly IWorkExecutor?[] _executors = new IWorkExecutor?[WorkItem.MaxProviderId + 1];
    private readonly SemaphoreSlim _wake = new(0);

    private Thread[]? _threads;
    private int _registeredExecutors;
    private int _idleWorkers;
    private volatile bool _stopping;
    private bool _disposed;

#if DEBUG
    private long _inlineRuns;
#endif

    /// <summary>
    ///     The scheduler whose worker this thread is, so a worker of one scheduler that publishes into another is treated as an outside thread.
    /// </summary>
    [ThreadStatic]
    private static JobScheduler? _currentScheduler;

    [ThreadStatic]
    private static int _workerId;

    public JobScheduler(JobSchedulerDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentOutOfRangeException.ThrowIfLessThan(description.ThreadCount, 1);

        Pool = new JobPool();
        _globalQueue = new BoundedMpmcQueue(description.GlobalQueueCapacity);
        _localQueues = new BoundedWorkStealingDeque[description.ThreadCount];

        for (var index = 0; index < _localQueues.Length; index++)
        {
            _localQueues[index] = new BoundedWorkStealingDeque(description.LocalQueueCapacity);
        }

        Name = description.Name;
    }
    
    public JobPool Pool { get; }
    
    public string Name { get; }

    /// <summary>
    ///     Workers registered as parked. Equal to <see cref="ThreadCount"/> once the pool is asleep.
    /// </summary>
    internal int IdleWorkers => Volatile.Read(ref _idleWorkers);

    /// <summary>
    ///     Wake permits released and not yet consumed.
    /// </summary>
    internal int PendingWakes => _wake.CurrentCount;

    /// <summary>
    ///     Whether the calling thread is a worker of any scheduler.
    /// </summary>
    internal static bool IsWorkerThread => _currentScheduler != null;

    public int ThreadCount => _localQueues.Length;

    /// <summary>
    ///     Whether the workers are running.
    /// </summary>
    public bool IsStarted => _threads != null;

#if DEBUG
    /// <summary>
    ///     Items a worker ran inline because its queue and the shared queue were both full.
    ///     Nonzero means the queue capacities are undersized for the load.
    /// </summary>
    public long InlineRuns => Volatile.Read(ref _inlineRuns);
#endif

    /// <summary>
    ///     Registers an executor and returns the provider id that names it in a work item.
    ///     Executors must be registered before the workers start.
    /// </summary>
    public int RegisterExecutor(IWorkExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);

        if (_threads != null)
        {
            throw new InvalidOperationException("Executors must be registered before the scheduler starts");
        }

        if (_registeredExecutors > WorkItem.MaxProviderId)
        {
            throw new InvalidOperationException($"The scheduler is out of provider ids (max {WorkItem.MaxProviderId})");
        }

        var providerId = _registeredExecutors++;
        _executors[providerId] = executor;
        return providerId;
    }

    /// <summary>
    ///     Starts the workers. Idempotent.
    /// </summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_threads != null)
        {
            return;
        }

        var threads = new Thread[_localQueues.Length];

        for (var index = 0; index < threads.Length; index++)
        {
            var workerId = index;
            threads[index] = new Thread(() => RunWorker(workerId))
            {
                IsBackground = true,
                Name = $"{Name}{workerId}",
            };
        }

        _threads = threads;

        foreach (var thread in threads)
        {
            thread.Start();
        }
    }

    /// <summary>
    ///     Queues one unit of work against a job's counter.
    ///     False means the shared queue is full; only a thread outside the pool can get that. A worker whose queues are both full runs the item inline instead.
    /// </summary>
    public bool Publish(Job job, int providerId, int payloadSlot)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        
        Pool.Validate(job);

        ArgumentOutOfRangeException.ThrowIfNegative(providerId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(providerId, WorkItem.MaxProviderId);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadSlot);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payloadSlot, WorkItem.MaxPayloadSlot);

        return _executors[providerId] == null 
            ? throw new InvalidOperationException($"No executor is registered for the provider id {providerId}")
            : Publish(WorkItem.Pack(providerId, job.Slot, payloadSlot));
    }

    private bool Publish(long workItem)
    {
        var isWorker = ReferenceEquals(_currentScheduler, this);

        if (isWorker && _localQueues[_workerId].TryPushBottom(workItem))
        {
            WakeIfIdle();
            return true;
        }

        if (!_globalQueue.TryEnqueue(workItem))
        {
            if (!isWorker)
            {
                return false;
            }

#if DEBUG
            Interlocked.Increment(ref _inlineRuns);
#endif

            Execute(workItem, _workerId);
            return true;
        }

        WakeIfIdle();
        return true;
    }

    /// <summary>
    ///     Runs one queued work item on the calling thread.
    ///     Only legal before the workers start, because the context it passes carries a worker id that a running worker would also be using.
    /// </summary>
    public bool TryExecuteOne(int workerId = 0)
    {
        if (_threads != null)
        {
            throw new InvalidOperationException("Work items can only be executed directly before the scheduler starts");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(workerId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(workerId, _localQueues.Length);

        if (TryTakeWork(workerId, out var handle))
        {
            Execute(handle, workerId);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Runs queued work until every queue is empty and returns how many items ran.
    /// </summary>
    public int Drain(int workerId = 0)
    {
        var count = 0;

        while (TryExecuteOne(workerId))
        {
            count++;
        }

        return count;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping = true;

        var threads = _threads;

        if (threads != null)
        {
            for (var index = 0; index < threads.Length; index++)
            {
                _wake.Release();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }
        }

        Pool.Abandon();
        _wake.Dispose();
    }

    private void RunWorker(int workerId)
    {
        _currentScheduler = this;
        _workerId = workerId;

        while (!_stopping)
        {
            if (_localQueues[workerId].PopBottom(out var handle) || TryTakeWork(workerId, out handle))
            {
                Execute(handle, workerId);
                continue;
            }

            WaitForWork();
        }

        _currentScheduler = null;
    }

    /// <summary>
    ///     Takes work from the shared queue, then the other workers' deques.
    /// </summary>
    private bool TryTakeWork(int workerId, out long handle)
    {
        if (_globalQueue.TryDequeue(out handle))
        {
            return true;
        }

        for (var offset = 1; offset < _localQueues.Length; offset++)
        {
            var victim = workerId + offset;

            if (victim >= _localQueues.Length)
            {
                victim -= _localQueues.Length;
            }

            if (_localQueues[victim].StealTop(out handle))
            {
                return true;
            }
        }

        handle = 0;
        return false;
    }

    private bool HasWork()
    {
        if (_globalQueue.ApproximateCount > 0)
        {
            return true;
        }

        for (var index = 0; index < _localQueues.Length; index++)
        {
            var queue = _localQueues[index];
          
            if (queue.ApproximateCount > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Parks until work arrives.
    /// </summary>
    private void WaitForWork()
    {
        Interlocked.Increment(ref _idleWorkers);

        if (HasWork() || _stopping)
        {
            LeaveIdle();
            return;
        }

        _wake.Wait();
    }

    /// <summary>
    ///     Takes back an idle registration without parking.
    /// </summary>
    private void LeaveIdle()
    {
        var idle = Volatile.Read(ref _idleWorkers);

        while (idle > 0)
        {
            var observed = Interlocked.CompareExchange(ref _idleWorkers, idle - 1, idle);

            if (observed == idle)
            {
                return;
            }

            idle = observed;
        }

        _wake.Wait();
    }

    /// <summary>
    ///     Wakes at most one idle worker per published item.
    /// </summary>
    private void WakeIfIdle()
    {
        Thread.MemoryBarrier();

        var idle = Volatile.Read(ref _idleWorkers);

        while (idle > 0)
        {
            var observed = Interlocked.CompareExchange(ref _idleWorkers, idle - 1, idle);

            if (observed == idle)
            {
                _wake.Release();
                return;
            }

            idle = observed;
        }
    }

    /// <summary>
    ///     Runs a work item and retires one unit of its job's counter, whatever the executor does.
    /// </summary>
    private void Execute(long workItem, int workerId)
    {
        var jobSlot = WorkItem.JobSlot(workItem);

        try
        {
            var providerId = WorkItem.ProviderId(workItem);
            var executor = _executors[providerId] ?? throw new InvalidOperationException($"No executor is registered for the provider id {providerId}");

            executor.Execute(new WorkExecutionContext(Pool.Pack(jobSlot), workerId), WorkItem.PayloadSlot(workItem));
        }
        catch (Exception exception)
        {
            Pool.Fault(exception);
        }
        finally
        {
            Pool.Decrement(jobSlot);
        }
    }
}
