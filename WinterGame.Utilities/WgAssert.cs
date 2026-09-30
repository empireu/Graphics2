using System.Diagnostics;

namespace WinterGame.Utilities;

public static class WgAssert
{
    private static Thread? _gameThread;
    private static Thread? _renderThread;

    [Conditional("DEBUG")]
    public static void InitGameThread()
    {
        if (Interlocked.CompareExchange(ref _gameThread, Thread.CurrentThread, null) != null)
        {
            throw new InvalidOperationException("Tried to set game thread multiple times");
        }
    }

    [Conditional("DEBUG")]
    public static void InitRenderThread()
    {
        if (Interlocked.CompareExchange(ref _renderThread, Thread.CurrentThread, null) != null)
        {
            throw new InvalidOperationException("Tried to set render thread multiple times");
        }
    }
    
    [Conditional("DEBUG")]
    public static void DebugIsOnGameThread()
    {
        //var thread = Volatile.Read(ref _gameThread);
        //
        //Debug.Assert(thread != null, $"Game thread not set in {nameof(WgAssert)}");
        //Debug.Assert(thread == Thread.CurrentThread, "Not on game thread");
    }

    [Conditional("DEBUG")]
    public static void DebugIsOnRenderThread()
    {
        //var thread = Volatile.Read(ref _renderThread);
        //
        //Debug.Assert(thread != null, $"Render thread not set in {nameof(WgAssert)}");
        //Debug.Assert(thread == Thread.CurrentThread, "Not on render thread");
    }
}