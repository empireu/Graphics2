using System.Numerics;
using WinterGame.World;
using WinterGame.World.Level;

namespace WinterGame.Simulation;

/// <summary>
///     What rendering needs from the simulation, taken at the end of a tick.
///     The render thread reads this and never the simulation itself.
/// </summary>
internal readonly struct GameSnapshot(
    long tickCount,
    Vector3 previousPlayerPosition,
    Vector3 playerPosition,
    long previousClockTicks,
    long clockTicks,
    LevelStats levelStats,
    int meshQueueCount,
    Vector3? brushTarget,
    double tickMilliseconds,
    double worstTickMilliseconds)
{
    /// <summary>
    ///     The ticks run so far, which is 0 for the snapshot a session starts with.
    /// </summary>
    public readonly long TickCount = tickCount;

    public readonly Vector3 PreviousPlayerPosition = previousPlayerPosition;
    public readonly Vector3 PlayerPosition = playerPosition;

    /// <summary>
    ///     The world clock before this tick, which can be more than one tick behind <see cref="ClockTicks"/> while the time is fast-forwarded.
    /// </summary>
    public readonly long PreviousClockTicks = previousClockTicks;

    public readonly long ClockTicks = clockTicks;

    public readonly LevelStats LevelStats = levelStats;

    /// <summary>
    ///     Sections waiting for a mesh job, which does not count the jobs already sent.
    /// </summary>
    public readonly int MeshQueueCount = meshQueueCount;

    /// <summary>
    ///     Where the debug brush would dig or fill, in metres, or null when the view meets no ground within its reach.
    /// </summary>
    public readonly Vector3? BrushTarget = brushTarget;

    /// <summary>
    ///     How long the tick took on the game thread.
    /// </summary>
    public readonly double TickMilliseconds = tickMilliseconds;

    /// <summary>
    ///     The longest tick of the last full second, so a single slow tick stays readable.
    /// </summary>
    public readonly double WorstTickMilliseconds = worstTickMilliseconds;

    /// <summary>
    ///     The player's position <paramref name="tickFraction"/> of the way from the previous tick to this one.
    /// </summary>
    public Vector3 GetPlayerPosition(float tickFraction)
    {
        return Vector3.Lerp(PreviousPlayerPosition, PlayerPosition, tickFraction);
    }

    /// <summary>
    ///     The time of day <paramref name="tickFraction"/> of the way from the previous tick to this one, as the fraction of a day since midnight.
    /// </summary>
    public double GetTimeOfDay(float tickFraction)
    {
        var ticks = PreviousClockTicks + (ClockTicks - PreviousClockTicks) * (double)tickFraction;
        var days = ticks / WorldClock.TicksPerDay;

        return days - Math.Floor(days);
    }
}
