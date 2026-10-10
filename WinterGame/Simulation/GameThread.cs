using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Graphics2.Windowing.Input;
using WinterGame.Jobs;
using WinterGame.Utilities;
using WinterGame.World;
using WinterGame.World.Level.Meshing;
using WinterGame.World.Lighting;

namespace WinterGame.Simulation;

internal sealed class GameThread : IDisposable
{
    private const int WorkerCount = 4;

    private readonly Lock _lock = new();
    private readonly MessageChannel<SectionMeshMessage> _meshChannel;
    private readonly MessageChannel<LightVolumeMessage> _lightChannel;
    private readonly Thread _thread;

    /// <summary>
    ///     The frames since the last tick, written by the render thread and copied out by the game thread under <see cref="_lock"/>.
    /// </summary>
    private readonly TickInput _pendingInput = new();

    private GameSnapshot _snapshot;

    /// <summary>
    ///     When the tick that produced <see cref="_snapshot"/> started, as a <see cref="Stopwatch"/> timestamp.
    /// </summary>
    private long _snapshotTimestamp;

    private volatile bool _stopRequested;
    private volatile bool _paused;
    private ExceptionDispatchInfo? _fault;

    /// <summary>
    ///     Starts the game thread, whose mesh jobs reach the render thread through <paramref name="meshChannel"/> and whose light changes go through <paramref name="lightChannel"/>.
    /// </summary>
    public GameThread(MessageChannel<SectionMeshMessage> meshChannel, MessageChannel<LightVolumeMessage> lightChannel)
    {
        WgAssert.DebugIsOnRenderThread();

        _meshChannel = meshChannel;
        _lightChannel = lightChannel;

        // The simulation picks the spawn, so until the first tick publishes the snapshot is empty and the player is at the origin:
        _snapshot = default;
        _snapshotTimestamp = Stopwatch.GetTimestamp();

        // The thread sleeps for most of each tick, and the priority keeps the scheduler from taking the core during the short spin before a deadline:
        _thread = new Thread(Run)
        {
            Name = "Game",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };

        _thread.Start();
    }

    /// <summary>
    ///     Adds one frame to what the next tick reads, together with the camera's current rotation.
    /// </summary>
    public void SubmitFrame(InputSnapshot input, bool hasFocus, float yaw, float pitch)
    {
        WgAssert.DebugIsOnRenderThread();

        lock (_lock)
        {
            if (hasFocus)
            {
                _pendingInput.AddFrame(input);
            }
            else
            {
                _pendingInput.ReleaseKeys();
            }

            _pendingInput.Yaw = yaw;
            _pendingInput.Pitch = pitch;
        }
    }

    /// <summary>
    ///     While set, the thread skips its ticks, so the simulation stands still and the snapshot stays the last one. The schedule keeps running, so resuming ticks on time.
    /// </summary>
    public void SetPaused(bool paused)
    {
        WgAssert.DebugIsOnRenderThread();

        _paused = paused;
    }

    /// <summary>
    ///     The latest tick's snapshot, and how far <paramref name="timestamp"/> is past the start of that tick as a fraction of a tick clamped to [0, 1], which is how far rendering interpolates.
    ///     Pass the frame's input timestamp, so the position describes the same instant as the rotation.
    /// </summary>
    public GameSnapshot GetSnapshot(long timestamp, out float tickFraction)
    {
        WgAssert.DebugIsOnRenderThread();

        GameSnapshot snapshot;
        long snapshotTimestamp;

        lock (_lock)
        {
            snapshot = _snapshot;
            snapshotTimestamp = _snapshotTimestamp;
        }

        // A tick that started after the timestamp clamps to 0, which is the point the previous tick's interpolation ended on:
        tickFraction = (float)Math.Clamp(Stopwatch.GetElapsedTime(snapshotTimestamp, timestamp).TotalSeconds / GameSimulation.TickSeconds, 0.0, 1.0);

        return snapshot;
    }

    /// <summary>
    ///     Rethrows the exception that stopped the game thread, on the calling thread and with the game thread's stack trace.
    /// </summary>
    public void ThrowIfFaulted()
    {
        Volatile.Read(ref _fault)?.Throw();
    }

    public void Dispose()
    {
        WgAssert.DebugIsOnRenderThread();

        _stopRequested = true;
        _thread.Join();
    }

    private void Run()
    {
        WgAssert.InitGameThread();

        try
        {
            using var waiter = PreciseWaiter.Create();
            using var scheduler = new JobScheduler(new JobSchedulerDescription
            {
                Name = "ServerThreadExecutor",
                ThreadCount = WorkerCount
            });

            // The level and the mesh publisher register their executors before the workers start, and the simulation is disposed before the scheduler so the level can still wait for its jobs:
            using var simulation = new GameSimulation(scheduler, _meshChannel, _lightChannel);
            scheduler.Start();

            var input = new TickInput();
            var anchor = Stopwatch.GetTimestamp();
            var ticksSinceAnchor = 0L;

            while (!_stopRequested)
            {
                waiter.WaitUntil(GetDeadline(anchor, ticksSinceAnchor));

                if (_stopRequested)
                {
                    return;
                }

                // A press made just before the pause would otherwise act when the game resumes:
                if (_paused)
                {
                    lock (_lock)
                    {
                        _pendingInput.ClearPresses();
                    }

                    ticksSinceAnchor++;
                    continue;
                }

                var tickStart = Stopwatch.GetTimestamp();

                lock (_lock)
                {
                    _pendingInput.CopyTo(input);
                    _pendingInput.ClearPresses();
                }

                simulation.Tick(input);
                var snapshot = simulation.Extract();

                lock (_lock)
                {
                    _snapshot = snapshot;
                    _snapshotTimestamp = tickStart;
                }

                ticksSinceAnchor++;

                var now = Stopwatch.GetTimestamp();

                // A tick that overran the next deadline restarts the schedule from now instead of queueing the ticks it missed:
                if (now > GetDeadline(anchor, ticksSinceAnchor))
                {
                    anchor = now;
                    ticksSinceAnchor = 0;
                }
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _fault, ExceptionDispatchInfo.Capture(exception));
        }
    }

    /// <summary>
    ///     The timestamp at which the tick <paramref name="tickCount"/> ticks after <paramref name="anchor"/> is due, in integer arithmetic so the schedule does not drift.
    /// </summary>
    private static long GetDeadline(long anchor, long tickCount)
    {
        return anchor + tickCount * Stopwatch.Frequency / GameSimulation.TicksPerSecond;
    }
}
