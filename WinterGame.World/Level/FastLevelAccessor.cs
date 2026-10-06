using System.Diagnostics;
using Graphics2.Mathematics;
using WinterGame.Utilities;
using WinterGame.World.Storage;

namespace WinterGame.World.Level;

/// <summary>
///     Reads a <see cref="WorldLevel"/> while caching the last block of sections it looked up, so reads that stay inside one block skip the lookup.
///     It is only valid while the level adds and removes no block.
/// </summary>
public ref struct FastLevelAccessor
{
    private readonly SectionTable _table;

    /// <summary>
    ///     The level's block version when the accessor was made, which a Debug build checks on every lookup.
    /// </summary>
    private readonly int _blockVersion;

    private ulong _blockKey;
    private bool _hasBlockKey;
    private bool _blockFound;
    private SectionBlock _block;

    public FastLevelAccessor(WorldLevel level)
    {
        WgAssert.DebugIsOnGameThread();
        ArgumentNullException.ThrowIfNull(level);

        _table = level.Table;
        _blockVersion = _table.Blocks.Version;
    }

    public SectionStatus GetStatus(SectionPos position)
    {
        return TryGetBlock(position, out var block) ? _table.GetStatus(block, SectionBlockPos.GetLocalIndex(position)) : SectionStatus.None;
    }

    public bool TryGetSection(SectionPos position, out Section section)
    {
        if (TryGetBlock(position, out var block))
        {
            return _table.TryGetSection(block, position, out section);
        }

        section = default;
        return false;
    }

    /// <summary>
    ///     Reads one voxel out of its loaded section. A section that is not loaded reads as air and the result is false.
    /// </summary>
    public bool ReadPoint(Vector3i position, out byte content, out byte material)
    {
        var sectionPosition = new SectionPos(
            SectionPos.VoxelToSectionCoord(position.X),
            SectionPos.VoxelToSectionCoord(position.Y),
            SectionPos.VoxelToSectionCoord(position.Z)
        );

        if (!TryGetSection(sectionPosition, out var section))
        {
            content = VoxelChannel.Content.GetClearValue();
            material = VoxelChannel.Material.GetClearValue();

            return false;
        }

        var local = position & SectionPos.SectionMask;
        var brick = local >> Section.BrickLog;
        var inBrick = local & (Section.BrickSize - 1);
        var brickIndex = Section.GetBrickIndex(brick.X, brick.Y, brick.Z);
        var voxelIndex = Section.GetBrickVoxelIndex(inBrick.X, inBrick.Y, inBrick.Z);

        content = SectionVoxelOps.ReadVoxel(section, VoxelChannel.Content, brickIndex, voxelIndex);
        material = SectionVoxelOps.ReadVoxel(section, VoxelChannel.Material, brickIndex, voxelIndex);

        return true;
    }

    private bool TryGetBlock(SectionPos position, out SectionBlock block)
    {
        DebugValidateBlockVersion();

        var blockKey = SectionBlockPos.FromSection(position).GetKey();

        if (!_hasBlockKey || blockKey != _blockKey)
        {
            _blockKey = blockKey;
            _hasBlockKey = true;
            _blockFound = _table.Blocks.TryGet(blockKey, out _block);
        }

        block = _block;
        return _blockFound;
    }

    [Conditional("DEBUG")]
    private readonly void DebugValidateBlockVersion()
    {
        Debug.Assert(_table.Blocks.Version == _blockVersion, "The level added or removed a block of sections while an accessor was reading it.");
    }
}
