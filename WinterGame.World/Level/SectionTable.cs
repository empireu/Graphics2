using WinterGame.World.Level.Meshing;
using WinterGame.World.Storage;

namespace WinterGame.World.Level;

/// <summary>
///     The level's sections: their voxels in <see cref="Terrain"/>, their state in <see cref="Blocks"/>, and the transitions between states.
///     A section only becomes generating, ready or gone through this table, so whatever has to follow a transition happens in one place.
/// </summary>
internal sealed class SectionTable(SectionBlockMap blocks, MeshReadiness meshReadiness)
{
    public readonly SectionArena Terrain = new();

    public SectionBlockMap Blocks => blocks;

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
