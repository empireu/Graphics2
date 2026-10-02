using System.Diagnostics;

namespace WinterGame.Utilities;

public static class PreciseWaiter
{
    /// <summary>
    ///     The platform's waiter, or one that spins the whole way where the platform has no timer fine enough.
    /// </summary>
    public static IPreciseWaiter Create()
    {
        if (OperatingSystem.IsWindows())
        {
            var waiter = WindowsPreciseWaiter.TryCreate();

            if (waiter != null)
            {
                return waiter;
            }
        }

        return new SpinningPreciseWaiter();
    }

    public static void SpinUntil(long timestamp)
    {
        while (Stopwatch.GetTimestamp() < timestamp)
        {
            Thread.SpinWait(1);
        }
    }
}

/// <summary>
///     Spins the whole wait, which is exact but keeps a hardware thread fully busy.
/// </summary>
internal sealed class SpinningPreciseWaiter : IPreciseWaiter
{
    public void WaitUntil(long timestamp)
    {
        PreciseWaiter.SpinUntil(timestamp);
    }

    public void Dispose()
    {
    }
}
