using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Graphics2.Mathematics;
using WinterGame.Jobs;
using WinterGame.World;
using WinterGame.World.Generation;
using WinterGame.World.Level;
using WinterGame.World.Level.Residency;
using WinterGame.World.Lighting;
using WinterGame.World.Storage;

namespace WinterGame.GenerationBenchmark;

/// <summary>
///     Times the level's residency bookkeeping without generating anything.
///     The ticket sits above the highest surface, where every section is written as uniform air without a job, so one update loads a shape of any size.
/// </summary>
internal static class ResidencyBenchmark
{
    /// <summary>
    ///     Steps out and back per warmup round, so tiered compilation has promoted the move and update paths before anything is timed.
    /// </summary>
    private const int WarmupSteps = 40;

    private const int WarmupRounds = 2;
    private const int ReadCount = 1 << 18;
    private const int ReadRounds = 5;
    private const int LineLength = 128;

    public static void Run(BenchmarkOptions options)
    {
        var graph = TerrainGraph.Build(options.Seed, new TerrainConfig());
        var center = SkyCenter(graph, options.VerticalRadius);
        var loadShape = Benchmark.GetLoadShape(center, options.Radius, options.VerticalRadius, out _);
        var keepSections = Benchmark.GetLoadShape(center, options.Radius + WorldLevel.KeepMargin, options.VerticalRadius + WorldLevel.KeepMargin, out _).Length;
        var tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") == "0" ? "off" : "on";

        Console.WriteLine("Level residency benchmark");
        Console.WriteLine($"  Runtime:  {RuntimeInformation.FrameworkDescription}, {(GCSettings.IsServerGC ? "server" : "workstation")} GC, tiered compilation {tieredCompilation}");
        Console.WriteLine($"  Ticket:   horizontal radius {options.Radius}, vertical radius {options.VerticalRadius}, above the surface, {loadShape.Length} section(s) loaded and {keepSections} kept");
        Console.WriteLine($"  Unloads:  {options.UnloadsPerUpdate} per update");
        Console.WriteLine(
            options.Light
                ? $"  Light:    the engine over the game's box (radius {Benchmark.LightRadius(options)}, vertical {Benchmark.LightVerticalRadius(options)}) around the ticket, updated after every update, timed apart from it"
                : "  Light:    off"
        );
        Console.WriteLine();

        // The level is declared last so it is disposed first:
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        using var level = new WorldLevel(scheduler, graph, unloadsPerUpdate: options.UnloadsPerUpdate, recordChanges: options.Light);
        var light = options.Light ? new LightEngine(level, Benchmark.LightRadius(options), Benchmark.LightVerticalRadius(options)) : null;

        // One section loaded and unloaded first, so the arena's shared descriptor page is not counted as residency memory:
        level.GetOrLoadSection(new SectionPos(center.X - 10_000, center.Y, center.Z));
        level.Update();
        UpdateLight(light, center);

        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        var addStart = Stopwatch.GetTimestamp();
        var ticket = level.AddTicket(center, options.Radius, options.VerticalRadius);
        var add = Stopwatch.GetElapsedTime(addStart).TotalMilliseconds;
        var fillStart = Stopwatch.GetTimestamp();

        level.Update();

        var fill = Stopwatch.GetElapsedTime(fillStart).TotalMilliseconds;

        RequireLoaded(level, loadShape);

        var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        var lightFill = UpdateLight(light, center);

        Console.WriteLine("Fill, the first time each path runs:");
        Console.WriteLine($"  AddTicket:           {add:F3} ms");
        Console.WriteLine($"  The update loading:  {fill:F3} ms");
        Console.WriteLine($"  Heap growth:         {Mib(memoryAfter - memoryBefore):F2} MiB, {(double)(memoryAfter - memoryBefore) / keepSections:F0} bytes per kept section");

        if (light != null)
        {
            Console.WriteLine($"  Light after it:      {lightFill:F3} ms, {light.LastVisitCount} cell visit(s)");
        }

        Console.WriteLine();

        Warmup(level, ticket, light);
        MeasureReads(level, loadShape, options.Seed);

        var alongX = MeasureSteps(level, ticket, light, options.Moves, (start, step) => new SectionPos(start.X + step, start.Y, start.Z));
        var diagonal = MeasureSteps(level, ticket, light, options.Moves, (start, step) => new SectionPos(start.X + step, start.Y, start.Z + step));
        var up = MeasureSteps(level, ticket, light, options.Moves, (start, step) => new SectionPos(start.X, start.Y + step, start.Z));

        Console.WriteLine($"Ticket steps of one section, {options.Moves} per direction, each followed by one update:");
        Console.WriteLine($"  MoveTicket along X:  {Describe(alongX.Moves)}");
        Console.WriteLine($"  Update after it:     {Describe(alongX.Updates)}");
        DescribeLight(light, alongX.Light);
        Console.WriteLine($"  MoveTicket diagonal: {Describe(diagonal.Moves)}");
        Console.WriteLine($"  Update after it:     {Describe(diagonal.Updates)}");
        DescribeLight(light, diagonal.Light);
        Console.WriteLine($"  MoveTicket up:       {Describe(up.Moves)}");
        Console.WriteLine($"  Update after it:     {Describe(up.Updates)}");
        DescribeLight(light, up.Light);
        Console.WriteLine("  The update loads every section the step brought into the load shape, which only a ticket above the surface does, and unloads up to the budget.");
        Console.WriteLine();

        MeasureTeleport(level, ticket, light, center, options);
    }

    /// <summary>
    ///     Walks out and back along X, diagonally and up, and teleports away and back, so every path of a move and an update has run often enough to be promoted.
    /// </summary>
    private static void Warmup(WorldLevel level, LevelTicket ticket, LightEngine? light)
    {
        var home = ticket.Center;

        for (var round = 0; round < WarmupRounds; round++)
        {
            for (var step = 1; step <= WarmupSteps; step++)
            {
                StepTo(level, ticket, light, new SectionPos(home.X + step, home.Y + step % 3, home.Z + step / 2));
            }

            for (var step = WarmupSteps - 1; step >= 0; step--)
            {
                StepTo(level, ticket, light, new SectionPos(home.X + step, home.Y + step % 3, home.Z + step / 2));
            }

            StepTo(level, ticket, light, new SectionPos(home.X + 10_000, home.Y, home.Z));
            StepTo(level, ticket, light, home);
            Drain(level, light, home);

            // Tiered compilation promotes a method on a background thread some time after its 30th call:
            Thread.Sleep(500);
        }
    }

    private static void StepTo(WorldLevel level, LevelTicket ticket, LightEngine? light, SectionPos center)
    {
        level.MoveTicket(ticket, center);
        level.Update();
        UpdateLight(light, center);
    }

    /// <summary>
    ///     Updates the light engine around <paramref name="center"/> and returns how long it took, or zero without one.
    /// </summary>
    private static double UpdateLight(LightEngine? light, SectionPos center)
    {
        if (light == null)
        {
            return 0.0;
        }

        var start = Stopwatch.GetTimestamp();

        light.Update(center);

        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static void DescribeLight(LightEngine? light, double[] milliseconds)
    {
        if (light != null)
        {
            Console.WriteLine($"  Light after it:      {Describe(milliseconds)}");
        }
    }

    /// <summary>
    ///     Random section lookups and random voxel reads over the load shape, and voxel reads along lines through it, which is how a raycast reads.
    /// </summary>
    private static void MeasureReads(WorldLevel level, SectionPos[] loadShape, int seed)
    {
        var random = new Random(seed);
        var sections = new SectionPos[ReadCount];
        var voxels = new Vector3i[ReadCount];
        var lineStarts = new Vector3i[ReadCount / LineLength];

        for (var index = 0; index < ReadCount; index++)
        {
            sections[index] = loadShape[random.Next(loadShape.Length)];
            voxels[index] = RandomVoxel(random, loadShape);
        }

        for (var index = 0; index < lineStarts.Length; index++)
        {
            lineStarts[index] = RandomVoxel(random, loadShape);
        }

        var bestSections = double.MaxValue;
        var bestVoxels = double.MaxValue;
        var bestLines = double.MaxValue;
        var bestAccessorLines = double.MaxValue;
        var checksum = 0L;

        // Round 0 is the warmup:
        for (var round = 0; round <= ReadRounds; round++)
        {
            var start = Stopwatch.GetTimestamp();
            checksum += ReadSections(level, sections);

            var sectionMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            start = Stopwatch.GetTimestamp();
            checksum += ReadVoxels(level, voxels);

            var voxelMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            start = Stopwatch.GetTimestamp();
            checksum += ReadLines(level, lineStarts);

            var lineMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            start = Stopwatch.GetTimestamp();
            checksum += ReadLinesWithAccessor(level, lineStarts);

            var accessorLineMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            if (round == 0)
            {
                continue;
            }

            bestSections = Math.Min(bestSections, sectionMilliseconds);
            bestVoxels = Math.Min(bestVoxels, voxelMilliseconds);
            bestLines = Math.Min(bestLines, lineMilliseconds);
            bestAccessorLines = Math.Min(bestAccessorLines, accessorLineMilliseconds);
        }

        Console.WriteLine($"Reads, best of {ReadRounds} round(s) of {ReadCount} (checksum {checksum}):");
        Console.WriteLine($"  TryGetSection at random sections:   {Nanoseconds(bestSections):F1} ns");
        Console.WriteLine($"  ReadPoint at random voxels:         {Nanoseconds(bestVoxels):F1} ns");
        Console.WriteLine($"  ReadPoint along lines of {LineLength} voxels: {Nanoseconds(bestLines):F1} ns");
        Console.WriteLine($"  The same through one FastLevelAccessor per line: {Nanoseconds(bestAccessorLines):F1} ns");
        Console.WriteLine();
    }

    // The read loops are compiled fully optimized up front, since each runs only once per round and would otherwise be timed as on-stack replacement code:
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long ReadSections(WorldLevel level, SectionPos[] sections)
    {
        var checksum = 0L;

        foreach (var position in sections)
        {
            if (level.TryGetSection(position, out var section))
            {
                checksum += section.SlotIndex;
            }
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long ReadVoxels(WorldLevel level, Vector3i[] voxels)
    {
        var checksum = 0L;

        foreach (var voxel in voxels)
        {
            level.ReadPoint(voxel, out var content, out var material);
            checksum += content + material;
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long ReadLines(WorldLevel level, Vector3i[] lineStarts)
    {
        var checksum = 0L;

        foreach (var lineStart in lineStarts)
        {
            for (var offset = 0; offset < LineLength; offset++)
            {
                level.ReadPoint(lineStart + new Vector3i(offset, offset / 4, offset / 2), out var content, out var material);
                checksum += content + material;
            }
        }

        return checksum;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long ReadLinesWithAccessor(WorldLevel level, Vector3i[] lineStarts)
    {
        var checksum = 0L;

        foreach (var lineStart in lineStarts)
        {
            var accessor = new FastLevelAccessor(level);

            for (var offset = 0; offset < LineLength; offset++)
            {
                accessor.ReadPoint(lineStart + new Vector3i(offset, offset / 4, offset / 2), out var content, out var material);
                checksum += content + material;
            }
        }

        return checksum;
    }

    /// <summary>
    ///     Steps the ticket away from where it stands, timing each move and the update after it, then puts it back and drains the unloads, untimed.
    /// </summary>
    private static (double[] Moves, double[] Updates, double[] Light) MeasureSteps(
        WorldLevel level,
        LevelTicket ticket,
        LightEngine? light,
        int count,
        Func<SectionPos, int, SectionPos> target)
    {
        var start = ticket.Center;
        var moveMilliseconds = new double[count];
        var updateMilliseconds = new double[count];
        var lightMilliseconds = new double[count];

        for (var step = 0; step < count; step++)
        {
            var moveStart = Stopwatch.GetTimestamp();

            level.MoveTicket(ticket, target(start, step + 1));
            moveMilliseconds[step] = Stopwatch.GetElapsedTime(moveStart).TotalMilliseconds;

            var updateStart = Stopwatch.GetTimestamp();

            level.Update();
            updateMilliseconds[step] = Stopwatch.GetElapsedTime(updateStart).TotalMilliseconds;
            lightMilliseconds[step] = UpdateLight(light, ticket.Center);
        }

        StepTo(level, ticket, light, start);
        Drain(level, light, start);

        return (moveMilliseconds, updateMilliseconds, lightMilliseconds);
    }

    /// <summary>
    ///     Jumps far away, loads the new shape, then removes the ticket and times the updates that drain the backlog.
    /// </summary>
    private static void MeasureTeleport(WorldLevel level, LevelTicket ticket, LightEngine? light, SectionPos center, BenchmarkOptions options)
    {
        var moveStart = Stopwatch.GetTimestamp();
        var destination = new SectionPos(center.X + 10_000, center.Y, center.Z + 10_000);

        level.MoveTicket(ticket, destination);

        var move = Stopwatch.GetElapsedTime(moveStart).TotalMilliseconds;
        var updateStart = Stopwatch.GetTimestamp();

        level.Update();

        var update = Stopwatch.GetElapsedTime(updateStart).TotalMilliseconds;
        var lightUpdate = UpdateLight(light, destination);
        var removeStart = Stopwatch.GetTimestamp();

        level.RemoveTicket(ticket);

        var remove = Stopwatch.GetElapsedTime(removeStart).TotalMilliseconds;
        var backlog = level.UnloadQueueCount;
        var drainMilliseconds = new List<double>();

        while (level.UnloadQueueCount > 0)
        {
            var drainStart = Stopwatch.GetTimestamp();

            level.Update();
            drainMilliseconds.Add(Stopwatch.GetElapsedTime(drainStart).TotalMilliseconds);
            UpdateLight(light, destination);
        }

        Console.WriteLine("Teleport and removal:");
        Console.WriteLine($"  MoveTicket 10000 along X and Z:  {move:F3} ms");
        Console.WriteLine($"  The update loading the new shape: {update:F3} ms");

        if (light != null)
        {
            Console.WriteLine($"  Light after it:                   {lightUpdate:F3} ms, {light.LastVisitCount} cell visit(s)");
        }

        Console.WriteLine($"  RemoveTicket:                     {remove:F3} ms");
        Console.WriteLine(
            $"  Unload drain at {options.UnloadsPerUpdate} per update: {backlog} section(s) in {drainMilliseconds.Count} update(s), {Describe(drainMilliseconds.ToArray())}, {1000.0 * drainMilliseconds.Sum() / backlog:F2} us per section"
        );
        Console.WriteLine();
    }

    private static void Drain(WorldLevel level, LightEngine? light, SectionPos center)
    {
        while (level.UnloadQueueCount > 0)
        {
            level.Update();
            UpdateLight(light, center);
        }
    }

    private static void RequireLoaded(WorldLevel level, SectionPos[] shape)
    {
        foreach (var position in shape)
        {
            if (level.GetStatus(position) != SectionStatus.Ready)
            {
                throw new InvalidOperationException($"Section {position.X}, {position.Y}, {position.Z} is not loaded after the update.");
            }
        }
    }

    /// <summary>
    ///     The section whose shape's lowest row is the lowest row a level writes as air without a job. The keep margin below it is never loaded.
    /// </summary>
    private static SectionPos SkyCenter(TerrainGraph graph, int verticalRadius)
    {
        var sectionHeight = SectionPos.SectionSize * WorldConstants.VoxelSize;
        var lowestSkyY = (int)MathF.Ceiling((graph.MaxSurfaceY + WorldConstants.VoxelSize) / sectionHeight);

        return new SectionPos(0, lowestSkyY + verticalRadius, 0);
    }

    private static Vector3i RandomVoxel(Random random, SectionPos[] shape)
    {
        var section = shape[random.Next(shape.Length)];

        return section.GetOrigin() + new Vector3i(random.Next(SectionPos.SectionSize), random.Next(SectionPos.SectionSize), random.Next(SectionPos.SectionSize));
    }

    private static string Describe(double[] milliseconds)
    {
        var sorted = milliseconds.Order().ToArray();

        return $"median {sorted[sorted.Length / 2]:F3} ms, max {sorted[^1]:F3} ms";
    }

    private static double Nanoseconds(double milliseconds)
    {
        return milliseconds * 1_000_000.0 / ReadCount;
    }

    private static double Mib(long bytes)
    {
        return bytes / (1024.0 * 1024.0);
    }
}