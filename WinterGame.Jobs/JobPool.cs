using System.Diagnostics;

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

public sealed class JobPoolDescription
{
    public required int ThreadCount { get; init; } = 4;
}

public sealed class JobPool(JobPoolDescription description)
{
    /// <summary>
    ///     The slot half of a handle is 16 bits, which fixes the slab size.
    /// </summary>
    public const int MaxCounters = 1 << 16;

   private struct CounterEntry
   {
       public uint Generation;
       public int Remaining;
   }

    private readonly CounterEntry[] _entries = new CounterEntry[MaxCounters];
    
    /// <summary>
    ///     Slots that have been released and are ready to hand out again.
    ///     Slots below <see cref="_nextSlot"/> that are not here have never been used.
    /// </summary>
    private readonly Stack<int> _releasedSlots = new();
    private readonly Lock _counterLock = new();
    private int _nextSlot;

    public int ThreadCount { get; } = description.ThreadCount;

    /// <summary>
    ///     Number of slots that <see cref="CreateJob"/> can still hand out.
    /// </summary>
    public int AvailableCounters
    {
        get
        {
            lock (_counterLock)
            {
                return MaxCounters - _nextSlot + _releasedSlots.Count;
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
            if (_releasedSlots.Count > 0)
            {
                slot = _releasedSlots.Pop();
            }
            else if (_nextSlot < MaxCounters)
            {
                slot = _nextSlot++;
            }
            else
            {
                throw new InvalidOperationException($"The pool has no free job slots left (max {MaxCounters})");
            }
        }

        // The slot is ours alone until release, so its generation is stable here.
        ref var entry = ref _entries[slot];
        entry.Remaining = count;

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
    ///     True once every counted unit of work has finished.
    ///     The call is non-blocking; it only reads the counter.
    /// </summary>
    public bool TryComplete(Job job)
    {
        ref var entry = ref GetEntry(job);
        return Volatile.Read(ref entry.Remaining) == 0;
    }

    /// <summary>
    ///     Returns the counter to the slab and invalidates the handle.
    ///     Only legal once the job is complete, and only from the thread that created it.
    /// </summary>
    public void Release(Job job)
    {
        ref var entry = ref GetEntry(job);

        if (Volatile.Read(ref entry.Remaining) != 0)
        {
            throw new InvalidOperationException("The job was released before it completed.");
        }

        lock (_counterLock)
        {
            entry.Generation++;
            _releasedSlots.Push(job.Slot);
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

        return Interlocked.Decrement(ref _entries[slot].Remaining) == 0;
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
