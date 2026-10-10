using WinterGame.Jobs;
using WinterGame.World.Storage;

namespace WinterGame.World.Level.Meshing;

/// <summary>
///     One section's mesh job, handed from the game thread to the render thread together with the slot it meshes into.
/// </summary>
internal readonly struct SectionMeshJob(SectionMeshExecutor executor, int slot, Job job)
{
    public readonly SectionMeshExecutor Executor = executor;
    public readonly int Slot = slot;
    public readonly Job Job = job;
}

internal enum SectionMeshMessageKind : byte
{
    /// <summary>
    ///     A job was published for the section, and its result replaces the section's mesh.
    /// </summary>
    Started,
    /// <summary>
    ///     The section was destroyed, so neither its mesh nor any job in flight for it is wanted.
    /// </summary>
    Retire
}

/// <summary>
///     A job or a retire that the game thread's <see cref="SectionMeshPublisher"/> sends to the render thread's <see cref="SectionMeshReceiver"/>.
///     It is public so the game can create the channel that carries it. Only the level reads or creates one.
/// </summary>
public readonly struct SectionMeshMessage
{
    internal readonly SectionMeshMessageKind Kind;
    internal readonly SectionPos Position;

    /// <summary>
    ///     The job a <see cref="SectionMeshMessageKind.Started"/> message hands over. A retire carries none.
    /// </summary>
    internal readonly SectionMeshJob Job;

    internal SectionMeshMessage(SectionMeshMessageKind kind, SectionPos position, SectionMeshJob job)
    {
        Kind = kind;
        Position = position;
        Job = job;
    }
}
