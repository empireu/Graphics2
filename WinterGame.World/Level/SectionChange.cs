using WinterGame.World.Storage;

namespace WinterGame.World.Level;

public enum SectionChangeKind : byte
{
    /// <summary>
    ///     The section became ready, generated, written as sky or loaded synchronously.
    /// </summary>
    Ready,
    /// <summary>
    ///     A write changed some of the section's voxels.
    /// </summary>
    Written,
    /// <summary>
    ///     The section was unloaded, and its voxels are gone.
    /// </summary>
    Destroyed
}

/// <summary>
///     One change to the store, as <see cref="WorldLevel.TakeChanges"/> reports it.
///     It names the section and carries none of its data, so a consumer reads the section again and skips it when it is no longer ready.
/// </summary>
public readonly struct SectionChange(SectionPos position, SectionChangeKind kind)
{
    public readonly SectionPos Position = position;
    public readonly SectionChangeKind Kind = kind;
}
