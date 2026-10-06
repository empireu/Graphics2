using WinterGame.Jobs;
using WinterGame.Utilities;
using WinterGame.World.Storage;

namespace WinterGame.World.Level.Meshing;

/// <summary>
///     The game thread's side of meshing. It takes the level's mesh changes, keeps the sections that want a mesh nearest the focus first behind the ones an edit wants in the order of the edits, and for each free slot reads the section's window out of the level, publishes its job and sends the job to the render thread through a <see cref="SectionMeshChannel"/>.
///     A slot comes back only once the render thread has taken its result, so a render thread that falls behind stops the publishing instead of piling results up.
///     Game thread only, and created before the scheduler starts, since it registers its executor.
/// </summary>
public sealed class SectionMeshPublisher
{
    private readonly struct RefusedPublish(Job job, int slot)
    {
        public readonly Job Job = job;
        public readonly int Slot = slot;
    }

    private readonly JobScheduler _scheduler;
    private readonly SectionMeshExecutor _executor;
    private readonly int _providerId;
    private readonly SectionMeshChannel _channel;

    private readonly List<SectionMeshChange> _changes = [];

    /// <summary>
    ///     Sections that want a mesh and have no job yet. The queue holds them by squared distance to the focus, and an entry this set no longer holds went stale.
    /// </summary>
    private readonly HashSet<ulong> _wanted = [];

    private readonly PriorityQueue<ulong, long> _queue = new();

    /// <summary>
    ///     Sections an edit wants meshed, published before <see cref="_queue"/>. A section may sit in both, and whichever is taken first publishes it.
    /// </summary>
    private readonly Queue<ulong> _edits = new();

    /// <summary>
    ///     Sections a job was sent for since their last retire, the only ones the render thread has to be told to retire.
    /// </summary>
    private readonly HashSet<ulong> _started = [];

    /// <summary>
    ///     Jobs the scheduler refused, published again first on the next update. The render thread already holds them, and a job that never ran cannot complete, so they are never dropped.
    /// </summary>
    private readonly List<RefusedPublish> _refused = [];

    private readonly List<SectionMeshMessage> _outgoing = [];

    private SectionPos _focus;

    public SectionMeshPublisher(JobScheduler scheduler, SectionMeshingDescription description, SectionMeshChannel channel)
    {
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(channel);

        if (scheduler.IsStarted)
        {
            throw new InvalidOperationException("The publisher registers its meshing executor, so it has to be created before the scheduler starts.");
        }

        _scheduler = scheduler;
        _executor = new SectionMeshExecutor(description, scheduler.ThreadCount);
        _providerId = scheduler.RegisterExecutor(_executor);
        _channel = channel;
    }

    /// <summary>
    ///     Sections that want a mesh and are waiting for a slot.
    /// </summary>
    public int QueuedCount => _wanted.Count;

    /// <summary>
    ///     Slots neither in flight nor holding a result the render thread has yet to take.
    /// </summary>
    public int FreeSlotCount => _executor.FreeSlotCount;

    internal SectionMeshExecutor Executor => _executor;

    /// <summary>
    ///     Takes the level's mesh changes, then publishes the wanted sections nearest <see cref="focus"/> into the free slots and sends their jobs and the retires to the render thread.
    /// </summary>
    public void Update(WorldLevel level, SectionPos focus)
    {
        WgAssert.DebugIsOnGameThread();
        ArgumentNullException.ThrowIfNull(level);

        // The level picks the sections an edit meshes again by its own window, so it has to be the window read here:
        if (level.MeshWindow.Min != _executor.WindowOffset || level.MeshWindow.Max != _executor.WindowOffset + _executor.WindowSize - 1)
        {
            throw new InvalidOperationException("The level was built with another meshing description than the publisher, so an edit would not mesh again every section whose window it changed.");
        }

        TakeChanges(level, focus);

        // A publish refused again holds back new ones, but the retires still go out:
        if (RepublishRefused())
        {
            PublishNearest(level);
        }

        if (_outgoing.Count > 0)
        {
            _channel.Post(_outgoing);
            _outgoing.Clear();
        }
    }

    private void TakeChanges(WorldLevel level, SectionPos focus)
    {
        level.TakeMeshChanges(_changes);

        var focusMoved = focus != _focus;

        _focus = focus;

        for (var index = 0; index < _changes.Count; index++)
        {
            var change = _changes[index];
            var key = change.Position.GetKey();

            if (change.Kind == SectionMeshChangeKind.Mesh)
            {
                if (_wanted.Add(key) && !focusMoved)
                {
                    _queue.Enqueue(key, GetPriority(change.Position));
                }

                continue;
            }

            if (change.Kind == SectionMeshChangeKind.Edit)
            {
                _wanted.Add(key);
                _edits.Enqueue(key);
                continue;
            }

            _wanted.Remove(key);

            if (_started.Remove(key))
            {
                _outgoing.Add(new SectionMeshMessage(SectionMeshMessageKind.Retire, change.Position, default));
            }
        }

        _changes.Clear();

        if (!focusMoved)
        {
            return;
        }

        _queue.Clear();

        foreach (var key in _wanted)
        {
            _queue.Enqueue(key, GetPriority(new SectionPos(key)));
        }
    }

    /// <summary>
    ///     Publishes the jobs the scheduler refused on an earlier update. False means it refused again, and nothing new should be published this update.
    /// </summary>
    private bool RepublishRefused()
    {
        while (_refused.Count > 0)
        {
            var refused = _refused[^1];

            if (!_scheduler.Publish(refused.Job, _providerId, refused.Slot))
            {
                return false;
            }

            _refused.RemoveAt(_refused.Count - 1);
        }

        return true;
    }

    /// <summary>
    ///     Publishes what the edits want, then the wanted sections nearest the focus, until the slots run out.
    /// </summary>
    private void PublishNearest(WorldLevel level)
    {
        while (_executor.FreeSlotCount > 0 && (_edits.TryDequeue(out var key) || _queue.TryDequeue(out key, out _)))
        {
            // A section retired since it was queued, or queued twice, went stale:
            if (!_wanted.Remove(key))
            {
                continue;
            }

            var position = new SectionPos(key);
            var slot = _executor.Allocate(position);
            var min = _executor.GetWindowMin(position);

            // A neighbor unloaded since the section was reported, so the window holds air there, and the level reports the section again once its neighborhood is back:
            if (!level.ReadRange(min, min + _executor.WindowSize - 1, _executor.GetContentWindow(slot), _executor.GetMaterialWindow(slot)))
            {
                _executor.Free(slot);
                level.ReportMeshAgain(position);
                continue;
            }

            var job = _scheduler.Pool.CreateJob();
            var published = _scheduler.Publish(job, _providerId, slot);

            _started.Add(key);
            _outgoing.Add(new SectionMeshMessage(SectionMeshMessageKind.Started, position, new SectionMeshJob(_executor, slot, job)));

            if (!published)
            {
                _refused.Add(new RefusedPublish(job, slot));
                return;
            }
        }
    }

    private long GetPriority(SectionPos position)
    {
        long deltaX = position.X - _focus.X;
        long deltaY = position.Y - _focus.Y;
        long deltaZ = position.Z - _focus.Z;

        return deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ;
    }
}
