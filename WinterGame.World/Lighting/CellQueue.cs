using System.Runtime.CompilerServices;

namespace WinterGame.World.Lighting;

/// <summary>
///     A first-in first-out queue of uint entries in one array.
///     The queue starts again at the front of the array whenever it empties, and the array only grows.
/// </summary>
internal sealed class CellQueue(int capacity)
{
    private uint[] _items = new uint[capacity];
    private int _head;
    private int _tail;

    public int Count => _tail - _head;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enqueue(uint item)
    {
        if (_tail == _items.Length)
        {
            MakeRoom();
        }

        _items[_tail++] = item;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out uint item)
    {
        if (_head == _tail)
        {
            _head = 0;
            _tail = 0;
            item = 0;

            return false;
        }

        item = _items[_head++];

        return true;
    }

    /// <summary>
    ///     Makes room at the tail by moving the entries to the front of the array.
    ///     The array doubles when the entries fill more than half of it.
    /// </summary>
    private void MakeRoom()
    {
        var count = _tail - _head;

        if (count <= _items.Length / 2)
        {
            Array.Copy(_items, _head, _items, 0, count);
        }
        else
        {
            var items = new uint[_items.Length * 2];

            Array.Copy(_items, _head, items, 0, count);
            _items = items;
        }

        _head = 0;
        _tail = count;
    }
}
