namespace WinterGame.World;

public enum SectionMeshChangeKind : byte
{
    /// <summary>
    ///     The section needs a mesh, which replaces any mesh it has.
    /// </summary>
    Mesh,
    /// <summary>
    ///     The section was destroyed, so its mesh is no longer drawn.
    /// </summary>
    Retire,
    /// <summary>
    ///     A write changed what the section's mesh reads, so it needs a mesh ahead of the ones streaming wants, which replaces any it has and may be empty.
    /// </summary>
    Edit
}

/// <summary>
///     One change to which sections have a mesh, as <see cref="Level.TakeMeshChanges"/> reports it.
/// </summary>
public readonly struct SectionMeshChange(SectionPos position, SectionMeshChangeKind kind)
{
    public readonly SectionPos Position = position;
    public readonly SectionMeshChangeKind Kind = kind;
}
