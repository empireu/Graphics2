using System.Diagnostics;
using System.Numerics;
using System.Runtime.ExceptionServices;
using Graphics2.Windowing.Input;
using WinterGame.Utilities;

namespace WinterGame.Simulation;

internal sealed class GameThread : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Vector3 _spawnPosition;
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
    private ExceptionDispatchInfo? _fault;

    public GameThread(Vector3 spawnPosition)
    {
        WgAssert.DebugIsOnRenderThread();

        _spawnPosition = spawnPosition;

        // The player stands at the spawn until the first tick publishes:
        _snapshot = new GameSnapshot(spawnPosition, spawnPosition);
        _snapshotTimestamp = Stopwatch.GetTimestamp();

        _thread = new Thread(Run)
        {
            Name = "Game",
            IsBackground = true
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
    ///     The latest tick's snapshot, and the time since that tick started as a fraction of a tick clamped to [0, 1], which is how far rendering interpolates.
    /// </summary>
    public GameSnapshot GetSnapshot(out float tickFraction)
    {
        WgAssert.DebugIsOnRenderThread();

        GameSnapshot snapshot;
        long timestamp;

        lock (_lock)
        {
            snapshot = _snapshot;
            timestamp = _snapshotTimestamp;
        }

        tickFraction = (float)Math.Clamp(Stopwatch.GetElapsedTime(timestamp).TotalSeconds / GameSimulation.TickSeconds, 0.0, 1.0);

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
            var simulation = new GameSimulation(_spawnPosition);
            var input = new TickInput();
            var anchor = Stopwatch.GetTimestamp();
            var ticksSinceAnchor = 0L;

            while (!_stopRequested)
            {
                var deadline = GetDeadline(anchor, ticksSinceAnchor);

                while (Stopwatch.GetTimestamp() < deadline)
                {
                    if (_stopRequested)
                    {
                        return;
                    }

                    Thread.SpinWait(1);
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
