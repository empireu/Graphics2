using WinterGame.Utilities;

namespace WinterGame.Screens;

/// <summary>
///     The screens open over the session, or over nothing in the main menu. Only the top screen is updated and drawn.
///     Every change is queued and applied by <see cref="ApplyPending"/> at the start of the next frame, so a screen can close itself or open another from inside its own <see cref="Screen.Draw"/>.
/// </summary>
internal sealed class ScreenStack
{
    private enum OperationKind
    {
        Push,
        Remove,
        Clear
    }

    private readonly List<Screen> _screens = [];
    private readonly List<(OperationKind Kind, Screen? Screen)> _pending = [];

    public int Count => _screens.Count;

    public Screen? Top => _screens.Count > 0 ? _screens[^1] : null;

    /// <summary>
    ///     Whether any open screen hides the session.
    /// </summary>
    public bool HidesSession
    {
        get
        {
            foreach (var screen in _screens)
            {
                if (screen.HidesSession)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void Push(Screen screen)
    {
        WgAssert.DebugIsOnRenderThread();
        ArgumentNullException.ThrowIfNull(screen);

        _pending.Add((OperationKind.Push, screen));
    }

    /// <summary>
    ///     Closes a screen wherever it is in the stack.
    ///     A screen that is no longer open is ignored, so a screen may ask to close itself after a <see cref="Clear"/> has already removed it.
    /// </summary>
    public void Remove(Screen screen)
    {
        WgAssert.DebugIsOnRenderThread();
        ArgumentNullException.ThrowIfNull(screen);

        _pending.Add((OperationKind.Remove, screen));
    }

    public void Clear()
    {
        WgAssert.DebugIsOnRenderThread();

        _pending.Add((OperationKind.Clear, null));
    }

    /// <summary>
    ///     Applies the queued changes in the order they were made.
    /// </summary>
    public void ApplyPending()
    {
        WgAssert.DebugIsOnRenderThread();

        foreach (var (kind, screen) in _pending)
        {
            switch (kind)
            {
                case OperationKind.Push:
                    _screens.Add(screen!);
                    break;
                case OperationKind.Remove:
                    _screens.Remove(screen!);
                    break;
                case OperationKind.Clear:
                    _screens.Clear();
                    break;
            }
        }

        _pending.Clear();
    }

    public void Update(double deltaTime)
    {
        WgAssert.DebugIsOnRenderThread();

        Top?.Update(deltaTime);
    }

    public void Draw()
    {
        WgAssert.DebugIsOnRenderThread();

        Top?.Draw();
    }
}
