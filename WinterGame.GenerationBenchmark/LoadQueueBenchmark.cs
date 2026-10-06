using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using WinterGame.Jobs;
using WinterGame.World;

namespace WinterGame.GenerationBenchmark;

/// <summary>
///     Times ticket steps and the update after each, which rebuilds the load queue, with and without many small tickets.
///     Above the surface every wanted section loads in the update that wants it, so only the sections a step brings in are pending.
///     On the surface the scheduler is never started, so the generation slots stay full and the rest of the load shape stays pending, which is the backlog of a fill that generation has not caught up with.
/// </summary>
internal static class LoadQueueBenchmark
{
    /// <summary>
    ///     Steps out and back per warmup round, so tiered compilation has promoted the move and update paths before anything is timed.
    /// </summary>
    private const int WarmupSteps = 40;

    private const int WarmupRounds = 2;

    /// <summary>
    ///     Sections between two machine tickets along X and Z.
    /// </summary>
    private const int MachineSpacing = 6;

    private sealed class Scenario
    {
        public required string Name;

        /// <summary>
        ///     Whether the walkers stand on the surface with generation stalled, instead of above it.
        /// </summary>
        public required bool Backlog;

        public required int Walkers;
        public required int Machines;
        public required bool MachinesMove;
    }

    private sealed class StepTimes(int count)
    {
        public readonly double[] Moves = new double[count];
        public readonly double[] Updates = new double[count];
        public readonly double[] Totals = new double[count];
        public readonly int[] Pending = new int[count];

        /// <summary>
        ///     Whether a garbage collection of any generation ran during the step.
        /// </summary>
        public readonly bool[] Collected = new bool[count];

        public long AllocatedBytes;
        public readonly int[] Collections = new int[3];
        public double PauseMilliseconds;
    }

    public static void Run(BenchmarkOptions options)
    {
        var graph = TerrainGraph.Build(options.Seed, new TerrainConfig());
        var tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") == "0" ? "off" : "on";
        var walkerSections = Benchmark.GetLoadShape(default, options.Radius, options.VerticalRadius, out _).Length;
        var machineSections = Benchmark.GetLoadShape(default, options.MachineRadius, options.MachineVerticalRadius, out _).Length;

        Console.WriteLine("Level load queue benchmark");
        Console.WriteLine($"  Runtime:   {RuntimeInformation.FrameworkDescription}, {(GCSettings.IsServerGC ? "server" : "workstation")} GC, tiered compilation {tieredCompilation}");
        Console.WriteLine($"  Walker:    horizontal radius {options.Radius}, vertical radius {options.VerticalRadius}, {walkerSections} section(s) in the load shape");
        Console.WriteLine($"  Machines:  {options.Machines} of horizontal radius {options.MachineRadius}, vertical radius {options.MachineVerticalRadius}, {machineSections} section(s) each, {MachineSpacing} sections apart, loaded synchronously when added");
        Console.WriteLine($"  Steps:     {options.Moves} of one section along X, then {options.Moves} diagonal, each followed by one update");
        Console.WriteLine("  Warmup:    every scenario runs once unreported before the reported pass");
        Console.WriteLine();

        var scenarios = new[]
        {
            new Scenario { Name = "Sky, 1 walker", Backlog = false, Walkers = 1, Machines = 0, MachinesMove = false },
            new Scenario { Name = $"Sky, 1 walker, {options.Machines} machines", Backlog = false, Walkers = 1, Machines = options.Machines, MachinesMove = false },
            new Scenario { Name = $"Sky, 1 walker, {options.Machines} machines moving with it", Backlog = false, Walkers = 1, Machines = options.Machines, MachinesMove = true },
            new Scenario { Name = "Sky, 4 walkers apart", Backlog = false, Walkers = 4, Machines = 0, MachinesMove = false },
            new Scenario { Name = "Backlog, 1 walker", Backlog = true, Walkers = 1, Machines = 0, MachinesMove = false },
            new Scenario { Name = $"Backlog, 1 walker, {options.Machines} machines", Backlog = true, Walkers = 1, Machines = options.Machines, MachinesMove = false }
        };

        // Every scenario runs once unreported first, since the first one in the process otherwise times code that tiered compilation has not promoted yet:
        foreach (var scenario in scenarios)
        {
            RunScenario(graph, options, scenario, report: false);
        }

        foreach (var scenario in scenarios)
        {
            RunScenario(graph, options, scenario, report: true);
        }
    }

    private static void RunScenario(TerrainGraph graph, BenchmarkOptions options, Scenario scenario, bool report)
    {
        var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        var level = new Level(scheduler, graph, unloadsPerUpdate: options.UnloadsPerUpdate);

        try
        {
            var skyY = LowestSkySectionY(graph);
            var walkerStart = scenario.Backlog ? Benchmark.SurfaceSection(graph, 0, 0) : new SectionPos(0, skyY + options.VerticalRadius, 0);
            var walkers = new List<LevelTicket>();
            var machines = new List<LevelTicket>();

            for (var index = 0; index < scenario.Walkers; index++)
            {
                // Far enough apart that no two keep shapes overlap:
                var offsetZ = index * (2 * (options.Radius + Level.KeepMargin) + 1);

                walkers.Add(level.AddTicket(new SectionPos(walkerStart.X, walkerStart.Y, walkerStart.Z + offsetZ), options.Radius, options.VerticalRadius));
            }

            var fillStart = Stopwatch.GetTimestamp();

            level.Update();

            var fill = Stopwatch.GetElapsedTime(fillStart).TotalMilliseconds;
            var addMilliseconds = 0.0;
            var loadMilliseconds = 0.0;
            var firstUpdate = 0.0;

            if (scenario.Machines > 0)
            {
                AddMachines(level, options, scenario.Machines, skyY + options.MachineVerticalRadius, machines, out addMilliseconds, out loadMilliseconds);

                var updateStart = Stopwatch.GetTimestamp();

                level.Update();
                firstUpdate = Stopwatch.GetElapsedTime(updateStart).TotalMilliseconds;
            }

            var moving = scenario.MachinesMove ? walkers.Concat(machines).ToArray() : walkers.ToArray();

            Warmup(level, moving);

            var alongX = MeasureSteps(level, moving, options.Moves, 1, 0);
            var diagonal = MeasureSteps(level, moving, options.Moves, 1, 1);

            if (!report)
            {
                return;
            }

            Console.WriteLine($"{scenario.Name}:");
            Console.WriteLine($"  The walkers' first update: {fill:F3} ms");

            if (scenario.Machines > 0)
            {
                Console.WriteLine($"  Adding the machines: AddTicket {addMilliseconds:F3} ms in all, synchronous loads {loadMilliseconds:F3} ms, the update after them {firstUpdate:F3} ms");
            }

            Report("Along X", alongX);
            Report("Diagonal", diagonal);
            Console.WriteLine();
        }
        finally
        {
            // The scheduler never started, so the jobs in flight are run here before the level waits on them:
            scheduler.Drain();
            level.Dispose();
            scheduler.Dispose();
        }
    }

    /// <summary>
    ///     Adds the machines on a square grid around the origin column and loads each one's load shape synchronously, which is how a machine is meant to be added.
    /// </summary>
    private static void AddMachines(Level level, BenchmarkOptions options, int count, int centerY, List<LevelTicket> machines, out double addMilliseconds, out double loadMilliseconds)
    {
        var side = (int)Math.Ceiling(Math.Sqrt(count));

        addMilliseconds = 0.0;
        loadMilliseconds = 0.0;

        for (var index = 0; index < count; index++)
        {
            var center = new SectionPos((index % side - side / 2) * MachineSpacing, centerY, (index / side - side / 2) * MachineSpacing);
            var addStart = Stopwatch.GetTimestamp();

            machines.Add(level.AddTicket(center, options.MachineRadius, options.MachineVerticalRadius));
            addMilliseconds += Stopwatch.GetElapsedTime(addStart).TotalMilliseconds;

            var loadStart = Stopwatch.GetTimestamp();

            foreach (var position in Benchmark.GetLoadShape(center, options.MachineRadius, options.MachineVerticalRadius, out _))
            {
                level.GetOrLoadSection(position);
            }

            loadMilliseconds += Stopwatch.GetElapsedTime(loadStart).TotalMilliseconds;
        }
    }

    /// <summary>
    ///     Steps the moving tickets out and back along X, so every path of a step and an update has run often enough to be promoted, then drains the unloads.
    /// </summary>
    private static void Warmup(Level level, LevelTicket[] moving)
    {
        for (var round = 0; round < WarmupRounds; round++)
        {
            for (var step = 0; step < WarmupSteps; step++)
            {
                Step(level, moving, 1, 0);
                level.Update();
            }

            for (var step = 0; step < WarmupSteps; step++)
            {
                Step(level, moving, -1, 0);
                level.Update();
            }

            while (level.UnloadQueueCount > 0)
            {
                level.Update();
            }

            // Tiered compilation promotes a method on a background thread some time after its 30th call:
            Thread.Sleep(500);
        }
    }

    private static StepTimes MeasureSteps(Level level, LevelTicket[] moving, int count, int deltaX, int deltaZ)
    {
        var times = new StepTimes(count);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var pauseBefore = GC.GetTotalPauseDuration();
        Span<int> collectionsBefore = stackalloc int[times.Collections.Length];

        for (var generation = 0; generation < times.Collections.Length; generation++)
        {
            collectionsBefore[generation] = GC.CollectionCount(generation);
        }

        for (var step = 0; step < count; step++)
        {
            var stepCollections = GC.CollectionCount(0);
            var moveStart = Stopwatch.GetTimestamp();

            Step(level, moving, deltaX, deltaZ);

            var updateStart = Stopwatch.GetTimestamp();

            level.Update();

            var updateEnd = Stopwatch.GetTimestamp();

            times.Moves[step] = Stopwatch.GetElapsedTime(moveStart, updateStart).TotalMilliseconds;
            times.Updates[step] = Stopwatch.GetElapsedTime(updateStart, updateEnd).TotalMilliseconds;
            times.Totals[step] = times.Moves[step] + times.Updates[step];
            times.Pending[step] = level.GetStats().PendingSections;
            times.Collected[step] = GC.CollectionCount(0) != stepCollections;
        }

        times.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        times.PauseMilliseconds = (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;

        for (var generation = 0; generation < times.Collections.Length; generation++)
        {
            times.Collections[generation] = GC.CollectionCount(generation) - collectionsBefore[generation];
        }

        return times;
    }

    private static void Step(Level level, LevelTicket[] moving, int deltaX, int deltaZ)
    {
        foreach (var ticket in moving)
        {
            var center = ticket.Center;

            level.MoveTicket(ticket, new SectionPos(center.X + deltaX, center.Y, center.Z + deltaZ));
        }
    }

    private static void Report(string name, StepTimes times)
    {
        var pending = times.Pending.Order().ToArray();

        Console.WriteLine($"  {name}, pending after the update: median {pending[pending.Length / 2]}, max {pending[^1]}; {times.AllocatedBytes / times.Moves.Length} bytes allocated per step");
        Console.WriteLine($"    MoveTicket:  {Describe(times.Moves)}");
        Console.WriteLine($"    Update:      {Describe(times.Updates)}");
        Console.WriteLine($"    Step total:  {Describe(times.Totals)}");

        var withoutCollection = times.Totals.Where((_, step) => !times.Collected[step]).DefaultIfEmpty(0.0).Max();

        Console.WriteLine(
            $"    GC:          {times.Collections[0]} gen0, {times.Collections[1]} gen1, {times.Collections[2]} gen2 collection(s), {times.PauseMilliseconds:F1} ms paused; step max without a collection {Microseconds(withoutCollection):F1} us"
        );
    }

    private static string Describe(double[] milliseconds)
    {
        var sorted = milliseconds.Order().ToArray();
        var percentile95 = sorted[(int)Math.Ceiling(0.95 * sorted.Length) - 1];

        return $"median {Microseconds(sorted[sorted.Length / 2]),9:F1} us, p95 {Microseconds(percentile95),9:F1} us, max {Microseconds(sorted[^1]),9:F1} us";
    }

    private static double Microseconds(double milliseconds)
    {
        return milliseconds * 1000.0;
    }

    /// <summary>
    ///     The lowest section whose first sample is one sample above the highest surface, the lowest one a level writes as air without a job.
    /// </summary>
    private static int LowestSkySectionY(TerrainGraph graph)
    {
        var sectionHeight = SectionPos.SectionSize * WorldConstants.VoxelSize;

        return (int)MathF.Ceiling((graph.MaxSurfaceY + WorldConstants.VoxelSize) / sectionHeight);
    }
}
