using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace WinterGame.World;

/// <summary>
///     A handle to one block's storage in a <see cref="SectionBlockAllocator"/>.
///     It names the block by its index, so it holds no reference and storing it costs no write barrier.
/// </summary>
internal readonly struct SectionBlock(int index) : IEquatable<SectionBlock>
{
    public readonly int Index = index;

    public bool Equals(SectionBlock other)
    {
        return Index == other.Index;
    }

    public override bool Equals(object? obj)
    {
        return obj is SectionBlock other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Index;
    }

    public static bool operator ==(SectionBlock left, SectionBlock right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(SectionBlock left, SectionBlock right)
    {
        return !left.Equals(right);
    }
}

/// <summary>
///     A section's slot, which is its arena slot while it is ready and its executor slot while it is generating, and the content sides of a ready section.
///     It is only meaningful while its section is generating or ready.
///     The sides share the slot's element because a fourth span in <see cref="SectionBlockView"/> measured slower on every unload.
/// </summary>
internal readonly struct SectionSlot(int index, SectionContentSides contentSides)
{
    public readonly int Index = index;
    public readonly SectionContentSides ContentSides = contentSides;
}

/// <summary>
///     One block's storage: a ticket count and a slot for each section, and one bit for each section in every mask.
///     Pages never move, so a view stays valid until its block is freed.
/// </summary>
internal readonly ref struct SectionBlockView(Span<int> ticketCounts, Span<SectionSlot> slots, Span<ulong> masks)
{
    /// <summary>
    ///     The sections whose ticket count is above zero.
    /// </summary>
    public const int ClaimedMask = 0;

    public const int ReadyMask = 1;
    public const int GeneratingMask = 2;

    /// <summary>
    ///     The ready sections waiting in the unload queue.
    /// </summary>
    public const int UnloadQueuedMask = 3;

    public const int MaskCount = 4;

    /// <summary>
    ///     The tickets whose keep shape covers each section.
    /// </summary>
    public readonly Span<int> TicketCounts = ticketCounts;

    /// <summary>
    ///     Each section's slot, and its content sides while it is ready.
    /// </summary>
    public readonly Span<SectionSlot> Slots = slots;

    private readonly Span<ulong> _masks = masks;

    /// <summary>
    ///     The sections that have an entry, which are the claimed, generating and ready ones. A block with none is freed.
    /// </summary>
    public ulong Occupied => _masks[ClaimedMask] | _masks[ReadyMask] | _masks[GeneratingMask];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetBit(int localIndex)
    {
        return 1UL << localIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetMask(int mask)
    {
        return _masks[mask];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsClaimed(int localIndex)
    {
        return (_masks[ClaimedMask] & GetBit(localIndex)) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsReady(int localIndex)
    {
        return (_masks[ReadyMask] & GetBit(localIndex)) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsGenerating(int localIndex)
    {
        return (_masks[GeneratingMask] & GetBit(localIndex)) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsReadyOrGenerating(int localIndex)
    {
        return ((_masks[ReadyMask] | _masks[GeneratingMask]) & GetBit(localIndex)) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsUnloadQueued(int localIndex)
    {
        return (_masks[UnloadQueuedMask] & GetBit(localIndex)) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetClaimed(int localIndex)
    {
        _masks[ClaimedMask] |= GetBit(localIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearClaimed(int localIndex)
    {
        _masks[ClaimedMask] &= ~GetBit(localIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetUnloadQueued(int localIndex)
    {
        _masks[UnloadQueuedMask] |= GetBit(localIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearUnloadQueued(int localIndex)
    {
        _masks[UnloadQueuedMask] &= ~GetBit(localIndex);
    }

    /// <summary>
    ///     Marks the section generating into <paramref name="executorSlot"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetGenerating(int localIndex, int executorSlot)
    {
        Slots[localIndex] = new SectionSlot(executorSlot, default);
        _masks[GeneratingMask] |= GetBit(localIndex);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearGenerating(int localIndex)
    {
        _masks[GeneratingMask] &= ~GetBit(localIndex);
    }

    /// <summary>
    ///     Marks the section ready in <paramref name="arenaSlot"/>, replacing a generation in flight if there is one.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetReady(int localIndex, int arenaSlot, SectionContentSides contentSides)
    {
        var bit = GetBit(localIndex);

        Slots[localIndex] = new SectionSlot(arenaSlot, contentSides);
        _masks[GeneratingMask] &= ~bit;
        _masks[ReadyMask] |= bit;
    }

    /// <summary>
    ///     Marks the section no longer ready. Its slot keeps its old value, which nothing reads until the section is generating or ready again.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ClearReady(int localIndex)
    {
        _masks[ReadyMask] &= ~GetBit(localIndex);
    }
}

/// <summary>
///     The storage of a level's blocks of sections, one page array per field, where every field is a number.
///     No page holds a reference, so the collector never traces one and a store never pays a write barrier, and pages never move, so a block's storage stays where it is until it is freed.
/// </summary>
internal sealed class SectionBlockAllocator
{
    private const int PageLog = 10;

    /// <summary>
    ///     The number of blocks per page.
    /// </summary>
    private const int PageSize = 1 << PageLog;

    private const int PageMask = PageSize - 1;

    private readonly List<int[]> _ticketCountPages = [];
    private readonly List<SectionSlot[]> _slotPages = [];
    private readonly List<ulong[]> _maskPages = [];
    private readonly Stack<int> _freeBlocks = [];

#if DEBUG
    private readonly HashSet<int> _inUse = [];
#endif

    /// <summary>
    ///     Hands out a block whose ticket counts and masks are all zero.
    /// </summary>
    public SectionBlock Allocate()
    {
        if (!_freeBlocks.TryPop(out var index))
        {
            AllocatePage();
            index = _freeBlocks.Pop();
        }

#if DEBUG
        Debug.Assert(_inUse.Add(index), $"Block {index} was handed out while in use.");
#endif

        return new SectionBlock(index);
    }

    /// <summary>
    ///     Returns an empty block, one with no ticket count and no mask bit, so its next owner starts from zero.
    /// </summary>
    public void Free(SectionBlock block)
    {
#if DEBUG
        Debug.Assert(_inUse.Remove(block.Index), $"Block {block.Index} was freed while not in use.");

        var view = Get(block);

        Debug.Assert(view.Occupied == 0 && view.GetMask(SectionBlockView.UnloadQueuedMask) == 0 && !view.TicketCounts.ContainsAnyExcept(0), $"Block {block.Index} was freed while not empty.");
#endif

        _freeBlocks.Push(block.Index);
    }

    /// <summary>
    ///     One mask of a block, read without making a view, which is the cheaper path for a read.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong GetMask(SectionBlock block, int mask)
    {
        return _maskPages[block.Index >> PageLog][(block.Index & PageMask) * SectionBlockView.MaskCount + mask];
    }

    /// <summary>
    ///     Whether one section's bit is set in a mask, read without making a view.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSet(SectionBlock block, int mask, int localIndex)
    {
        return (GetMask(block, mask) & SectionBlockView.GetBit(localIndex)) != 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public SectionSlot GetSlot(SectionBlock block, int localIndex)
    {
        return _slotPages[block.Index >> PageLog][(block.Index & PageMask) * SectionBlockPos.SectionsPerBlock + localIndex];
    }

    public SectionBlockView Get(SectionBlock block)
    {
        var page = block.Index >> PageLog;
        var offset = block.Index & PageMask;

        return new SectionBlockView(
            _ticketCountPages[page].AsSpan(offset * SectionBlockPos.SectionsPerBlock, SectionBlockPos.SectionsPerBlock),
            _slotPages[page].AsSpan(offset * SectionBlockPos.SectionsPerBlock, SectionBlockPos.SectionsPerBlock),
            _maskPages[page].AsSpan(offset * SectionBlockView.MaskCount, SectionBlockView.MaskCount)
        );
    }

    private void AllocatePage()
    {
        var first = _ticketCountPages.Count * PageSize;

        _ticketCountPages.Add(new int[PageSize * SectionBlockPos.SectionsPerBlock]);
        _slotPages.Add(new SectionSlot[PageSize * SectionBlockPos.SectionsPerBlock]);
        _maskPages.Add(new ulong[PageSize * SectionBlockView.MaskCount]);

        // Pushed from the end, so the page hands out its blocks in address order:
        for (var index = first + PageSize - 1; index >= first; index--)
        {
            _freeBlocks.Push(index);
        }
    }
}