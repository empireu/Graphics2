using System.Numerics;
using WinterGame.World;

namespace WinterGame.Simulation;

/// <summary>
///     What rendering needs from the simulation, taken at the end of a tick.
///     The render thread reads this and never the simulation itself.
/// </summary>
internal readonly struct GameSnapshot(
    Vector3 previousPlayerPosition,
    Vector3 playerPosition,
    LevelStats levelStats,
    double tickMilliseconds,
    double worstTickMilliseconds)
{
    public readonly Vector3 PreviousPlayerPosition = previousPlayerPosition;
    public readonly Vector3 PlayerPosition = playerPosition;
    public readonly LevelStats LevelStats = levelStats;

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
}
