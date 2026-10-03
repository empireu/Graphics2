using System.Runtime.InteropServices;
using WinterGame.Utilities;

namespace WinterGame.World;

/// <summary>
///     Where <see cref="SectionMeshReceiver"/> puts finished meshes, in the order the sections changed.
/// </summary>
public interface ISectionMeshSink
{
    /// <summary>
    ///     Gives the section this mesh, replacing any it has. The mesh lives in its slot and is only valid during the call, so it has to be uploaded or copied here.
    /// </summary>
    void SetMesh(SectionMeshResult mesh);

    /// <summary>
    ///     Takes away a mesh an earlier <see cref="SetMesh"/> gave the section.
    /// </summary>
    void RemoveMesh(SectionPos position);
}

/// <summary>
///     The render thread's side of meshing. It takes what the <see cref="SectionMeshPublisher"/> sent, adopts each job, and gives every finished mesh that is still wanted to a sink before freeing its slot for the game thread.
///     A retire cancels the section's jobs in flight and removes its mesh, and a newer job for a section cancels the older ones, so a result never lands after one that replaced it.
///     Render thread only. A job left in flight when the session ends is dropped without a release, since the pool goes away with its scheduler.
/// </summary>
public sealed class SectionMeshReceiver
{
    private struct InFlightMesh
    {
        public SectionPos Position;
        public SectionMeshJob Job;

        /// <summary>
        ///     Set when a retire or a newer job made the result unwanted. The job still has to complete before its slot can be freed.
        /// </summary>
        public bool Cancelled;
    }

    private readonly SectionMeshChannel _channel;
    private readonly List<SectionMeshMessage> _messages = [];
    private readonly List<InFlightMesh> _inFlight = [];

    /// <summary>
    ///     Sections the sink holds a mesh for.
    /// </summary>
    private readonly HashSet<ulong> _meshed = [];

    public SectionMeshReceiver(SectionMeshChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        _channel = channel;
    }

    /// <summary>
    ///     Jobs taken from the channel that have not completed, or completed since the last <see cref="Update"/>.
    /// </summary>
    public int InFlightCount => _inFlight.Count;

    /// <summary>
    ///     Sections the sink holds a mesh for.
    /// </summary>
    public int MeshCount => _meshed.Count;

    /// <summary>
    ///     Applies what the publisher sent since the last call, then gives each finished mesh still wanted to <see cref="sink"/> and frees its slot.
    ///     Rethrows the pool's fault, like the other completion checks.
    /// </summary>
    public void Update(ISectionMeshSink sink)
    {
        WgAssert.DebugIsOnRenderThread();
        ArgumentNullException.ThrowIfNull(sink);

        _channel.Take(_messages);

        for (var index = 0; index < _messages.Count; index++)
        {
            var message = _messages[index];

            Cancel(message.Position);

            if (message.Kind == SectionMeshMessageKind.Started)
            {
                var job = message.Job.Job;

                job.Pool.Adopt(job);

                _inFlight.Add(new InFlightMesh
                {
                    Position = message.Position,
                    Job = message.Job
                });

                continue;
            }

            if (_meshed.Remove(message.Position.GetKey()))
            {
                sink.RemoveMesh(message.Position);
            }
        }

        _messages.Clear();

        for (var index = _inFlight.Count - 1; index >= 0; index--)
        {
            var inFlight = _inFlight[index];
            var job = inFlight.Job.Job;

            if (!job.Pool.TryComplete(job))
            {
                continue;
            }

            if (!inFlight.Cancelled)
            {
                sink.SetMesh(inFlight.Job.Executor.GetResult(inFlight.Job.Slot));
                _meshed.Add(inFlight.Position.GetKey());
            }

            job.Pool.Release(job);
            inFlight.Job.Executor.Free(inFlight.Job.Slot);

            _inFlight[index] = _inFlight[^1];
            _inFlight.RemoveAt(_inFlight.Count - 1);
        }
    }

    private void Cancel(SectionPos position)
    {
        foreach (ref var inFlight in CollectionsMarshal.AsSpan(_inFlight))
        {
            if (inFlight.Position == position)
            {
                inFlight.Cancelled = true;
            }
        }
    }
}
