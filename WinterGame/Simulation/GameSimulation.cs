using System.Diagnostics;
using System.Numerics;
using Graphics2.Windowing.Input;
using WinterGame.Jobs;
using WinterGame.Utilities;
using WinterGame.World;
using WinterGame.World.Generation;
using WinterGame.World.Level;
using WinterGame.World.Level.Meshing;
using WinterGame.World.Level.Residency;
using WinterGame.World.Lighting;
using WinterGame.World.Storage;

namespace WinterGame.Simulation;

internal sealed class GameSimulation : IDisposable
{
    public const int TicksPerSecond = 30;
    public const double TickSeconds = 1.0 / TicksPerSecond;

    private const int Seed = 3141;
    private const int TicketHorizontalRadius = 8;
    private const int TicketVerticalRadius = 4;

    /// <summary>
    ///     How far above the surface the player starts, in metres.
    /// </summary>
    private const float SpawnHeight = 2.0f;

    /// <summary>
    ///     The world starts at 09:00 of the first day.
    /// </summary>
    private const long StartClockTicks = WorldClock.TicksPerDay * 9 / 24;

    /// <summary>
    ///     How many clock ticks a tick advances while T is held, which runs a day in 20 seconds. Temporary, for trying out the lighting.
    /// </summary>
    private const long FastForwardClockTicks = 60;

    private readonly Player _player;
    private readonly WorldClock _clock = new(StartClockTicks);
    private readonly WorldLevel _level;
    private readonly LightEngine _light;
    private readonly LevelTicket _playerTicket;
    private readonly SectionMeshPublisher _meshPublisher;
    private readonly MessageChannel<LightVolumeMessage> _lightChannel;
    private readonly List<LightVolumeMessage> _lightMessages = [];
    private readonly DebugBrush _brush = new();
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    private bool _initialFillReported;
    private long _tickCount;
    private double _tickMilliseconds;
    private double _worstTickMilliseconds;
    private double _windowWorstTickMilliseconds;
    private int _windowTicks;

    /// <summary>
    ///     Creates the level and the mesh publisher, which register their executors, so <paramref name="scheduler"/> must not have started yet.
    ///     The player starts <see cref="SpawnHeight"/> above the surface at the origin column.
    ///     The publisher sends its mesh jobs to the render thread through <paramref name="meshChannel"/>, and the light engine's volume changes go through <paramref name="lightChannel"/>.
    /// </summary>
    public GameSimulation(JobScheduler scheduler, MessageChannel<SectionMeshMessage> meshChannel, MessageChannel<LightVolumeMessage> lightChannel)
    {
        WgAssert.DebugIsOnGameThread();

        var graph = TerrainGraph.Build(Seed, new TerrainConfig());
        var meshing = new SectionMeshingDescription();
        var spawnPosition = new Vector3(0.0f, new TerrainGraphSource(graph).Elevation(0, 0) + SpawnHeight, 0.0f);

        _player = new Player(spawnPosition);
        _level = new WorldLevel(scheduler, graph, meshing: meshing, recordChanges: true);
        _light = new LightEngine(_level, TicketHorizontalRadius, TicketVerticalRadius);
        _playerTicket = _level.AddTicket(GetSection(spawnPosition), TicketHorizontalRadius, TicketVerticalRadius);
        _meshPublisher = new SectionMeshPublisher(scheduler, meshing, meshChannel);
        _lightChannel = lightChannel;
    }

    public void Tick(TickInput input)
    {
        WgAssert.DebugIsOnGameThread();

        var start = Stopwatch.GetTimestamp();

        _clock.Tick(input.IsKeyDown(G2Key.T) ? FastForwardClockTicks : 1);
        _player.Tick(input);

        var playerSection = GetSection(_player.Position);

        _level.MoveTicket(_playerTicket, playerSection);
        _level.Update();

        // The edits come before the publisher, so the sections they change are published in this tick:
        _brush.Tick(
            _level,
            _player.Position,
            Camera3D.GetForward(input.Yaw, input.Pitch),
            input.WasMousePressed(G2MouseButton.Left),
            input.WasMousePressed(G2MouseButton.Right)
        );

        _meshPublisher.Update(_level, playerSection);
        _light.Update(playerSection);
        _light.TakeVolumeChanges(_lightMessages);
        _lightChannel.Post(_lightMessages);
        _lightMessages.Clear();
        _tickCount++;

        RecordTickTime(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        ReportInitialFill();
    }

    public GameSnapshot Extract()
    {
        WgAssert.DebugIsOnGameThread();

        return new GameSnapshot(
            _tickCount,
            _player.PreviousPosition,
            _player.Position,
            _clock.PreviousTicks,
            _clock.Ticks,
            _level.GetStats(),
            _meshPublisher.QueuedCount,
            _brush.Target,
            _tickMilliseconds,
            _worstTickMilliseconds
        );
    }

    /// <summary>
    ///     Waits for the level's generations in flight, so the scheduler has to still be running.
    /// </summary>
    public void Dispose()
    {
        WgAssert.DebugIsOnGameThread();

        _level.Dispose();
    }

    private void RecordTickTime(double milliseconds)
    {
        _tickMilliseconds = milliseconds;
        _windowWorstTickMilliseconds = Math.Max(_windowWorstTickMilliseconds, milliseconds);
        _windowTicks++;

        if (_windowTicks < TicksPerSecond)
        {
            return;
        }

        _worstTickMilliseconds = _windowWorstTickMilliseconds;
        _windowWorstTickMilliseconds = 0.0;
        _windowTicks = 0;
    }

    /// <summary>
    ///     Prints how long the level took to first have nothing left to generate, which is the fill rate of the session's start.
    /// </summary>
    private void ReportInitialFill()
    {
        if (_initialFillReported)
        {
            return;
        }

        var stats = _level.GetStats();

        if (stats.PendingSections > 0 || stats.GeneratingSections > 0)
        {
            return;
        }

        _initialFillReported = true;

        Console.WriteLine($"Level: {stats.LoadedSections} section(s) loaded in {Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds:F1} s");
    }

    private static SectionPos GetSection(Vector3 position)
    {
        return new SectionPos(
            SectionPos.VoxelToSectionCoord((int)MathF.Floor(position.X / WorldConstants.VoxelSize)),
            SectionPos.VoxelToSectionCoord((int)MathF.Floor(position.Y / WorldConstants.VoxelSize)),
            SectionPos.VoxelToSectionCoord((int)MathF.Floor(position.Z / WorldConstants.VoxelSize))
        );
    }
}
