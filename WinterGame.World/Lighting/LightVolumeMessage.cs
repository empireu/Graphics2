using System.Runtime.CompilerServices;
using Graphics2.Mathematics;
using WinterGame.World.Storage;

namespace WinterGame.World.Lighting;

public enum LightVolumeMessageKind : byte
{
    /// <summary>
    ///     The window was placed, with its lowest section at <see cref="LightVolumeMessage.Min"/>.
    /// </summary>
    Placed,

    /// <summary>
    ///     The light or the air of a slot changed, and <see cref="LightVolumeMessage.Levels"/> holds all of it.
    /// </summary>
    Slot
}

/// <summary>
///     One byte per cell of a slot.
/// </summary>
[InlineArray(LightWindow.CellsPerSection)]
public struct LightSlotLevels
{
    private byte _element;
}

/// <summary>
///     What <see cref="LightEngine.TakeVolumeChanges"/> sends to a volume that mirrors the window.
///     The volume has a cell for each cell of the window and keeps each cell at its brick coordinates modulo the volume's size, so moving the window moves no cell.
/// </summary>
public readonly struct LightVolumeMessage
{
    public readonly LightVolumeMessageKind Kind;

    /// <summary>
    ///     The window's lowest section, when it was placed.
    /// </summary>
    public readonly SectionPos Min;

    /// <summary>
    ///     The window's cells along each axis, when it was placed. The volume has this size.
    /// </summary>
    public readonly Vector3i Size;

    /// <summary>
    ///     The slot that changed. A later message for the same slot replaces this one.
    /// </summary>
    public readonly int Slot;

    /// <summary>
    ///     Where the slot's first cell lies in the volume. Its cells fill <see cref="Section.BricksPerAxis"/> cells along each axis from there.
    /// </summary>
    public readonly Vector3i Origin;

    /// <summary>
    ///     One byte for each of the slot's cells, with X fastest, then Y, then Z.
    ///     0 is a cell with no air, and a cell with air holds its sky visibility from 1 for none to 255 for the whole sky.
    /// </summary>
    public readonly LightSlotLevels Levels;

    internal LightVolumeMessage(SectionPos min, Vector3i size)
    {
        Kind = LightVolumeMessageKind.Placed;
        Min = min;
        Size = size;
    }

    internal LightVolumeMessage(int slot, Vector3i origin, ReadOnlySpan<byte> levels)
    {
        Kind = LightVolumeMessageKind.Slot;
        Slot = slot;
        Origin = origin;
        levels.CopyTo(Levels);
    }
}
