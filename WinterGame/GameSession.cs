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
    private readonly GameSimulation _simulation;
    private readonly TickInput _tickInput = new();
    private GameSnapshot _snapshot;

    /// <summary>
    ///     Real time since the last tick was due, so its fraction of a tick is how far rendering interpolates.
    /// </summary>
    private double _timeSinceTick;

    public GameSession(App app, Vector2D<int> framebufferSize)
    {
        WgAssert.DebugIsOnRenderThread();

        _app = app;
        _world = new WorldLayer(app, _camera);
        _world.OnResize(framebufferSize);
        _simulation = new GameSimulation(_world.SpawnPosition);
        _snapshot = _simulation.Extract();
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

        _camera.Update(input, _app.G2Wnd.Window.FramebufferSize);

        if (hasFocus)
        {
            _tickInput.AddFrame(input);
        }
        else
        {
            _tickInput.ReleaseKeys();
        }

        RunDueTick(deltaTime);

        _camera.Position = _snapshot.GetPlayerPosition((float)(_timeSinceTick / GameSimulation.TickSeconds));
        _world.Update(frame, IsLoaded ? PlayingBuildBudgetMilliseconds : LoadingBuildBudgetMilliseconds);

        if (!_world.IsBuilding)
        {
            IsLoaded = true;
        }
    }

    private void RunDueTick(double deltaTime)
    {
        _timeSinceTick += deltaTime;

        if (_timeSinceTick < GameSimulation.TickSeconds)
        {
            return;
        }

        _tickInput.Yaw = _camera.Yaw;
        _tickInput.Pitch = _camera.Pitch;
        _simulation.Tick(_tickInput);
        _tickInput.ClearPresses();
        _snapshot = _simulation.Extract();

        _timeSinceTick %= GameSimulation.TickSeconds;
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
