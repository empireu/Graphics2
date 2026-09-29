using Graphics2.Mathematics;

namespace WinterGame.Jobs;

/// <summary>
///     Bounded multi-producer, multi-consumer queue of work items, following Dmitry Vyukov's implementation.
/// </summary>
internal sealed class BoundedMpmcQueue
{
    private struct Cell
    {
        public long Sequence;
        public long Item;
    }

    private readonly Cell[] _cells;
    private readonly int _mask;
    private long _enqueuePosition;
    private long _dequeuePosition;

    public BoundedMpmcQueue(int capacity)
    {
        if (!MathX.IsPowerOfTwo(capacity))
        {
            throw new ArgumentException($"The {nameof(capacity)} must be a power of two", nameof(capacity));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);

        _mask = capacity - 1;
        _cells = new Cell[capacity];

        for (var index = 0; index < capacity; index++)
        {
            _cells[index].Sequence = index;
        }
    }

    public int Capacity => _cells.Length;

    /// <summary>
    ///     Approximate number of items, because both positions move under any reader.
    /// </summary>
    public int ApproximateCount
    {
        get
        {
            var count = Volatile.Read(ref _enqueuePosition) - Volatile.Read(ref _dequeuePosition);

            if (count <= 0)
            {
                return 0;
            }

            return count >= Capacity ? Capacity : (int)count;
        }
    }

    /// <summary>
    ///     False means the queue is full.
    /// </summary>
    public bool TryEnqueue(long item)
    {
        var position = Volatile.Read(ref _enqueuePosition);

        while (true)
        {
            ref var cell = ref _cells[position & _mask];
            var difference = Volatile.Read(ref cell.Sequence) - position;

            if (difference == 0)
            {
                var observed = Interlocked.CompareExchange(ref _enqueuePosition, position + 1, position);

                if (observed == position)
                {
                    cell.Item = item;
                    Volatile.Write(ref cell.Sequence, position + 1);
                    return true;
                }

                position = observed;
            }
            else if (difference < 0)
            {
                return false;
            }
            else
            {
                position = Volatile.Read(ref _enqueuePosition);
            }
        }
    }

    /// <summary>
    ///     False means the queue is empty.
    /// </summary>
    public bool TryDequeue(out long item)
    {
        var position = Volatile.Read(ref _dequeuePosition);

        while (true)
        {
            ref var cell = ref _cells[position & _mask];
            var difference = Volatile.Read(ref cell.Sequence) - (position + 1);

            if (difference == 0)
            {
                var observed = Interlocked.CompareExchange(ref _dequeuePosition, position + 1, position);

                if (observed == position)
                {
                    item = cell.Item;
                    Volatile.Write(ref cell.Sequence, position + _mask + 1);
                    return true;
                }

                position = observed;
            }
            else if (difference < 0)
            {
                item = 0;
                return false;
            }
            else
            {
                position = Volatile.Read(ref _dequeuePosition);
            }
        }
    }
}
