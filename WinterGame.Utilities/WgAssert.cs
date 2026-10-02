using System.Diagnostics;

namespace WinterGame.Utilities;

/// <summary>
///     Debug checks that a call runs on the thread that owns its state.
///     A claim marks only the calling thread, so each session's game thread claims itself and each test claims the thread it runs on.
/// </summary>
public static class WgAssert
{
    [ThreadStatic]
    private static bool _isGameThread;

    [ThreadStatic]
    private static bool _isRenderThread;

    [Conditional("DEBUG")]
    public static void InitGameThread()
    {
        _isGameThread = true;
    }

    [Conditional("DEBUG")]
    public static void InitRenderThread()
    {
        _isRenderThread = true;
    }

    [Conditional("DEBUG")]
    public static void DebugIsOnGameThread()
    {
        Debug.Assert(_isGameThread, "Not on the game thread");
    }

    [Conditional("DEBUG")]
    public static void DebugIsOnRenderThread()
    {
        Debug.Assert(_isRenderThread, "Not on the render thread");
    }
}