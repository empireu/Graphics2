using Graphics2.Core.Render;
using Graphics2.Windowing.Input;
using ImGuiNET;
using Silk.NET.Maths;
using WinterGame.Simulation;
using WinterGame.Utilities;
using WinterGame.World;

namespace WinterGame;

/// <summary>
///     It knows nothing about the screens drawn over it, and it is told whether it has focus instead.
/// </summary>
internal sealed class GameSession : IDisposable
{
    private readonly App _app;
    private readonly Camera3D _camera = new();
    private readonly WorldLayer _world;
    private readonly SectionMeshReceiver _meshReceiver;
    private readonly GameThread _gameThread;
    private GameSnapshot _snapshot;

    public GameSession(App app, Vector2D<int> framebufferSize)
    {
        WgAssert.DebugIsOnRenderThread();

        var meshChannel = new SectionMeshChannel();

        _app = app;
        _world = new WorldLayer(app, _camera);
        _world.OnResize(framebufferSize);
        _meshReceiver = new SectionMeshReceiver(meshChannel);
        _gameThread = new GameThread(meshChannel);
    }

    /// <summary>
    ///     Whether the level around the player has been generated and meshed for the first time. It stays set afterwards.
    /// </summary>
    public bool IsLoaded { get; private set; }

    /// <summary>
    ///     The fraction of the level's wanted sections that are loaded, from the latest snapshot.
    /// </summary>
    public float LoadProgress
    {
        get
        {
            var stats = _snapshot.LevelStats;
            var wanted = stats.LoadedSections + stats.GeneratingSections + stats.PendingSections;

            return wanted > 0 ? (float)stats.LoadedSections / wanted : 0.0f;
        }
    }

    public void OnResize(Vector2D<int> framebufferSize)
    {
        WgAssert.DebugIsOnRenderThread();

        _world.OnResize(framebufferSize);
    }

    /// <summary>
    ///     While <paramref name="paused"/>, the game thread skips its ticks, so the snapshot and everything drawn from it stand still.
    /// </summary>
    public void Update(Frame frame, InputSnapshot input, bool hasFocus, bool paused)
    {
        WgAssert.DebugIsOnRenderThread();

        _gameThread.ThrowIfFaulted();
        _gameThread.SetPaused(paused);

        if (_camera.FpsMode != hasFocus)
        {
            _camera.ToggleFpsMode(_app.G2Wnd);
        }

        _camera.Update(input, _app.G2Wnd.Window.FramebufferSize);
        _gameThread.SubmitFrame(input, hasFocus, _camera.Yaw, _camera.Pitch);

        _snapshot = _gameThread.GetSnapshot(input.Timestamp, out var tickFraction);

        _camera.Position = _snapshot.GetPlayerPosition(tickFraction);

        // The snapshot is read before the meshes are taken, so every job its tick sent is already in the channel when the receiver counts what is in flight:
        _world.Update(frame, _meshReceiver);

        var stats = _snapshot.LevelStats;

        if (_snapshot.TickCount > 0 && stats.PendingSections == 0 && stats.GeneratingSections == 0 &&
            _snapshot.MeshQueueCount == 0 && _meshReceiver.InFlightCount == 0)
        {
            IsLoaded = true;
        }
    }

    public void Render(Frame frame)
    {
        WgAssert.DebugIsOnRenderThread();

        _world.Render(frame, _snapshot.BrushTarget);
    }

    public void DrawDebugUi()
    {
        WgAssert.DebugIsOnRenderThread();

        _world.DrawPanel();

        if (ImGui.Begin("Level"))
        {
            var stats = _snapshot.LevelStats;

            ImGui.Text($"Loaded: {stats.LoadedSections} section(s)");
            ImGui.Text($"Generating: {stats.GeneratingSections}");
            ImGui.Text($"Pending: {stats.PendingSections}");
            ImGui.Text($"Unload queue: {stats.UnloadQueueCount}");
            ImGui.Text($"Meshes: {_meshReceiver.MeshCount} received, {_meshReceiver.InFlightCount} in flight");
            ImGui.Text($"Tick: {_snapshot.TickMilliseconds:F2} ms, worst of the last second {_snapshot.WorstTickMilliseconds:F2} ms");
        }

        ImGui.End();
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
