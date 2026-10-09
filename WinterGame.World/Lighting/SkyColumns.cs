using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using WinterGame.Utilities;
using WinterGame.World.Level;
using WinterGame.World.Storage;

namespace WinterGame.World.Lighting;

/// <summary>
///     A change to one brick column's floor, as listed in <see cref="SkyColumns.FloorMoves"/>.
///     The two floors are equal when only the found bit changed.
/// </summary>
internal readonly struct SkyFloorMove(int brickX, int brickZ, int previousFloor, int floor)
{
    public readonly int BrickX = brickX;
    public readonly int BrickZ = brickZ;
    public readonly int PreviousFloor = previousFloor;
    public readonly int Floor = floor;
}

/// <summary>
///     Keeps a sky floor per brick column, for every section column that has had a ready section.
///     A column's floor is its lowest brick that sees the sky. Every brick at or above it sees the sky, and the brick below it blocks (<see cref="SkyBlockers"/>).
///     A walk finds the floor by going down from the top of <see cref="WorldLevel.LowestSkySectionY"/> one ready section at a time.
///     When the section below is not ready, the walk waits at its top and the floor is not found yet.
///     Each section the level reports as ready or written is checked against the floors. A blocking brick above a floor raises it, and an opened floor lets the walk go on.
///     A record is never freed, so it keeps the floors of a section column whose sections unloaded.
///     Brick coordinates are world voxel coordinates shifted right by <see cref="Section.BrickLog"/>. Game thread only.
/// </summary>
internal sealed class SkyColumns
{
    /// <summary>
    ///     The floor changes since the list was last cleared, in order.
    ///     A section's check and the walk after it add their changes together, so the changes of a section column come in runs.
    /// </summary>
    public readonly List<SkyFloorMove> FloorMoves = [];

    private readonly WorldLevel _level;
    private readonly Dictionary<ulong, SkyColumnRecord> _records = [];
    private readonly SkyColumnAllocator _storage = new();

    /// <summary>
    ///     The floor every column of a new record starts at. It is the top of <see cref="WorldLevel.LowestSkySectionY"/>.
    /// </summary>
    private readonly int _startFloor;

    public SkyColumns(WorldLevel level)
    {
        ArgumentNullException.ThrowIfNull(level);

        _level = level;
        _startFloor = (level.LowestSkySectionY + 1) << Section.BricksPerAxisLog;
    }

    /// <summary>
    ///     Section columns with a record.
    /// </summary>
    public int ColumnCount => _records.Count;

    /// <summary>
    ///     Checks each changed section that is still ready against its column's floors.
    /// </summary>
    public void ApplyChanges(ReadOnlySpan<SectionChange> changes)
    {
        foreach (var change in changes)
        {
            Apply(change.Position);
        }
    }

    /// <summary>
    ///     A section column's floors indexed like <see cref="SkyBlockers.ColumnsPerSection"/>, or an empty span when it has no record.
    /// </summary>
    public Span<int> GetFloors(int sectionX, int sectionZ)
    {
        return _records.TryGetValue(SectionPos.Pack(sectionX, 0, sectionZ), out var record) ? _storage.GetFloors(record) : Span<int>.Empty;
    }

    /// <summary>
    ///     Gets a brick column's floor and whether it is found.
    ///     Returns false when its section column has no record.
    /// </summary>
    public bool TryGetFloor(int brickX, int brickZ, out int floor, out bool isFound)
    {
        WgAssert.DebugIsOnGameThread();

        if (!_records.TryGetValue(SectionPos.Pack(brickX >> Section.BricksPerAxisLog, 0, brickZ >> Section.BricksPerAxisLog), out var record))
        {
            floor = 0;
            isFound = false;

            return false;
        }

        var column = (brickX & Section.BricksPerAxisMask) | ((brickZ & Section.BricksPerAxisMask) << Section.BricksPerAxisLog);

        floor = _storage.GetFloors(record)[column];
        isFound = (_storage.GetHeader(record).FoundColumns & (1UL << column)) != 0;

        return true;
    }

    /// <summary>
    ///     Whether it is known which bricks of the section see the sky.
    ///     It is when every floor of the section's column is found, or waits at or below the section's bottom.
    /// </summary>
    public bool IsSkyKnown(SectionPos position)
    {
        return _records.TryGetValue(SectionPos.Pack(position.X, 0, position.Z), out var record) && GetWaitingFloor(record) <= position.Y << Section.BricksPerAxisLog;
    }

    /// <summary>
    ///     Checks a section against its column's floors when it is ready, creating the column's record first if it has none.
    ///     The walk goes on when a floor changed or the record is new.
    /// </summary>
    private void Apply(SectionPos position)
    {
        if (!_level.TryGetSection(position, out var section))
        {
            return;
        }

        ref var record = ref CollectionsMarshal.GetValueRefOrAddDefault(_records, SectionPos.Pack(position.X, 0, position.Z), out var exists);

        if (!exists)
        {
            record = _storage.Allocate(_startFloor);
        }

        var moved = false;
        var header = _storage.GetHeader(record);
        var bottom = position.Y << Section.BricksPerAxisLog;

        // A section below every floor cannot move one:
        if (header.MinimumFloor <= bottom + Section.BricksPerAxis)
        {
            Span<byte> blockers = stackalloc byte[SkyBlockers.ColumnsPerSection];

            // Without a blocking brick, only a floor inside the section or at its top can move:
            if (SkyBlockers.Scan(section, blockers) || bottom < header.MaximumFloor)
            {
                moved = ApplySection(record, position, blockers);
            }
        }

        if (moved || !exists)
        {
            Walk(record, position.X, position.Z);
        }
    }

    /// <summary>
    ///     Moves every waiting floor down through the ready sections below it, the highest first.
    ///     Each stops at a blocking brick or at a section that is not ready.
    /// </summary>
    private void Walk(SkyColumnRecord record, int sectionX, int sectionZ)
    {
        Span<byte> blockers = stackalloc byte[SkyBlockers.ColumnsPerSection];

        while (true)
        {
            var highest = GetWaitingFloor(record);

            if (highest == int.MinValue)
            {
                return;
            }

            Debug.Assert((highest & Section.BricksPerAxisMask) == 0, $"An open column's floor {highest} is not on a section boundary.");

            var below = new SectionPos(sectionX, (highest >> Section.BricksPerAxisLog) - 1, sectionZ);

            if (!_level.TryGetSection(below, out var section))
            {
                return;
            }

            SkyBlockers.Scan(section, blockers);

            var moved = ApplySection(record, below, blockers);

            Debug.Assert(moved, $"The walk scanned section ({below.X}, {below.Y}, {below.Z}) without moving the floor {highest} that waits on it.");
        }
    }

    /// <summary>
    ///     The highest waiting floor, or <see cref="int.MinValue"/> when every floor is found.
    ///     A waiting floor is always on a section boundary.
    /// </summary>
    private int GetWaitingFloor(SkyColumnRecord record)
    {
        var floors = _storage.GetFloors(record);
        var unfound = ~_storage.GetHeader(record).FoundColumns;
        var highest = int.MinValue;

        while (unfound != 0)
        {
            highest = Math.Max(highest, floors[BitOperations.TrailingZeroCount(unfound)]);
            unfound &= unfound - 1;
        }

        return highest;
    }

    /// <summary>
    ///     Sets the floors the section decides, given its blockers, and returns whether a floor or a found bit changed.
    ///     The section decides a floor inside it or at its top, and a floor below it when the column has a blocking brick in the section.
    ///     Such a floor moves to just above the column's highest blocking brick in the section and is found.
    ///     When the column has no blocking brick there, the floor moves to the section's bottom and waits.
    ///     Every change is added to <see cref="FloorMoves"/>.
    /// </summary>
    private bool ApplySection(SkyColumnRecord record, SectionPos position, ReadOnlySpan<byte> blockers)
    {
        var floors = _storage.GetFloors(record);
        ref var header = ref _storage.GetHeader(record);
        var bottom = position.Y << Section.BricksPerAxisLog;
        var top = bottom + Section.BricksPerAxis;
        var found = header.FoundColumns;
        var minimum = int.MaxValue;
        var maximum = int.MinValue;
        var moved = false;

        for (var column = 0; column < SkyBlockers.ColumnsPerSection; column++)
        {
            var floor = floors[column];
            int blocking = blockers[column];

            if (floor <= top && (floor > bottom || blocking != 0))
            {
                var bit = 1UL << column;
                var newFloor = blocking != 0 ? bottom + 1 + BitOperations.Log2((uint)blocking) : bottom;
                var newFound = blocking != 0 ? found | bit : found & ~bit;

                if (newFloor != floor || newFound != found)
                {
                    FloorMoves.Add(
                        new SkyFloorMove(
                            (position.X << Section.BricksPerAxisLog) | (column & Section.BricksPerAxisMask),
                            (position.Z << Section.BricksPerAxisLog) | (column >> Section.BricksPerAxisLog),
                            floor,
                            newFloor
                        )
                    );
                }

                moved |= newFloor != floor || newFound != found;
                floor = newFloor;
                found = newFound;
                floors[column] = floor;
            }

            minimum = Math.Min(minimum, floor);
            maximum = Math.Max(maximum, floor);
        }

        header.FoundColumns = found;
        header.MinimumFloor = minimum;
        header.MaximumFloor = maximum;

        return moved;
    }
}
