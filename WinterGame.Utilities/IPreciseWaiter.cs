namespace WinterGame.Utilities;

/// <summary>
///     Waits until a <see cref="System.Diagnostics.Stopwatch"/> timestamp to within microseconds.
///     Each platform chooses how much to sleep and how much to spin.
/// </summary>
public interface IPreciseWaiter : IDisposable
{
    /// <summary>
    ///     Returns at or just after <paramref name="timestamp"/>, or at once when it has passed.
    /// </summary>
    void WaitUntil(long timestamp);
}
