using System.Numerics;
using System.Runtime.CompilerServices;
using WinterGame.World.Storage;

namespace WinterGame.World.Lighting;

/// <summary>
///     Spreads light over a <see cref="LightWindow"/> from an add queue and takes it back from a removal queue.
///     A cell gives its level minus one to each neighbor whose air its lit air touches across the shared face.
///     A removal darkens every dimmer neighbor without looking at the air, and queues every brighter neighbor as an add so light from another source fills back in.
/// </summary>
internal sealed class LightPropagator(LightWindow window, SkyColumns sky)
{
    private const int InitialQueueCapacity = 1 << 14;

    /// <summary>
    ///     The packed coordinates of a removal entry.
    /// </summary>
    private const uint CoordinateMask = 0xFF_FFFF;

    /// <summary>
    ///     A removal entry keeps the level the cell had above its packed coordinates.
    /// </summary>
    private const int RemovedLevelShift = 24;

    private readonly CellQueue _adds = new(InitialQueueCapacity);
    private readonly CellQueue _removals = new(InitialQueueCapacity);

    /// <summary>
    ///     Cells the last <see cref="Run"/> took off its queues.
    /// </summary>
    public int LastVisitCount { get; private set; }

    /// <summary>
    ///     Sets the sky cells of a built section from its column's floors.
    ///     Only the sky cells below their column's push height are queued (<see cref="GetPushBelow(int,int,ReadOnlySpan{int},Span{int})"/>).
    /// </summary>
    public void SeedSection(int slot, SectionPos position)
    {
        var floors = sky.GetFloors(position.X, position.Z);

        if (floors.IsEmpty)
        {
            return;
        }

        Span<int> pushBelow = stackalloc int[SkyBlockers.ColumnsPerSection];

        GetPushBelow(position.X, position.Z, floors, pushBelow);
        window.MarkChanged(slot);

        var air = window.Air;
        var light = window.Light;
        var bottom = position.Y << Section.BricksPerAxisLog;
        var originX = (position.X - window.Min.X) << Section.BricksPerAxisLog;
        var originY = (position.Y - window.Min.Y) << Section.BricksPerAxisLog;
        var originZ = (position.Z - window.Min.Z) << Section.BricksPerAxisLog;
        var firstCell = slot << LightWindow.CellLog;

        for (var column = 0; column < SkyBlockers.ColumnsPerSection; column++)
        {
            var x = column & Section.BricksPerAxisMask;
            var z = column >> Section.BricksPerAxisLog;
            var first = Math.Max(floors[column] - bottom, 0);

            if (first >= Section.BricksPerAxis)
            {
                continue;
            }

            // A split cell's lit air follows from the lit air of the cell above it, so the column is seeded top down:
            var litAbove = GetSkyLitAir(originX + x, originY + Section.BricksPerAxis, originZ + z);

            for (var y = Section.BricksPerAxis - 1; y >= first; y--)
            {
                var cell = firstCell | x | (y << Section.BricksPerAxisLog) | (z << (2 * Section.BricksPerAxisLog));
                var cellAir = air[cell];

                if (cellAir == 0)
                {
                    litAbove = 0;
                    continue;
                }

                var word = LightWord.Sky(cellAir, window.IsSplit(cell), litAbove);

                light[cell] = word;
                litAbove = LightWord.GetLitAir(word, cellAir);

                if (bottom + y < pushBelow[column])
                {
                    _adds.Enqueue(LightWindow.Pack(originX + x, originY + y, originZ + z));
                }
            }
        }
    }

    /// <summary>
    ///     Queues the cells on the faces of the six neighboring sections that are bright enough to raise the cell of the built section they face.
    /// </summary>
    public void PullFromNeighbors(int slot, SectionPos position)
    {
        const int strideY = Section.BricksPerAxis;
        const int strideZ = Section.BricksPerAxis * Section.BricksPerAxis;
        const int last = Section.BricksPerAxisMask;

        var relativeX = position.X - window.Min.X;
        var relativeY = position.Y - window.Min.Y;
        var relativeZ = position.Z - window.Min.Z;
        var origin = LightWindow.Pack(relativeX << Section.BricksPerAxisLog, relativeY << Section.BricksPerAxisLog, relativeZ << Section.BricksPerAxisLog);

        if (relativeX > 0)
        {
            PullFace(slot, window.GetSlot(position with { X = position.X - 1 }), 0, last, strideY, strideZ, origin - 1, 0x100, 0x1_0000);
        }

        if (relativeX + 1 < window.SizeX)
        {
            PullFace(slot, window.GetSlot(position with { X = position.X + 1 }), last, 0, strideY, strideZ, origin + Section.BricksPerAxis, 0x100, 0x1_0000);
        }

        if (relativeY > 0)
        {
            PullFace(slot, window.GetSlot(position with { Y = position.Y - 1 }), 0, last * strideY, 1, strideZ, origin - 0x100, 1, 0x1_0000);
        }

        if (relativeY + 1 < window.SizeY)
        {
            PullFace(slot, window.GetSlot(position with { Y = position.Y + 1 }), last * strideY, 0, 1, strideZ, origin + (Section.BricksPerAxis << 8), 1, 0x1_0000);
        }

        if (relativeZ > 0)
        {
            PullFace(slot, window.GetSlot(position with { Z = position.Z - 1 }), 0, last * strideZ, 1, strideY, origin - 0x1_0000, 1, 0x100);
        }

        if (relativeZ + 1 < window.SizeZ)
        {
            PullFace(slot, window.GetSlot(position with { Z = position.Z + 1 }), last * strideZ, 0, 1, strideY, origin + (Section.BricksPerAxis << 16), 1, 0x100);
        }
    }

    /// <summary>
    ///     Darkens every lit cell of a section and queues it for removal.
    /// </summary>
    public void RemoveSectionLight(int slot, SectionPos position)
    {
        var light = window.Light;
        var originX = (position.X - window.Min.X) << Section.BricksPerAxisLog;
        var originY = (position.Y - window.Min.Y) << Section.BricksPerAxisLog;
        var originZ = (position.Z - window.Min.Z) << Section.BricksPerAxisLog;
        var firstCell = slot << LightWindow.CellLog;

        window.MarkChanged(slot);

        for (var local = 0; local < LightWindow.CellsPerSection; local++)
        {
            var level = light[firstCell | local] & LightWord.LevelMask;

            if (level == 0)
            {
                continue;
            }

            light[firstCell | local] = 0;

            var packed = LightWindow.Pack(
                originX + (local & Section.BricksPerAxisMask),
                originY + ((local >> Section.BricksPerAxisLog) & Section.BricksPerAxisMask),
                originZ + (local >> (2 * Section.BricksPerAxisLog))
            );

            _removals.Enqueue(packed | ((uint)level << RemovedLevelShift));
        }
    }

    /// <summary>
    ///     Darkens the lit cells on one outer face of the window that are not sky cells, and queues them for removal.
    /// </summary>
    public void RemoveFaceLight(int axis, bool highSide)
    {
        var light = window.Light;
        var (firstSize, secondSize, faceSize) = axis switch
        {
            0 => (window.BricksY, window.BricksZ, window.BricksX),
            1 => (window.BricksX, window.BricksZ, window.BricksY),
            _ => (window.BricksX, window.BricksY, window.BricksZ)
        };
        var face = highSide ? faceSize - 1 : 0;

        for (var second = 0; second < secondSize; second++)
        {
            for (var first = 0; first < firstSize; first++)
            {
                var (relativeX, relativeY, relativeZ) = axis switch
                {
                    0 => (face, first, second),
                    1 => (first, face, second),
                    _ => (first, second, face)
                };
                var cell = window.GetCell(relativeX, relativeY, relativeZ);
                var level = light[cell] & LightWord.LevelMask;

                if (level == 0 || level == LightEngine.MaxLevel)
                {
                    continue;
                }

                light[cell] = 0;
                window.MarkChanged(cell >> LightWindow.CellLog);
                _removals.Enqueue(LightWindow.Pack(relativeX, relativeY, relativeZ) | ((uint)level << RemovedLevelShift));
            }
        }
    }

    /// <summary>
    ///     Runs every queued removal, then spreads the queued light until both queues are empty.
    /// </summary>
    public void Run()
    {
        LastVisitCount = RunRemovals() + RunAdds();
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int RunRemovals()
    {
        var light = window.Light;
        var bricksX = window.BricksX;
        var bricksY = window.BricksY;
        var bricksZ = window.BricksZ;
        var visits = 0;

        while (_removals.TryDequeue(out var entry))
        {
            var packed = entry & CoordinateMask;
            var removedLevel = (int)(entry >> RemovedLevelShift);
            var relativeX = (int)(packed & 0xFF);
            var relativeY = (int)((packed >> 8) & 0xFF);
            var relativeZ = (int)(packed >> 16);
            var cell = window.GetCell(relativeX, relativeY, relativeZ);

            visits++;

            if (relativeX + 1 < bricksX)
            {
                RemoveFrom(light, (relativeX & Section.BricksPerAxisMask) != Section.BricksPerAxisMask ? cell + 1 : window.GetCell(relativeX + 1, relativeY, relativeZ), packed + 1, removedLevel);
            }

            if (relativeX > 0)
            {
                RemoveFrom(light, (relativeX & Section.BricksPerAxisMask) != 0 ? cell - 1 : window.GetCell(relativeX - 1, relativeY, relativeZ), packed - 1, removedLevel);
            }

            if (relativeY + 1 < bricksY)
            {
                RemoveFrom(light, (relativeY & Section.BricksPerAxisMask) != Section.BricksPerAxisMask ? cell + 8 : window.GetCell(relativeX, relativeY + 1, relativeZ), packed + 0x100, removedLevel);
            }

            if (relativeY > 0)
            {
                RemoveFrom(light, (relativeY & Section.BricksPerAxisMask) != 0 ? cell - 8 : window.GetCell(relativeX, relativeY - 1, relativeZ), packed - 0x100, removedLevel);
            }

            if (relativeZ + 1 < bricksZ)
            {
                RemoveFrom(light, (relativeZ & Section.BricksPerAxisMask) != Section.BricksPerAxisMask ? cell + 64 : window.GetCell(relativeX, relativeY, relativeZ + 1), packed + 0x1_0000, removedLevel);
            }

            if (relativeZ > 0)
            {
                RemoveFrom(light, (relativeZ & Section.BricksPerAxisMask) != 0 ? cell - 64 : window.GetCell(relativeX, relativeY, relativeZ - 1), packed - 0x1_0000, removedLevel);
            }
        }

        return visits;
    }

    /// <summary>
    ///     Darkens a neighbor of a removed cell and queues it for removal when it is dimmer than <paramref name="removedLevel"/>.
    ///     A lit neighbor at least as bright is queued as an add instead.
    ///     The air is not checked, so this can darken cells the removed light never reached, and the adds light them again.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RemoveFrom(ushort[] light, int neighbor, uint packed, int removedLevel)
    {
        var level = light[neighbor] & LightWord.LevelMask;

        if (level == 0)
        {
            return;
        }

        if (level < removedLevel)
        {
            light[neighbor] = 0;
            window.MarkChanged(neighbor >> LightWindow.CellLog);
            _removals.Enqueue(packed | ((uint)level << RemovedLevelShift));
            return;
        }

        _adds.Enqueue(packed);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private int RunAdds()
    {
        var air = window.Air;
        var light = window.Light;
        var bricksX = window.BricksX;
        var bricksY = window.BricksY;
        var bricksZ = window.BricksZ;
        var visits = 0;

        while (_adds.TryDequeue(out var packed))
        {
            var relativeX = (int)(packed & 0xFF);
            var relativeY = (int)((packed >> 8) & 0xFF);
            var relativeZ = (int)(packed >> 16);
            var cell = window.GetCell(relativeX, relativeY, relativeZ);
            var word = (int)light[cell];
            var level = word & LightWord.LevelMask;

            visits++;

            if (level <= 1)
            {
                continue;
            }

            var lit = LightWord.GetLitAir(word, air[cell]);
            var next = level - 1;

            // Each footprint is the lit air on one face, moved onto the facing face of the neighbor:
            var footprint = CubeMask.CrossPositiveX(lit);

            if (footprint != 0 && relativeX + 1 < bricksX)
            {
                Offer(air, light, (relativeX & Section.BricksPerAxisMask) != Section.BricksPerAxisMask ? cell + 1 : window.GetCell(relativeX + 1, relativeY, relativeZ), footprint, next, packed + 1);
            }

            footprint = CubeMask.CrossNegativeX(lit);

            if (footprint != 0 && relativeX > 0)
            {
                Offer(air, light, (relativeX & Section.BricksPerAxisMask) != 0 ? cell - 1 : window.GetCell(relativeX - 1, relativeY, relativeZ), footprint, next, packed - 1);
            }

            footprint = CubeMask.CrossPositiveY(lit);

            if (footprint != 0 && relativeY + 1 < bricksY)
            {
                Offer(air, light, (relativeY & Section.BricksPerAxisMask) != Section.BricksPerAxisMask ? cell + 8 : window.GetCell(relativeX, relativeY + 1, relativeZ), footprint, next, packed + 0x100);
            }

            footprint = CubeMask.CrossNegativeY(lit);

            if (footprint != 0 && relativeY > 0)
            {
                Offer(air, light, (relativeY & Section.BricksPerAxisMask) != 0 ? cell - 8 : window.GetCell(relativeX, relativeY - 1, relativeZ), footprint, next, packed - 0x100);
            }

            footprint = CubeMask.CrossPositiveZ(lit);

            if (footprint != 0 && relativeZ + 1 < bricksZ)
            {
                Offer(air, light, (relativeZ & Section.BricksPerAxisMask) != Section.BricksPerAxisMask ? cell + 64 : window.GetCell(relativeX, relativeY, relativeZ + 1), footprint, next, packed + 0x1_0000);
            }

            footprint = CubeMask.CrossNegativeZ(lit);

            if (footprint != 0 && relativeZ > 0)
            {
                Offer(air, light, (relativeZ & Section.BricksPerAxisMask) != 0 ? cell - 64 : window.GetCell(relativeX, relativeY, relativeZ - 1), footprint, next, packed - 0x1_0000);
            }
        }

        return visits;
    }

    /// <summary>
    ///     Raises a neighbor to <paramref name="level"/> and queues it, when it is dimmer and the footprint touches its air.
    ///     A split neighbor's seed voxel becomes the lowest voxel of the air the footprint reaches.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Offer(ulong[] air, ushort[] light, int neighbor, ulong footprint, int level, uint packed)
    {
        var neighborAir = air[neighbor];
        var touching = footprint & neighborAir;

        if (touching == 0 || (light[neighbor] & LightWord.LevelMask) >= level)
        {
            return;
        }

        var word = level;

        if (neighborAir != BrickAir.Full && window.IsSplit(neighbor))
        {
            word |= LightWord.HasSeed | (BitOperations.TrailingZeroCount(CubeMask.Flood(neighborAir, touching)) << LightWord.SeedShift);
        }

        light[neighbor] = (ushort)word;
        window.MarkChanged(neighbor >> LightWindow.CellLog);
        _adds.Enqueue(packed);
    }

    /// <summary>
    ///     The lit air of the cell at the relative coordinates when it is a sky cell.
    ///     Returns full air when the cell is above the window or is not a sky cell.
    /// </summary>
    private ulong GetSkyLitAir(int relativeX, int relativeY, int relativeZ)
    {
        if (relativeY >= window.BricksY)
        {
            return BrickAir.Full;
        }

        var cell = window.GetCell(relativeX, relativeY, relativeZ);
        var word = (int)window.Light[cell];

        return (word & LightWord.LevelMask) == LightEngine.MaxLevel ? LightWord.GetLitAir(word, window.Air[cell]) : BrickAir.Full;
    }

    /// <summary>
    ///     Queues the cells on one face of a neighboring section whose light would raise the cell of the built section they face.
    ///     The face offsets are the face's first cell in each section, and the two strides step across the face in both.
    ///     The packed arguments give the queue entries of the neighbor's cells the same way.
    /// </summary>
    private void PullFace(int slot, int neighborSlot, int faceOffset, int neighborFaceOffset, int firstStride, int secondStride, uint packedFace, uint packedFirstStride, uint packedSecondStride)
    {
        if (!window.IsPresent(neighborSlot))
        {
            return;
        }

        var air = window.Air;
        var light = window.Light;
        var face = (slot << LightWindow.CellLog) + faceOffset;
        var neighborFace = (neighborSlot << LightWindow.CellLog) + neighborFaceOffset;

        for (var second = 0; second < Section.BricksPerAxis; second++)
        {
            for (var first = 0; first < Section.BricksPerAxis; first++)
            {
                var offset = first * firstStride + second * secondStride;
                var level = light[neighborFace + offset] & LightWord.LevelMask;

                if ((light[face + offset] & LightWord.LevelMask) + 1 < level && air[face + offset] != 0)
                {
                    _adds.Enqueue(packedFace + (uint)first * packedFirstStride + (uint)second * packedSecondStride);
                }
            }
        }
    }

    /// <summary>
    ///     Computes the push height of each brick column of a section column.
    ///     A sky cell below its column's push height may border a cell that is not sky, and a sky cell at or above it cannot.
    ///     The push height is the highest of the column's floor plus one and the floors of the four columns beside it.
    /// </summary>
    private void GetPushBelow(int sectionX, int sectionZ, ReadOnlySpan<int> floors, Span<int> pushBelow)
    {
        var west = sky.GetFloors(sectionX - 1, sectionZ);
        var east = sky.GetFloors(sectionX + 1, sectionZ);
        var north = sky.GetFloors(sectionX, sectionZ - 1);
        var south = sky.GetFloors(sectionX, sectionZ + 1);

        for (var column = 0; column < SkyBlockers.ColumnsPerSection; column++)
        {
            pushBelow[column] = GetPushBelow(column, floors, west, east, north, south);
        }
    }

    /// <summary>
    ///     The push height of one brick column. A neighboring section column without a record has an empty span and adds nothing.
    /// </summary>
    private static int GetPushBelow(int column, ReadOnlySpan<int> floors, ReadOnlySpan<int> west, ReadOnlySpan<int> east, ReadOnlySpan<int> north, ReadOnlySpan<int> south)
    {
        const int last = Section.BricksPerAxisMask;
        const int row = Section.BricksPerAxis;

        var x = column & Section.BricksPerAxisMask;
        var z = column >> Section.BricksPerAxisLog;
        var result = floors[column] + 1;

        result = Math.Max(result, x > 0 ? floors[column - 1] : west.IsEmpty ? int.MinValue : west[column + last]);
        result = Math.Max(result, x < last ? floors[column + 1] : east.IsEmpty ? int.MinValue : east[column - last]);
        result = Math.Max(result, z > 0 ? floors[column - row] : north.IsEmpty ? int.MinValue : north[column + last * row]);
        result = Math.Max(result, z < last ? floors[column + row] : south.IsEmpty ? int.MinValue : south[column - last * row]);

        return result;
    }
}
