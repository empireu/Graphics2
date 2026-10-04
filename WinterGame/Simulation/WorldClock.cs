namespace WinterGame.Simulation;

/// <summary>
///     The world's time, counted in ticks from midnight of the first day.
/// </summary>
internal sealed class WorldClock(long ticks)
{
    /// <summary>
    ///     Twenty minutes of play at the tick rate.
    /// </summary>
    public const long TicksPerDay = 20 * 60 * GameSimulation.TicksPerSecond;

    public long Ticks = ticks;

    /// <summary>
    ///     The time before the last tick, which rendering interpolates from.
    /// </summary>
    public long PreviousTicks = ticks;

    /// <summary>
    ///     Moves the time on by <paramref name="ticks"/>, which is 1 for a normal tick.
    /// </summary>
    public void Tick(long ticks)
    {
        PreviousTicks = Ticks;
        Ticks += ticks;
    }
}
