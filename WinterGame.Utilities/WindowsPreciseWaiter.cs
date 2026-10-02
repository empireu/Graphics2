using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace WinterGame.Utilities;

/// <summary>
///     Sleeps on a high resolution waitable timer until shortly before the deadline, then spins the rest.
///     The timer needs Windows 10 1803 and does not change the system timer resolution.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsPreciseWaiter : IPreciseWaiter
{
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerAllAccess = 0x001F0003;
    private const uint Infinite = 0xFFFFFFFF;

    /// <summary>
    ///     The timer was measured waking up to 1.4 ms late, so the last 2 ms are spun.
    /// </summary>
    private static readonly long SpinDuration = Stopwatch.Frequency * 2 / 1000;

    private readonly SafeWaitHandle _timer;

    private WindowsPreciseWaiter(SafeWaitHandle timer)
    {
        _timer = timer;
    }

    /// <summary>
    ///     A waiter over a new high resolution timer, or null when this version of Windows cannot create one.
    /// </summary>
    public static WindowsPreciseWaiter? TryCreate()
    {
        var timer = CreateWaitableTimerExW(IntPtr.Zero, IntPtr.Zero, CreateWaitableTimerHighResolution, TimerAllAccess);

        if (timer.IsInvalid)
        {
            timer.Dispose();
            return null;
        }

        return new WindowsPreciseWaiter(timer);
    }

    public void WaitUntil(long timestamp)
    {
        var sleepDuration = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), timestamp - SpinDuration);

        if (sleepDuration.Ticks > 0)
        {
            // A negative due time is relative to now, in the same 100 ns units as a TimeSpan tick:
            var dueTime = -sleepDuration.Ticks;

            if (SetWaitableTimer(_timer, in dueTime, 0, IntPtr.Zero, IntPtr.Zero, false))
            {
                WaitForSingleObject(_timer, Infinite);
            }
        }

        PreciseWaiter.SpinUntil(timestamp);
    }

    public void Dispose()
    {
        _timer.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr timerAttributes, IntPtr timerName, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(SafeWaitHandle timer, in long dueTime, int period, IntPtr completionRoutine, IntPtr completionArgument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);
}
