using Graphics2.Core.Render;
using Graphics2.Windowing.Input;
using Silk.NET.Maths;
using WinterGame.Simulation;
using WinterGame.Utilities;

namespace WinterGame;

/// <summary>
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
    private readonly GameThread _gameThread;

    public GameSession(App app, Vector2D<int> framebufferSize)
    {
        WgAssert.DebugIsOnRenderThread();

        _app = app;
        _world = new WorldLayer(app, _camera);
        _world.OnResize(framebufferSize);
        _gameThread = new GameThread(_world.SpawnPosition);
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

    public void Update(Frame frame, InputSnapshot input, bool hasFocus)
    {
        WgAssert.DebugIsOnRenderThread();

        _gameThread.ThrowIfFaulted();

        if (_camera.FpsMode != hasFocus)
        {
            _camera.ToggleFpsMode(_app.G2Wnd);
        }

        _camera.Update(input, _app.G2Wnd.Window.FramebufferSize);
        _gameThread.SubmitFrame(input, hasFocus, _camera.Yaw, _camera.Pitch);

        var snapshot = _gameThread.GetSnapshot(out var tickFraction);

        _camera.Position = snapshot.GetPlayerPosition(tickFraction);
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

        _gameThread.Dispose();

        if (_camera.FpsMode)
        {
            _camera.ToggleFpsMode(_app.G2Wnd);
        }

        _world.Dispose();
    }
}
