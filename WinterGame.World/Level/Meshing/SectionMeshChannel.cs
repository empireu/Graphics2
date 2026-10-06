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

internal readonly struct SectionMeshMessage(SectionMeshMessageKind kind, SectionPos position, SectionMeshJob job)
{
    public readonly SectionMeshMessageKind Kind = kind;
    public readonly SectionPos Position = position;

    /// <summary>
    ///     The job a <see cref="SectionMeshMessageKind.Started"/> message hands over. A retire carries none.
    /// </summary>
    public readonly SectionMeshJob Job = job;
}

/// <summary>
///     The jobs and retires the game thread's <see cref="SectionMeshPublisher"/> sends to the render thread's <see cref="SectionMeshReceiver"/>, in order, under one lock.
///     The lock is also what hands each job over, so the render thread can adopt it.
/// </summary>
public sealed class SectionMeshChannel // TODO implement generic
{
    private readonly Lock _lock = new();
    private readonly List<SectionMeshMessage> _messages = [];

    internal void Post(List<SectionMeshMessage> messages)
    {
        lock (_lock)
        {
            _messages.AddRange(messages);
        }
    }

    internal void Take(List<SectionMeshMessage> destination)
    {
        lock (_lock)
        {
            destination.AddRange(_messages);
            _messages.Clear();
        }
    }
}
