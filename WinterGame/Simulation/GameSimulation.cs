using System.Diagnostics;
using System.Numerics;
using WinterGame.Jobs;
using WinterGame.Utilities;
using WinterGame.World;

namespace WinterGame.Simulation;

internal sealed class GameSimulation : IDisposable
{
    public const int TicksPerSecond = 30;
    public const double TickSeconds = 1.0 / TicksPerSecond;

    private const int Seed = 3141;
    private const int TicketHorizontalRadius = 8;
    private const int TicketVerticalRadius = 4;

    private readonly Player _player;
    private readonly Level _level;
    private readonly LevelTicket _playerTicket;
    private readonly List<SectionMeshChange> _meshChanges = [];
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    private bool _initialFillReported;
    private double _tickMilliseconds;
    private double _worstTickMilliseconds;
    private double _windowWorstTickMilliseconds;
    private int _windowTicks;

    /// <summary>
    ///     Creates the level, which registers its executor, so <paramref name="scheduler"/> must not have started yet.
    /// </summary>
    public GameSimulation(Vector3 spawnPosition, JobScheduler scheduler)
    {
        WgAssert.DebugIsOnGameThread();

        _player = new Player(spawnPosition);
        _level = new Level(scheduler, TerrainGraph.Build(Seed, new TerrainConfig()));
        _playerTicket = _level.AddTicket(GetSection(spawnPosition), TicketHorizontalRadius, TicketVerticalRadius);
    }

    public void Tick(TickInput input)
    {
        WgAssert.DebugIsOnGameThread();

        var start = Stopwatch.GetTimestamp();

        _player.Tick(input);
        _level.MoveTicket(_playerTicket, GetSection(_player.Position));
        _level.Update();

        _level.TakeMeshChanges(_meshChanges);
        _meshChanges.Clear();

        RecordTickTime(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        ReportInitialFill();
    }

    public GameSnapshot Extract()
    {
        WgAssert.DebugIsOnGameThread();

        return new GameSnapshot(
            _player.PreviousPosition,
            _player.Position,
            _level.GetStats(),
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
