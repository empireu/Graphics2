using System.Numerics;
using Graphics2.Mathematics;
using WinterGame.World.Storage;

// ReSharper disable ForCanBeConvertedToForeach

namespace WinterGame.World.Level.Meshing;

/// <summary>
///     Decides when a section is meshed, and records the mesh changes for <see cref="WorldLevel.TakeMeshChanges"/>.
///     A section is reported to mesh once it and its 26 neighbors are ready and it holds a surface, again when a write reaches its mesh window, and to retire when it is destroyed.
/// </summary>
internal sealed class MeshReadiness(SectionBlockMap blocks, BoundingBox3i meshWindow)
{
    /// <summary>
    ///     The blocks with a newly ready section, each added when its first section became ready since the last <see cref="ReportCompletedNeighborhoods()"/>.
    /// </summary>
    private readonly List<ulong> _newlyReadyBlocks = [];

    /// <summary>
    ///     The blocks <see cref="ReportCompletedNeighborhoods()"/> looks for completed neighborhoods in, which are the newly ready blocks and the neighbors their newly ready sections touch.
    /// </summary>
    private readonly HashSet<ulong> _blocksToCheck = [];

    /// <summary>
    ///     The mesh changes since the last <see cref="Take"/>, in the order they happened.
    /// </summary>
    private readonly List<SectionMeshChange> _meshChanges = [];

    /// <summary>
    ///     The samples a section's mesh reads, relative to its origin, which decide the sections a write has to mesh again.
    /// </summary>
    public BoundingBox3i MeshWindow => meshWindow;

    /// <summary>
    ///     Lists the block for the next <see cref="ReportCompletedNeighborhoods()"/>. Called before a section of it is marked newly ready, so the block is listed once.
    /// </summary>
    public void TrackNewlyReady(ulong blockKey, SectionBlockView view)
    {
        if (view.GetMask(SectionBlockView.NewlyReadyMask) == 0)
        {
            _newlyReadyBlocks.Add(blockKey);
        }
    }

    /// <summary>
    ///     Retires the reported mesh of a section being destroyed.
    /// </summary>
    public void Retire(SectionPos position, SectionBlockView view, int localIndex)
    {
        view.ClearMeshReported(localIndex);
        _meshChanges.Add(new SectionMeshChange(position, SectionMeshChangeKind.Retire));
    }

    /// <summary>
    ///     Moves the mesh changes since the last call into <paramref name="destination"/>, in the order they happened.
    /// </summary>
    public void Take(List<SectionMeshChange> destination)
    {
        destination.AddRange(_meshChanges);
        _meshChanges.Clear();
    }

    /// <summary>
    ///     Reports a ready and reported section to mesh again once it and its 26 neighbors are ready. Anything else is left alone.
    /// </summary>
    public void ReportAgain(SectionPos position)
    {
        var index = SectionBlockPos.GetLocalIndex(position);

        if (!blocks.TryGet(position, out var blockKey, out var view) || !view.IsReady(index) || !view.IsMeshReported(index))
        {
            return;
        }

        TrackNewlyReady(blockKey, view);

        // Newly ready again, so the next check looks at it, and its neighbors' return reaches it if that check finds it incomplete:
        view.ClearMeshReported(index);
        view.SetNewlyReady(index);
    }

    /// <summary>
    ///     Reports every section whose neighborhood became ready since the last call and that holds a surface, and clears the newly ready masks.
    ///     A section only completes a neighborhood within one section of it, so the blocks to check are the newly ready blocks and the neighbors their newly ready sections touch.
    /// </summary>
    public void ReportCompletedNeighborhoods()
    {
        if (_newlyReadyBlocks.Count == 0)
        {
            return;
        }

        for (var index = 0; index < _newlyReadyBlocks.Count; index++)
        {
            var blockKey = _newlyReadyBlocks[index];

            if (blocks.TryGet(blockKey, out var block))
            {
                AddBlocksToCheck(blockKey,
                    blocks.GetMask(block, SectionBlockView.NewlyReadyMask) &
                    blocks.GetMask(block, SectionBlockView.ReadyMask)
                );
            }
        }

        foreach (var blockKey in _blocksToCheck)
        {
            ReportCompletedNeighborhoods(blockKey);
        }

        // Cleared only now, since every check reads the newly ready masks of its neighbors:
        for (var index = 0; index < _newlyReadyBlocks.Count; index++)
        {
            var blockKey = _newlyReadyBlocks[index];

            if (blocks.TryGet(blockKey, out var block))
            {
                blocks.Get(block).ClearNewlyReady();
            }
        }

        _newlyReadyBlocks.Clear();
        _blocksToCheck.Clear();
    }

    private void AddBlocksToCheck(ulong blockKey, ulong newlyReady)
    {
        var position = new SectionBlockPos(blockKey);

        for (var offsetZ = -1; offsetZ <= 1; offsetZ++)
        {
            for (var offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (var offsetX = -1; offsetX <= 1; offsetX++)
                {
                    if ((newlyReady & SectionBlockNeighborhoodOps.GetBorder(offsetX, offsetY, offsetZ)) != 0)
                    {
                        _blocksToCheck.Add(SectionBlockPos.Pack(position.X + offsetX, position.Y + offsetY, position.Z + offsetZ));
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Reports the sections of one block whose 26 neighbors and themselves are ready, with one of them newly ready, and that hold a surface.
    /// </summary>
    private void ReportCompletedNeighborhoods(ulong blockKey)
    {
        if (!blocks.TryGet(blockKey, out var center))
        {
            return;
        }

        var view = blocks.Get(center);
        var reported = view.GetMask(SectionBlockView.MeshReportedMask);

        if ((view.GetMask(SectionBlockView.ReadyMask) & ~reported) == 0)
        {
            return;
        }

        var position = new SectionBlockPos(blockKey);
        Span<SectionBlock> neighbors = stackalloc SectionBlock[SectionBlockNeighborhoodOps.Count];
        Span<ulong> ready = stackalloc ulong[SectionBlockNeighborhoodOps.Count];
        Span<ulong> newlyReady = stackalloc ulong[SectionBlockNeighborhoodOps.Count];

        GetNeighborhood(position, neighbors, ready, newlyReady);

        var candidates = SectionBlockNeighborhoodOps.Erode(ready) & SectionBlockNeighborhoodOps.Dilate(newlyReady) & ~reported;

        while (candidates != 0)
        {
            var localIndex = BitOperations.TrailingZeroCount(candidates);

            candidates &= candidates - 1;

            if (!HasSurface(neighbors, localIndex))
            {
                continue;
            }

            view.SetMeshReported(localIndex);
            _meshChanges.Add(new SectionMeshChange(position.GetSection(localIndex), SectionMeshChangeKind.Mesh));
        }
    }

    /// <summary>
    ///     Reports every ready section whose mesh window overlaps the changed box, grouped by block.
    /// </summary>
    public void ReportEditedSections(Vector3i changedMin, Vector3i changedMax)
    {
        // A window overlaps the box when the section's origin lies in the box grown by the window on the opposite side:
        var sectionMin = (changedMin - meshWindow.Max + SectionPos.SectionMask) >> SectionPos.Log;
        var sectionMax = (changedMax - meshWindow.Min) >> SectionPos.Log;
        var blockMin = sectionMin >> SectionBlockPos.Log;
        var blockMax = sectionMax >> SectionBlockPos.Log;

        for (var blockZ = blockMin.Z; blockZ <= blockMax.Z; blockZ++)
        {
            for (var blockY = blockMin.Y; blockY <= blockMax.Y; blockY++)
            {
                for (var blockX = blockMin.X; blockX <= blockMax.X; blockX++)
                {
                    var blockOrigin = new Vector3i(blockX, blockY, blockZ) << SectionBlockPos.Log;
                    var clipMin = Vector3i.Max(sectionMin, blockOrigin);
                    var clipMax = Vector3i.Min(sectionMax, blockOrigin + SectionBlockPos.BlockMask);
                    var dirty = 0UL;

                    for (var z = clipMin.Z; z <= clipMax.Z; z++)
                    {
                        for (var y = clipMin.Y; y <= clipMax.Y; y++)
                        {
                            for (var x = clipMin.X; x <= clipMax.X; x++)
                            {
                                dirty |= SectionBlockView.GetBit(SectionBlockPos.GetLocalIndex(x, y, z));
                            }
                        }
                    }

                    ReportEditedSections(SectionBlockPos.Pack(blockX, blockY, blockZ), dirty);
                }
            }
        }
    }

    /// <summary>
    ///     Reports the ready sections of <paramref name="dirty"/> in one block. A section with a mesh is meshed again even when it lost its surface, so the empty mesh replaces the old one, and a section without one is meshed once its neighborhood is ready and it holds a surface.
    /// </summary>
    private void ReportEditedSections(ulong blockKey, ulong dirty)
    {
        if (!blocks.TryGet(blockKey, out var center))
        {
            return;
        }

        var view = blocks.Get(center);

        dirty &= view.GetMask(SectionBlockView.ReadyMask);

        if (dirty == 0)
        {
            return;
        }

        var position = new SectionBlockPos(blockKey);
        Span<SectionBlock> neighbors = stackalloc SectionBlock[SectionBlockNeighborhoodOps.Count];
        Span<ulong> ready = stackalloc ulong[SectionBlockNeighborhoodOps.Count];
        Span<ulong> newlyReady = stackalloc ulong[SectionBlockNeighborhoodOps.Count];

        GetNeighborhood(position, neighbors, ready, newlyReady);

        var reported = view.GetMask(SectionBlockView.MeshReportedMask);
        var complete = SectionBlockNeighborhoodOps.Erode(ready);

        while (dirty != 0)
        {
            var localIndex = BitOperations.TrailingZeroCount(dirty);
            var bit = SectionBlockView.GetBit(localIndex);

            dirty &= dirty - 1;

            if ((reported & bit) == 0)
            {
                if ((complete & bit) == 0 || !HasSurface(neighbors, localIndex))
                {
                    continue;
                }

                view.SetMeshReported(localIndex);
            }

            _meshChanges.Add(new SectionMeshChange(position.GetSection(localIndex), SectionMeshChangeKind.Edit));
        }
    }

    /// <summary>
    ///     Looks up a block and its 26 neighbors with their ready and newly ready masks. A block that is not there has no ready section, so its masks are zero.
    /// </summary>
    private void GetNeighborhood(SectionBlockPos position, Span<SectionBlock> neighbors, Span<ulong> ready, Span<ulong> newlyReady)
    {
        for (var offsetZ = -1; offsetZ <= 1; offsetZ++)
        {
            for (var offsetY = -1; offsetY <= 1; offsetY++)
            {
                for (var offsetX = -1; offsetX <= 1; offsetX++)
                {
                    var index = SectionBlockNeighborhoodOps.GetIndex(offsetX, offsetY, offsetZ);

                    if (!blocks.TryGet(SectionBlockPos.Pack(position.X + offsetX, position.Y + offsetY, position.Z + offsetZ), out var neighbor))
                    {
                        ready[index] = 0;
                        newlyReady[index] = 0;
                        continue;
                    }

                    neighbors[index] = neighbor;
                    ready[index] = blocks.GetMask(neighbor, SectionBlockView.ReadyMask);
                    newlyReady[index] = blocks.GetMask(neighbor, SectionBlockView.NewlyReadyMask);
                }
            }
        }
    }

    /// <summary>
    ///     Whether a section of the center block holds a surface, from its content sides and those of its positive neighbors, which have to be ready.
    /// </summary>
    private bool HasSurface(ReadOnlySpan<SectionBlock> neighbors, int localIndex)
    {
        var x = localIndex & SectionBlockPos.BlockMask;
        var y = (localIndex >> SectionBlockPos.Log) & SectionBlockPos.BlockMask;
        var z = localIndex >> (2 * SectionBlockPos.Log);

        return SectionContentSides.HasSurface(
            GetNeighborContentSides(neighbors, x, y, z),
            GetNeighborContentSides(neighbors, x + 1, y, z),
            GetNeighborContentSides(neighbors, x, y + 1, z),
            GetNeighborContentSides(neighbors, x, y, z + 1)
        );
    }

    /// <summary>
    ///     The content sides of a ready section given relative to the center block's first section, from 0 to <see cref="SectionBlockPos.BlockSize"/> on each axis.
    /// </summary>
    private SectionContentSides GetNeighborContentSides(ReadOnlySpan<SectionBlock> neighbors, int x, int y, int z)
    {
        var block = neighbors[SectionBlockNeighborhoodOps.GetIndex(x >> SectionBlockPos.Log, y >> SectionBlockPos.Log, z >> SectionBlockPos.Log)];

        return blocks.GetSlot(block, SectionBlockPos.GetLocalIndex(x, y, z)).ContentSides;
    }
}
