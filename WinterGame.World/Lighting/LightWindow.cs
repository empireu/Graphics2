using System.Diagnostics;
using System.Runtime.CompilerServices;
using WinterGame.World.Storage;

namespace WinterGame.World.Lighting;

/// <summary>
///     The light engine's cells, one per brick, over a window of sections around a center.
///     A section sits in the slot at its coordinates modulo the window size. Moving the window moves no cell, and a section that stays inside keeps its slot.
///     A cell's index is its slot times <see cref="CellsPerSection"/> plus its brick index in the section.
///     The queues address a cell by its brick coordinates relative to <see cref="Min"/>, packed by <see cref="Pack"/>.
///     A cell of an empty slot has no air and no light. Every array is allocated at construction.
/// </summary>
internal sealed class LightWindow
{
    /// <summary>
    ///     <see cref="CellsPerSection"/> as a power of two.
    /// </summary>
    public const int CellLog = 3 * Section.BricksPerAxisLog;

    public const int CellsPerSection = 1 << CellLog;

    /// <summary>
    ///     The most sections along an axis. A packed coordinate has eight bits.
    /// </summary>
    public const int MaxSize = 256 >> Section.BricksPerAxisLog;

    public readonly int HorizontalRadius;
    public readonly int VerticalRadius;

    /// <summary>
    ///     Sections along each axis.
    /// </summary>
    public readonly int SizeX;

    public readonly int SizeY;
    public readonly int SizeZ;

    /// <summary>
    ///     Cells along each axis.
    /// </summary>
    public readonly int BricksX;

    public readonly int BricksY;
    public readonly int BricksZ;

    public readonly int SlotCount;

    /// <summary>
    ///     Each cell's air mask (<see cref="BrickAir"/>).
    /// </summary>
    public readonly ulong[] Air;

    /// <summary>
    ///     Each cell's light word (<see cref="LightWord"/>).
    /// </summary>
    public readonly ushort[] Light;

    /// <summary>
    ///     One bit per cell, set when the cell's air is split into several components (<see cref="IsSplit"/>).
    /// </summary>
    public readonly ulong[] Split;

    /// <summary>
    ///     The slot terms of each section coordinate relative to <see cref="Min"/>, one table per axis. A slot is the sum of one entry from each.
    /// </summary>
    public readonly int[] SlotX;

    public readonly int[] SlotY;
    public readonly int[] SlotZ;

    private readonly bool[] _present;
    private readonly SectionPos[] _sections;

    /// <summary>
    ///     The stamp of the engine update that last built each slot.
    /// </summary>
    private readonly int[] _builtStamps;

    public LightWindow(int horizontalRadius, int verticalRadius)
    {
        HorizontalRadius = horizontalRadius;
        VerticalRadius = verticalRadius;
        SizeX = 2 * horizontalRadius + 1;
        SizeY = 2 * verticalRadius + 1;
        SizeZ = SizeX;

        ArgumentOutOfRangeException.ThrowIfGreaterThan(SizeX, MaxSize, nameof(horizontalRadius));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(SizeY, MaxSize, nameof(verticalRadius));

        BricksX = SizeX << Section.BricksPerAxisLog;
        BricksY = SizeY << Section.BricksPerAxisLog;
        BricksZ = SizeZ << Section.BricksPerAxisLog;
        SlotCount = SizeX * SizeY * SizeZ;
        Air = new ulong[SlotCount * CellsPerSection];
        Light = new ushort[SlotCount * CellsPerSection];
        Split = new ulong[SlotCount * CellsPerSection / 64];
        SlotX = new int[SizeX];
        SlotY = new int[SizeY];
        SlotZ = new int[SizeZ];
        _present = new bool[SlotCount];
        _sections = new SectionPos[SlotCount];
        _builtStamps = new int[SlotCount];
    }

    /// <summary>
    ///     The window's lowest section on every axis.
    /// </summary>
    public SectionPos Min { get; private set; }

    public SectionPos Center { get; private set; }

    public bool IsPlaced { get; private set; }

    /// <summary>
    ///     Packs brick coordinates relative to <see cref="Min"/> into a queue entry, eight bits per axis with X lowest.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint Pack(int relativeX, int relativeY, int relativeZ)
    {
        return (uint)(relativeX | (relativeY << 8) | (relativeZ << 16));
    }

    /// <summary>
    ///     Centers the window on <paramref name="center"/>. The caller drops the sections that left it.
    /// </summary>
    public void Place(SectionPos center)
    {
        Center = center;
        Min = new SectionPos(center.X - HorizontalRadius, center.Y - VerticalRadius, center.Z - HorizontalRadius);
        IsPlaced = true;

        for (var index = 0; index < SizeX; index++)
        {
            SlotX[index] = Modulo(Min.X + index, SizeX);
        }

        for (var index = 0; index < SizeY; index++)
        {
            SlotY[index] = Modulo(Min.Y + index, SizeY) * SizeX;
        }

        for (var index = 0; index < SizeZ; index++)
        {
            SlotZ[index] = Modulo(Min.Z + index, SizeZ) * SizeX * SizeY;
        }
    }

    public bool Contains(SectionPos position)
    {
        return IsPlaced &&
               (uint)(position.X - Min.X) < (uint)SizeX &&
               (uint)(position.Y - Min.Y) < (uint)SizeY &&
               (uint)(position.Z - Min.Z) < (uint)SizeZ;
    }

    /// <summary>
    ///     The slot of a section inside the window.
    /// </summary>
    public int GetSlot(SectionPos position)
    {
        Debug.Assert(Contains(position), $"Section ({position.X}, {position.Y}, {position.Z}) is outside the window.");

        return SlotX[position.X - Min.X] + SlotY[position.Y - Min.Y] + SlotZ[position.Z - Min.Z];
    }

    /// <summary>
    ///     The index of the cell at brick coordinates relative to <see cref="Min"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int GetCell(int relativeX, int relativeY, int relativeZ)
    {
        var slot = SlotX[relativeX >> Section.BricksPerAxisLog] + SlotY[relativeY >> Section.BricksPerAxisLog] + SlotZ[relativeZ >> Section.BricksPerAxisLog];

        return (slot << CellLog) |
               (relativeX & Section.BricksPerAxisMask) |
               ((relativeY & Section.BricksPerAxisMask) << Section.BricksPerAxisLog) |
               ((relativeZ & Section.BricksPerAxisMask) << (2 * Section.BricksPerAxisLog));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsSplit(int cell)
    {
        return ((Split[cell >> 6] >> (cell & 63)) & 1) != 0;
    }

    public bool IsPresent(int slot)
    {
        return _present[slot];
    }

    public SectionPos GetSection(int slot)
    {
        return _sections[slot];
    }

    /// <summary>
    ///     Whether the slot was built in the engine update with this stamp.
    /// </summary>
    public bool WasBuiltIn(int slot, int stamp)
    {
        return _builtStamps[slot] == stamp;
    }

    /// <summary>
    ///     Fills a slot's air and split bits from the section's content bricks and marks the slot as holding the section.
    ///     The slot's light is not touched.
    /// </summary>
    public void Build(int slot, SectionPos position, Section section, int stamp)
    {
        _present[slot] = true;
        _sections[slot] = position;
        _builtStamps[slot] = stamp;

        var air = Air.AsSpan(slot << CellLog, CellsPerSection);
        var split = Split.AsSpan(slot * (CellsPerSection / 64), CellsPerSection / 64);

        split.Clear();

        // A shared section holds one content value in every brick:
        if (section.Type == SectionType.Shared)
        {
            air.Fill(section.GetUniformValue(VoxelChannel.Content, 0) > WorldConstants.ContentIsoLevel ? 0 : BrickAir.Full);
            return;
        }

        // The cell order is the section's brick order:
        for (var brickIndex = 0; brickIndex < Section.Bricks; brickIndex++)
        {
            var brickAir = ~section.GetSolidMask(brickIndex);

            air[brickIndex] = brickAir;

            if (brickAir != BrickAir.Full && !BrickAir.IsOneComponent(brickAir))
            {
                split[brickIndex >> 6] |= 1UL << (brickIndex & 63);
            }
        }
    }

    /// <summary>
    ///     Empties a slot and clears its cells' air, light and split bits.
    /// </summary>
    public void Drop(int slot)
    {
        _present[slot] = false;
        Air.AsSpan(slot << CellLog, CellsPerSection).Clear();
        Light.AsSpan(slot << CellLog, CellsPerSection).Clear();
        Split.AsSpan(slot * (CellsPerSection / 64), CellsPerSection / 64).Clear();
    }

    private static int Modulo(int value, int size)
    {
        var remainder = value % size;

        return remainder < 0 ? remainder + size : remainder;
    }
}
