using Graphics2.Core.Render;
using Graphics2.Windowing.Input;
using Silk.NET.Maths;
using WinterGame.Utilities;

namespace WinterGame;

/// <summary>
///     A running game, from the moment one is started until it is quit to the menu. It owns the camera and the world.
///     It knows nothing about the screens drawn over it, and it is told whether it has focus instead.
/// </summary>
internal sealed class GameSession : IDisposable
{
    /// <summary>
    ///     Build time per frame while the loading screen hides the world, which only has to keep that screen responsive.
    /// </summary>
    private const double LoadingBuildBudgetMilliseconds = 25.0;

    /// <summary>
    ///     Build time per frame for a rebuild while the world is on screen.
    /// </summary>
    private const double PlayingBuildBudgetMilliseconds = 4.0;

    private readonly App _app;
    private readonly Camera3D _camera = new();
    private readonly WorldLayer _world;

    public GameSession(App app, Vector2D<int> framebufferSize)
    {
        WgAssert.DebugIsOnRenderThread();

        _app = app;
        _world = new WorldLayer(app, _camera);
        _world.OnResize(framebufferSize);
    }

    /// <summary>
    ///     Whether the first build has finished. It stays set through later rebuilds.
    /// </summary>
    public bool IsLoaded { get; private set; }

    public float LoadProgress => _world.BuildProgress;

    public void OnResize(Vector2D<int> framebufferSize)
    {
        WgAssert.DebugIsOnRenderThread();

        _world.OnResize(framebufferSize);
    }

    public void Update(Frame frame, double deltaTime, InputSnapshot input, bool hasFocus)
    {
        WgAssert.DebugIsOnRenderThread();

        if (_camera.FpsMode != hasFocus)
        {
            _camera.ToggleFpsMode(_app.G2Wnd);
        }

        _camera.Update((float)deltaTime, input, _app.G2Wnd.Window.FramebufferSize);
        _world.Update(frame, IsLoaded ? PlayingBuildBudgetMilliseconds : LoadingBuildBudgetMilliseconds);

        if (!_world.IsBuilding)
        {
            IsLoaded = true;
        }
    }

    public void Render(Frame frame)
    {
        WgAssert.DebugIsOnRenderThread();

        _world.Render(frame);
    }

    public void DrawDebugUi()
    {
        WgAssert.DebugIsOnRenderThread();

        _world.DrawPanel();
    }

    public void Dispose()
    {
        WgAssert.DebugIsOnRenderThread();

        if (_camera.FpsMode)
        {
            _camera.ToggleFpsMode(_app.G2Wnd);
        }

        _world.Dispose();
    }
}
