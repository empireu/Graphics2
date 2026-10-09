using System.Runtime.InteropServices;
using WinterGame.Utilities;
using WinterGame.World.Level;
using WinterGame.World.Storage;

namespace WinterGame.World.Lighting;

/// <summary>
///     Computes sky light, a level from 0 to <see cref="MaxLevel"/> per brick, over a window of sections around a center.
///     A brick at or above its column's sky floor gets <see cref="MaxLevel"/>, and light spreads from there one level less per brick.
///     <see cref="SkyColumns"/> keeps the floors, <see cref="LightWindow"/> keeps the cells, and <see cref="LightPropagator"/> spreads and takes back the light.
///     The window holds a section when the section is ready and <see cref="SkyColumns.IsSkyKnown"/> is true for it.
///     Brick coordinates are world voxel coordinates shifted right by <see cref="Section.BrickLog"/>.
/// </summary>
public sealed class LightEngine
{
    /// <summary>
    ///     The level of a sky cell. Spreading lowers the level, so only seeding sets it.
    /// </summary>
    public const int MaxLevel = 8;

    internal readonly LightWindow Window;
    internal readonly SkyColumns Sky;

    private readonly WorldLevel _level;
    private readonly LightPropagator _propagator;
    private readonly List<SectionChange> _changes = [];

    /// <summary>
    ///     The sections built in this update. They are seeded after every section is built.
    /// </summary>
    private readonly List<SectionPos> _built = [];

    /// <summary>
    ///     The number of the current update, stamped on each slot it builds.
    /// </summary>
    private int _stamp;

    /// <summary>
    ///     Creates an engine whose window reaches <paramref name="horizontalRadius"/> sections from its center horizontally and <paramref name="verticalRadius"/> vertically.
    ///     The level must record its changes. All of the window's memory is allocated here.
    /// </summary>
    public LightEngine(WorldLevel level, int horizontalRadius, int verticalRadius)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentOutOfRangeException.ThrowIfNegative(horizontalRadius);
        ArgumentOutOfRangeException.ThrowIfNegative(verticalRadius);

        if (!level.RecordsChanges)
        {
            throw new ArgumentException("The light engine follows the level's changes, so the level has to record them.", nameof(level));
        }

        _level = level;
        Sky = new SkyColumns(level);
        Window = new LightWindow(horizontalRadius, verticalRadius);
        _propagator = new LightPropagator(Window, Sky);
    }

    /// <summary>
    ///     The number of cells the last update took off its queues.
    /// </summary>
    public int LastVisitCount => _propagator.LastVisitCount;

    /// <summary>
    ///     Takes the level's changes and moves the window to <paramref name="center"/>, then lights what changed until the light settles.
    /// </summary>
    public void Update(SectionPos center)
    {
        WgAssert.DebugIsOnGameThread();

        _stamp++;
        _changes.Clear();
        _built.Clear();
        _level.TakeChanges(_changes);

        var changes = CollectionsMarshal.AsSpan(_changes);

        // The floors move first, so every section is placed and seeded against this update's floors:
        Sky.ApplyChanges(changes);

        if (!Window.IsPlaced || Window.Center != center)
        {
            MoveWindow(center);
        }

        foreach (var change in changes)
        {
            if (Window.Contains(change.Position))
            {
                Refresh(change.Position, true);
            }
        }

        RefreshFloorMoves();
        Sky.FloorMoves.Clear();
        SeedBuilt();
        _propagator.Run();
    }

    /// <summary>
    ///     Gets the level of a brick. Returns false outside the window or in a slot that holds no section.
    /// </summary>
    public bool TryGetLight(int brickX, int brickY, int brickZ, out int level)
    {
        WgAssert.DebugIsOnGameThread();

        level = 0;

        if (!Window.IsPlaced)
        {
            return false;
        }

        var relativeX = brickX - (Window.Min.X << Section.BricksPerAxisLog);
        var relativeY = brickY - (Window.Min.Y << Section.BricksPerAxisLog);
        var relativeZ = brickZ - (Window.Min.Z << Section.BricksPerAxisLog);

        if ((uint)relativeX >= (uint)Window.BricksX || (uint)relativeY >= (uint)Window.BricksY || (uint)relativeZ >= (uint)Window.BricksZ)
        {
            return false;
        }

        var cell = Window.GetCell(relativeX, relativeY, relativeZ);

        if (!Window.IsPresent(cell >> LightWindow.CellLog))
        {
            return false;
        }

        level = Window.Light[cell] & LightWord.LevelMask;

        return true;
    }

    /// <summary>
    ///     Gets a brick column's sky floor and whether it is found.
    ///     The floor is the column's lowest brick that sees the sky. It is not found while its walk waits for the section below to become ready.
    ///     Returns false when no section of the column's section column has been ready.
    /// </summary>
    public bool TryGetSkyFloor(int brickX, int brickZ, out int floor, out bool isFound)
    {
        return Sky.TryGetFloor(brickX, brickZ, out floor, out isFound);
    }

    /// <summary>
    ///     Centers the window on <paramref name="center"/>, drops the sections that left it and refreshes the positions that entered it.
    /// </summary>
    private void MoveWindow(SectionPos center)
    {
        var hadWindow = Window.IsPlaced;
        var previousMin = Window.Min;
        var previousCenter = Window.Center;

        Window.Place(center);

        for (var slot = 0; slot < Window.SlotCount; slot++)
        {
            if (Window.IsPresent(slot) && !Window.Contains(Window.GetSection(slot)))
            {
                Window.Drop(slot);
            }
        }

        // Light that crossed a face the window moved away from may have come from a dropped section:
        if (hadWindow)
        {
            RemoveTrailingLight(center.X - previousCenter.X, Window.SizeX, 0);
            RemoveTrailingLight(center.Y - previousCenter.Y, Window.SizeY, 1);
            RemoveTrailingLight(center.Z - previousCenter.Z, Window.SizeZ, 2);
        }

        var min = Window.Min;

        for (var z = 0; z < Window.SizeZ; z++)
        {
            for (var y = 0; y < Window.SizeY; y++)
            {
                for (var x = 0; x < Window.SizeX; x++)
                {
                    var position = new SectionPos(min.X + x, min.Y + y, min.Z + z);

                    var wasInside = hadWindow &&
                                    (uint)(position.X - previousMin.X) < (uint)Window.SizeX &&
                                    (uint)(position.Y - previousMin.Y) < (uint)Window.SizeY &&
                                    (uint)(position.Z - previousMin.Z) < (uint)Window.SizeZ;

                    if (!wasInside)
                    {
                        Refresh(position, false);
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Makes the window hold the section exactly when it is ready and its sky is known.
    ///     A section that arrives is built, and so is a held one when <paramref name="relight"/> is true.
    ///     A held section loses its light before it is built again or dropped. <see cref="SeedBuilt"/> seeds what was built.
    /// </summary>
    private void Refresh(SectionPos position, bool relight)
    {
        var slot = Window.GetSlot(position);

        // A section built in this update already reads this update's store and floors:
        if (Window.WasBuiltIn(slot, _stamp))
        {
            return;
        }

        var isHeld = Window.IsPresent(slot);
        var isWanted = _level.TryGetSection(position, out var section) && Sky.IsSkyKnown(position);

        if (isHeld && !isWanted)
        {
            _propagator.RemoveSectionLight(slot, position);
            Window.Drop(slot);
            return;
        }

        if (!isWanted || (isHeld && !relight))
        {
            return;
        }

        // Its light and the light it spread are removed before it is built again:
        if (isHeld)
        {
            _propagator.RemoveSectionLight(slot, position);
        }

        Window.Build(slot, position, section, _stamp);
        _built.Add(position);
    }

    /// <summary>
    ///     Refreshes the window's sections in every section column with floor changes.
    ///     A run of consecutive changes in one section column is handled once, and the sections between its lowest and highest floor are built again.
    /// </summary>
    private void RefreshFloorMoves()
    {
        var moves = CollectionsMarshal.AsSpan(Sky.FloorMoves);
        var min = Window.Min;
        var index = 0;

        while (index < moves.Length)
        {
            var sectionX = moves[index].BrickX >> Section.BricksPerAxisLog;
            var sectionZ = moves[index].BrickZ >> Section.BricksPerAxisLog;
            var low = int.MaxValue;
            var high = int.MinValue;

            for (; index < moves.Length && moves[index].BrickX >> Section.BricksPerAxisLog == sectionX && moves[index].BrickZ >> Section.BricksPerAxisLog == sectionZ; index++)
            {
                low = Math.Min(low, Math.Min(moves[index].PreviousFloor, moves[index].Floor));
                high = Math.Max(high, Math.Max(moves[index].PreviousFloor, moves[index].Floor));
            }

            if ((uint)(sectionX - min.X) >= (uint)Window.SizeX || (uint)(sectionZ - min.Z) >= (uint)Window.SizeZ)
            {
                continue;
            }

            for (var y = min.Y; y < min.Y + Window.SizeY; y++)
            {
                var bottom = y << Section.BricksPerAxisLog;

                Refresh(new SectionPos(sectionX, y, sectionZ), bottom < high && bottom + Section.BricksPerAxis > low);
            }
        }
    }

    /// <summary>
    ///     Seeds the sections built in this update from the top down and pulls in their neighbors' light.
    ///     A split sky cell's lit air follows from the cell above it, so a section is seeded after the one above it.
    /// </summary>
    private void SeedBuilt()
    {
        var built = CollectionsMarshal.AsSpan(_built);

        built.Sort(static (first, second) => second.Y.CompareTo(first.Y));

        foreach (var position in built)
        {
            var slot = Window.GetSlot(position);

            _propagator.SeedSection(slot, position);
            _propagator.PullFromNeighbors(slot, position);
        }
    }

    /// <summary>
    ///     Takes back the light that is not sky on the window's trailing face along one axis. That face is on the side the window moved away from.
    ///     Nothing is taken back when the old and new windows do not overlap.
    /// </summary>
    private void RemoveTrailingLight(int delta, int size, int axis)
    {
        if (delta == 0 || Math.Abs(delta) >= size)
        {
            return;
        }

        _propagator.RemoveFaceLight(axis, delta < 0);
    }
}
