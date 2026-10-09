using WinterGame.World.Level.Meshing;
using WinterGame.World.Storage;

namespace WinterGame.World.Level;

/// <summary>
///     The level's sections: their voxels in <see cref="Terrain"/>, their state in <see cref="Blocks"/>, and the transitions between states.
///     A section only becomes generating, ready or gone through this table, so whatever has to follow a transition happens in one place.
/// </summary>
internal sealed class SectionTable(SectionBlockMap blocks, MeshReadiness meshReadiness, bool recordsChanges)
{
    public readonly SectionArena Terrain = new();

    /// <summary>
    ///     The sections that became ready, were written or were destroyed since the last <see cref="TakeChanges"/>, in the order it happened, kept only while <see cref="RecordsChanges"/>.
    /// </summary>
    private readonly List<SectionChange> _changes = [];

    public SectionBlockMap Blocks => blocks;

    public bool RecordsChanges => recordsChanges;

    /// <summary>
    ///     Marks a claimed section generating into <paramref name="executorSlot"/>.
    /// </summary>
    // ReSharper disable once MemberCanBeMadeStatic.Global
    public void MarkGenerating(SectionBlockView view, int localIndex, int executorSlot)
    {
        view.SetGenerating(localIndex, executorSlot);
    }

    /// <summary>
    ///     Marks the section ready in <paramref name="arenaSlot"/>, replacing a generation in flight if there is one, and lists it for the next mesh readiness check.
    /// </summary>
    public void MarkReady(ulong blockKey, SectionBlockView view, int localIndex, int arenaSlot, SectionContentSides contentSides)
    {
        meshReadiness.TrackNewlyReady(blockKey, view);
        view.SetReady(localIndex, arenaSlot, contentSides);

        if (recordsChanges)
        {
            _changes.Add(new SectionChange(new SectionBlockPos(blockKey).GetSection(localIndex), SectionChangeKind.Ready));
        }
    }

    /// <summary>
    ///     Records that a write changed some of a ready section's voxels.
    /// </summary>
    public void RecordWritten(SectionPos position)
    {
        if (recordsChanges)
        {
            _changes.Add(new SectionChange(position, SectionChangeKind.Written));
        }
    }

    public void TakeChanges(List<SectionChange> destination)
    {
        destination.AddRange(_changes);
        _changes.Clear();
    }

    /// <summary>
    ///     Drops a generation whose section no ticket holds any more, and frees its block when nothing else in it has an entry.
    /// </summary>
    public void DropGeneration(ulong blockKey, SectionBlockView view, int localIndex)
    {
        view.ClearGenerating(localIndex);
        blocks.FreeIfEmpty(blockKey, view);
    }

    /// <summary>
    ///     Destroys a loaded section that no ticket holds, retires its mesh if it was reported, and frees its block when nothing else in it has an entry.
    /// </summary>
    public void Destroy(SectionPos position, ulong blockKey, SectionBlockView view, int localIndex)
    {
        Terrain.Destroy(position);

        if (view.IsMeshReported(localIndex))
        {
            meshReadiness.Retire(position, view, localIndex);
        }

        view.ClearReady(localIndex);
        blocks.FreeIfEmpty(blockKey, view);

        if (recordsChanges)
        {
            _changes.Add(new SectionChange(position, SectionChangeKind.Destroyed));
        }
    }

    public SectionStatus GetStatus(SectionPos position)
    {
        return blocks.TryGet(SectionBlockPos.FromSection(position).GetKey(), out var block) ? GetStatus(block, SectionBlockPos.GetLocalIndex(position)) : SectionStatus.None;
    }

    public SectionStatus GetStatus(SectionBlock block, int localIndex)
    {
        if (blocks.IsSet(block, SectionBlockView.ReadyMask, localIndex))
        {
            return SectionStatus.Ready;
        }

        return blocks.IsSet(block, SectionBlockView.GeneratingMask, localIndex) ? SectionStatus.Generating : SectionStatus.None;
    }

    public bool TryGetSection(SectionPos position, out Section section)
    {
        if (blocks.TryGet(SectionBlockPos.FromSection(position).GetKey(), out var block))
        {
            return TryGetSection(block, position, out section);
        }

        section = default;
        return false;
    }

    /// <summary>
    ///     The section's handle when it is loaded, rebuilt from the arena slot its block holds.
    /// </summary>
    public bool TryGetSection(SectionBlock block, SectionPos position, out Section section)
    {
        var index = SectionBlockPos.GetLocalIndex(position);

        if (blocks.IsSet(block, SectionBlockView.ReadyMask, index))
        {
            section = new Section(Terrain, position, blocks.GetSlot(block, index).Index);
            return true;
        }

        section = default;
        return false;
    }
}
