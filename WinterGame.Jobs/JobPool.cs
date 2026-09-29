using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace WinterGame.Jobs;

public readonly struct Job(JobPool pool, ulong data) : IEquatable<Job>
{
    public readonly JobPool Pool = pool;

    /// <summary>
    ///     The low 32 bits are the counter slot, the high 32 bits are that slot's generation.
    /// </summary>
    private readonly ulong _data = data;

    internal int Slot => (int)(uint)_data;

    internal uint Generation => (uint)(_data >> 32);

    internal static ulong Pack(int slot, uint generation)
    {
        return ((ulong)generation << 32) | (uint)slot;
    }

    public bool Equals(Job other)
    {
        return ReferenceEquals(Pool, other.Pool) && _data == other._data;
    }

    public override bool Equals(object? obj)
    {
        return obj is Job other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Pool, _data);
    }

    public static bool operator ==(Job left, Job right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Job left, Job right)
    {
        return !left.Equals(right);
    }
}

public sealed class JobPool
{
    /// <summary>
    ///     The slot half of a handle is 16 bits, which fixes the slab size.
    /// </summary>
    public const int MaxCounters = 1 << 16;

    private struct CounterEntry
    {
        public uint Generation;
        public int Remaining;

        /// <summary>
        ///     Signaled by the last unit, so <see cref="Complete"/> can block without polling.
        /// </summary>
        public ManualResetEventSlim Fence;

        /// <summary>
        ///     Set to 1 by the last unit, after <see cref="Fence"/> has been signaled.
        ///     It proves the final writer is done touching the slot, which <see cref="Remaining"/> alone does not.
        /// </summary>
        public int Retired;

#if DEBUG
        /// <summary>
        ///     The managed thread that created the job, the only one allowed to wait on it or release it.
        /// </summary>
        public int CreatorThreadId;
#endif
    }

    private readonly CounterEntry[] _entries = new CounterEntry[MaxCounters];
    private readonly Stack<int> _freeSlots = new(MaxCounters);
    private readonly Lock _counterLock = new();
    private volatile bool _abandoned;
    private ExceptionDispatchInfo? _fault;
    
    public JobPool()
    {
        for (var i = MaxCounters - 1; i >= 0; i--)
        {
            _entries[i].Fence = new ManualResetEventSlim();
            _freeSlots.Push(i);
        }
    }

    public int AvailableCounters
    {
        get
        {
            lock (_counterLock)
            {
                return _freeSlots.Count;
            }
        }
    }

    /// <summary>
    ///     Creates a counter for <see cref="count"/> units of work.
    ///     Every published task decrements it once upon completion, and <see cref="count"/> must cover every task whose completion the caller will wait for.
    /// </summary>
    public Job CreateJob(int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        int slot;
        lock (_counterLock)
        {
            if (_freeSlots.Count > 0)
            {
                slot = _freeSlots.Pop();
            }
            else
            {
                throw new InvalidOperationException($"The pool has no free job slots left (max {MaxCounters})");
            }
        }
        
        ref var entry = ref _entries[slot];
        entry.Fence.Reset();
        entry.Retired = 0;
        entry.Remaining = count;

#if DEBUG
        entry.CreatorThreadId = Environment.CurrentManagedThreadId;
#endif

        return new Job(this, Job.Pack(slot, entry.Generation));
    }

    /// <summary>
    ///     Adds <see cref="delta"/> units of work to a job.
    ///     Must be called before publishing the tasks those units account for, and only while the job still owns at least one unit.
    /// </summary>
    public void AddWork(Job job, int delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, 1);

        ref var entry = ref GetEntry(job);

        // The caller is an unfinished task, so it holds a unit and the counter cannot already be zero.
        if (Interlocked.Add(ref entry.Remaining, delta) <= delta)
        {
            throw new InvalidOperationException("Work was added to a job that had already completed.");
        }
    }

    /// <summary>
    ///     True once every counted unit of work has finished and the last unit has finished <see cref="Decrement"/>, so the results are safe to read and the slot safe to recycle.
    ///     The call is non-blocking.
    ///     Rethrows the pool's fault, so a result from a faulted pool is never read as if it were valid.
    /// </summary>
    public bool TryComplete(Job job)
    {
        ThrowIfFaulted();

        ref var entry = ref GetEntry(job);
        return Volatile.Read(ref entry.Retired) == 1;
    }

    /// <summary>
    ///     Blocks until <see cref="TryComplete"/> would be true.
    ///     Only the thread that created the job may wait on it, and never a pool worker, which would deadlock once every worker is waiting for work only it could run.
    ///     Only one thread may wait, because <see cref="Release"/> clears the fence for the next occupant.
    ///     Throws <see cref="ObjectDisposedException"/> if the scheduler was disposed first, because the work it abandoned will never retire the job, and rethrows the pool's fault like <see cref="TryComplete"/>.
    /// </summary>
    public void Complete(Job job)
    {
        Debug.Assert(!JobScheduler.IsWorkerThread, $"{nameof(Complete)} was called from a pool worker, which deadlocks once every worker waits");

        ThrowIfFaulted();

        ref var entry = ref GetEntry(job);
        var fence = entry.Fence;

#if DEBUG
        Debug.Assert(entry.CreatorThreadId == Environment.CurrentManagedThreadId, $"{nameof(Complete)} was called from a thread that did not create the job");
#endif

        while (Volatile.Read(ref entry.Retired) == 0)
        {
            if (_abandoned)
            {
                throw new ObjectDisposedException(nameof(JobScheduler), "The scheduler was disposed before the job completed");
            }

            fence.Wait();
        }

        ThrowIfFaulted();
    }

    /// <summary>
    ///     Rethrows the first exception a task threw, with its original stack trace.
    ///     A task that throws is considered a bug, and errors a task expects are its own code's to handle, so a fault is fatal.
    /// </summary>
    public void ThrowIfFaulted()
    {
        Volatile.Read(ref _fault)?.Throw();
    }

    /// <summary>
    ///     Records a task's exception. Only the first is kept.
    /// </summary>
    internal void Fault(Exception exception)
    {
        Interlocked.CompareExchange(ref _fault, ExceptionDispatchInfo.Capture(exception), null);
    }

    /// <summary>
    ///     Releases every thread blocked in <see cref="Complete"/> once the scheduler has stopped for good.
    ///     Called after the workers have joined, so nothing should decrement concurrently.
    /// </summary>
    internal void Abandon()
    {
        _abandoned = true;

        for (var slot = 0; slot < MaxCounters; slot++)
        {
            _entries[slot].Fence.Set();
        }
    }

    /// <summary>
    ///     Returns the counter to the pool and invalidates the handle.
    ///     Only legal once the last unit has retired, and only from the thread that created the job.
    /// </summary>
    public void Release(Job job)
    {
        ref var entry = ref GetEntry(job);

#if DEBUG
        Debug.Assert(entry.CreatorThreadId == Environment.CurrentManagedThreadId, $"{nameof(Release)} was called from a thread that did not create the job");
#endif

        if (Volatile.Read(ref entry.Retired) == 0)
        {
            throw new InvalidOperationException("The job was released before its last unit retired");
        }

        lock (_counterLock)
        {
            entry.Generation++;
            _freeSlots.Push(job.Slot);
        }
    }

    /// <summary>
    ///     Decrements a counter on behalf of a finished task.
    ///     The slot comes from a work item the pool published, so it is trusted and carries no generation.
    ///     True means this was the last unit.
    /// </summary>
    internal bool Decrement(int slot)
    {
        Debug.Assert((uint)slot < MaxCounters, $"{nameof(Decrement)} was given the out-of-range slot {slot}");

        ref var entry = ref _entries[slot];

        var remaining = Interlocked.Decrement(ref entry.Remaining);

        Debug.Assert(remaining >= 0, "A job retired more units than it counted, so its slot may already belong to the next job");

        if (remaining != 0)
        {
            return false;
        }

        entry.Fence.Set();

        Volatile.Write(ref entry.Retired, 1);
        return true;
    }

    /// <summary>
    ///     Rebuilds a handle for a slot a work item named.
    ///     The counter pins the slot until its last unit retires, so the generation is still the one the handle carried and no generation has to travel in the work item itself.
    /// </summary>
    internal Job Pack(int slot)
    {
        Debug.Assert((uint)slot < MaxCounters, $"{nameof(Pack)} was given the out-of-range slot {slot}");

        return new Job(this, Job.Pack(slot, _entries[slot].Generation));
    }

    /// <summary>
    ///     Throws unless the handle names a live counter of this pool that still has an outstanding unit.
    ///     A published item retires one unit, so an item published against a job with none would decrement past zero and, once the slot is recycled, into the next job's counter.
    /// </summary>
    internal void Validate(Job job)
    {
        ref var entry = ref GetEntry(job);

        if (Volatile.Read(ref entry.Remaining) < 1)
        {
            throw new InvalidOperationException("Work was published to a job with no outstanding units");
        }
    }

    private ref CounterEntry GetEntry(Job job)
    {
        if (!ReferenceEquals(job.Pool, this))
        {
            throw new ArgumentException("The job handle belongs to another pool", nameof(job));
        }

        var slot = job.Slot;
        if ((uint)slot >= MaxCounters)
        {
            throw new ArgumentException($"The job slot {slot} is out of range", nameof(job));
        }

        ref var entry = ref _entries[slot];
        if (entry.Generation != job.Generation)
        {
            throw new InvalidOperationException("The job handle has already been released");
        }

        return ref entry;
    }
}
