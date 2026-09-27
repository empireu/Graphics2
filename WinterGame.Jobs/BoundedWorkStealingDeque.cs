using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Graphics2.Mathematics;

namespace WinterGame.Jobs;

internal sealed class SlotBuffer(int capacity)
{
    public readonly int Capacity = capacity;
    
    private readonly int _mask = capacity - 1;
    private readonly long[] _items = new long[capacity];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(long index, long handle)
    {
        Volatile.Write(ref _items[(int)(index & _mask)], handle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long Load(long index)
    {
        return Volatile.Read(ref _items[(int)(index & _mask)]);
    }
}

[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct WorkQueueState
{
    [FieldOffset(0)]
    public long Top;

    [FieldOffset(64)]
    public long Bottom;
    
    [FieldOffset(72)]
    public long CachedTop;
}

/// <summary>
///     Bounded form of the Chase-Lev deque, following the BoundedWSQ reference in: https://github.com/taskflow/work-stealing-queue (wsq.hpp)
/// </summary>
internal sealed class BoundedWorkStealingDeque
{
    public const int MinCapacity = 2;
    public const int MaxCapacity = 1 << 16;
    
    private WorkQueueState _state;
    private readonly SlotBuffer _buffer;

    public BoundedWorkStealingDeque(int capacity = 1024)
    {
        if (!MathX.IsPowerOfTwo(capacity))
        {
            throw new ArgumentException(
                $"The {nameof(capacity)} must be a power of two"
            );
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, MinCapacity);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, MaxCapacity);
        
        _buffer = new SlotBuffer(capacity);
    }

    public int Capacity => _buffer.Capacity;

    /// <summary>
    ///     Approximate number of elements (because the top can move under any reader).
    /// </summary>
    public int ApproximateCount
    {
        get
        {
            var top = Volatile.Read(ref _state.Top);
            var bottom = Volatile.Read(ref _state.Bottom);
            return (int)(bottom >= top ? bottom - top : 0);
        }
    }

    /// <summary>
    ///     Owner-only push at the bottom. False means the buffer is full.
    /// </summary>
    public bool TryPushBottom(long handle)
    {
        var bottom = Volatile.Read(ref _state.Bottom);

        if (_buffer.Capacity - 1 < bottom - _state.CachedTop)
        {
            var top = Volatile.Read(ref _state.Top);
            _state.CachedTop = top;

            if (_buffer.Capacity - 1 < bottom - top)
            {
                return false;
            }
        }

        _buffer.Store(bottom, handle);
        
        Volatile.Write(ref _state.Bottom, bottom + 1);

        return true;
    }

    /// <summary>
    ///     Owner-only pop from the bottom.
    /// </summary>
    public bool PopBottom(out long handle)
    {
        handle = -1;

        var bottom = Volatile.Read(ref _state.Bottom) - 1;
        
        Volatile.Write(ref _state.Bottom, bottom);

        Thread.MemoryBarrier();

        var top = Volatile.Read(ref _state.Top);

        if (top <= bottom)
        {
            var localHandle = _buffer.Load(bottom);

            if (top == bottom)
            {
                if (Interlocked.CompareExchange(ref _state.Top, top + 1, top) != top)
                {
                    Volatile.Write(ref _state.Bottom, bottom + 1);
                    return false;
                }

                Volatile.Write(ref _state.Bottom, bottom + 1);
            }

            handle = localHandle;
            return true;
        }

        Volatile.Write(ref _state.Bottom, bottom + 1);
      
        return false;
    }

    /// <summary>
    ///     Thief-only steal. Empty on a lost CAS, with no retry.
    /// </summary>
    public bool StealTop(out long handle)
    {
        handle = -1;

        var top = Volatile.Read(ref _state.Top);

        Thread.MemoryBarrier();

        var bottom = Volatile.Read(ref _state.Bottom);

        if (top < bottom)
        {
            var localHandle = _buffer.Load(top);

            if (Interlocked.CompareExchange(ref _state.Top, top + 1, top) != top)
            {
                return false;
            }

            handle = localHandle;
            return true;
        }

        return false;
    }
}