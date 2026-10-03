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
    Retire
}

/// <summary>
///     One change to which sections have a mesh, as <see cref="Level.TakeMeshChanges"/> reports it.
/// </summary>
public readonly struct SectionMeshChange(SectionPos position, SectionMeshChangeKind kind)
{
    public readonly SectionPos Position = position;
    public readonly SectionMeshChangeKind Kind = kind;
}
