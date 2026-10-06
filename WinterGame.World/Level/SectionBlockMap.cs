using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinterGame.World.Storage;

namespace WinterGame.World.Level;

/// <summary>
///     Every block holding a section that has an entry, by block key, with its storage.
///     A section has an entry while it is claimed, generating or ready, and a block left with none is freed.
/// </summary>
internal sealed class SectionBlockMap
{
    private readonly Dictionary<ulong, SectionBlock> _blocks = [];
    private readonly SectionBlockAllocator _storage = new();

    /// <summary>
    ///     Counts the blocks added and removed, so a <see cref="FastLevelAccessor"/> can check that the block it remembers is still the map's.
    /// </summary>
    public int Version { get; private set; }

    public bool TryGet(ulong blockKey, out SectionBlock block)
    {
        return _blocks.TryGetValue(blockKey, out block);
    }

    /// <summary>
    ///     The storage of the block holding the section. False when no section of that block has an entry.
    /// </summary>
    public bool TryGet(SectionPos position, out ulong blockKey, out SectionBlockView view)
    {
        blockKey = SectionBlockPos.FromSection(position).GetKey();

        if (_blocks.TryGetValue(blockKey, out var block))
        {
            view = _storage.Get(block);
            return true;
        }

        view = default;
        return false;
    }

    /// <summary>
    ///     The storage of a block that has to be in the map.
    /// </summary>
    public SectionBlockView Get(ulong blockKey)
    {
        return _storage.Get(_blocks[blockKey]);
    }

    public SectionBlockView Get(SectionBlock block)
    {
        return _storage.Get(block);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetMask(SectionBlock block, int mask)
    {
        return _storage.GetMask(block, mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSet(SectionBlock block, int mask, int localIndex)
    {
        return _storage.IsSet(block, mask, localIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SectionSlot GetSlot(SectionBlock block, int localIndex)
    {
        return _storage.GetSlot(block, localIndex);
    }

    public SectionBlockView GetOrAdd(ulong blockKey)
    {
        ref var block = ref CollectionsMarshal.GetValueRefOrAddDefault(_blocks, blockKey, out var exists);

        if (!exists)
        {
            block = _storage.Allocate();
            Version++;
        }

        return _storage.Get(block);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void FreeIfEmpty(ulong blockKey, SectionBlockView view)
    {
        if (view.Occupied != 0)
        {
            return;
        }

        Free(blockKey, view);
    }

    private void Free(ulong blockKey, SectionBlockView view)
    {
        // A section destroyed since it became ready leaves its newly ready bit, and the next owner of the block has to start from zero:
        view.ClearNewlyReady();

        _blocks.Remove(blockKey, out var block);
        _storage.Free(block);
        Version++;
    }

    /// <summary>
    ///     The sections with an entry, counted over every block.
    /// </summary>
    public int CountOccupied()
    {
        var count = 0;

        foreach (var block in _blocks.Values)
        {
            count += BitOperations.PopCount(_storage.Get(block).Occupied);
        }

        return count;
    }

    /// <summary>
    ///     The sections whose bit is set in one mask, counted over every block.
    /// </summary>
    public int CountSet(int mask)
    {
        var count = 0;

        foreach (var block in _blocks.Values)
        {
            count += BitOperations.PopCount(_storage.GetMask(block, mask));
        }

        return count;
    }
}
