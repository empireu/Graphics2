using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using Graphics2.Data;
using WinterGame.GenerationBenchmark;
using WinterGame.Jobs;
using WinterGame.World;
using WinterGame.World.Generation;
using WinterGame.World.Level;
using WinterGame.World.Level.Loading;
using WinterGame.World.Level.Residency;
using WinterGame.World.Lighting;
using WinterGame.World.Storage;

// ReSharper disable FieldCanBeMadeReadOnly.Global

Debug.Fail("Run the generation benchmark in Release");

var options = BenchmarkOptions.Parse(args);

if (options.Residency)
{
    ResidencyBenchmark.Run(options);
    return;
}

if (options.LoadQueue)
{
    LoadQueueBenchmark.Run(options);
    return;
}

Console.WriteLine("Section generation and storage benchmark");
Console.WriteLine($"  Runtime:  {RuntimeInformation.FrameworkDescription}, {(GCSettings.IsServerGC ? "server" : "workstation")} GC, {Environment.ProcessorCount} core(s)");
Console.WriteLine($"  Terrain:  seed {options.Seed}, default TerrainConfig");
Console.WriteLine($"  Ticket:   horizontal radius {options.Radius}, vertical radius {options.VerticalRadius}, centred on the surface section at the origin");
Console.WriteLine($"  Unpaced:  {string.Join(", ", options.Workers)} worker(s), updating as fast as the main thread can");
Console.WriteLine($"  Paced:    {options.Workers.Max()} worker(s) at {options.TickHz} Hz with {string.Join(", ", options.PacedSlots)} slot(s)");
Console.WriteLine($"  Unloads:  {options.UnloadsPerUpdate} per update");
Console.WriteLine();

Benchmark.Run(options);

internal static class Benchmark
{
    private const double SectionMetres = SectionPos.SectionSize * WorldConstants.VoxelSize;
    private const int DescriptorBytesPerSection = Section.DescriptorsPerSection * sizeof(uint);
    private const int PageNumbers = SectionArena.MaxPageNumber + 1;

    private enum SectionKind
    {
        Air,
        Solid,
        Surface
    }

    private sealed class RunResult
    {
        public required int Workers;
        public required int Slots;
        public required double Seconds;
        public required int Ticks;
        public required double UpdateAverageMilliseconds;
        public required double UpdateMaximumMilliseconds;

        /// <summary>
        ///     The light engine's update after each level update, zero when the run had none.
        /// </summary>
        public required double LightAverageMilliseconds;

        public required double LightMaximumMilliseconds;
    }

    /// <summary>
    ///     The light box of the game, or the ticket's when that is smaller.
    /// </summary>
    public static int LightRadius(BenchmarkOptions options)
    {
        return Math.Min(options.Radius, 8);
    }

    public static int LightVerticalRadius(BenchmarkOptions options)
    {
        return Math.Min(options.VerticalRadius, 4);
    }

    public static void Run(BenchmarkOptions options)
    {
        var graph = TerrainGraph.Build(options.Seed, new TerrainConfig());
        var skyMinimumY = graph.MaxSurfaceY + WorldConstants.VoxelSize;
        var center = SurfaceSection(graph, 0, 0);
        var shape = GetLoadShape(center, options.Radius, options.VerticalRadius, out var columns);
        var generated = shape.Count(position => !IsSky(position, skyMinimumY));

        Console.WriteLine($"Shape: {shape.Length} section(s) in {columns} column(s) of {2 * options.VerticalRadius + 1}, {shape.Length - generated} of them sky, {generated} generated");
        Console.WriteLine();

        MeasureSectionCost(graph, center, shape, skyMinimumY, options);

        var unpaced = new List<RunResult>();
        var maximumWorkers = options.Workers.Max();

        foreach (var workers in options.Workers)
        {
            // Enough slots that the workers are never starved between two updates:
            var slots = Math.Max(SectionGenerationExecutor.DefaultSlotCount, 2 * workers);

            unpaced.Add(
                MeasureFill(
                    graph,
                    center,
                    shape,
                    options,
                    workers,
                    slots,
                    tickHz: 0.0,
                    afterFill: workers == maximumWorkers
                        ? (level, ticket) =>
                        {
                            MeasureStorage(level, shape, columns);
                            MeasureMoves(level, ticket, options);
                        }
                        : null
                )
            );
        }

        Console.WriteLine("Unpaced fill of the shape, with Update called as fast as the main thread can:");
        Console.WriteLine("  Workers  Slots  Fill (s)  Sections/s  Speedup  Ticks   Update avg / max (ms)");

        foreach (var result in unpaced)
        {
            Console.WriteLine(
                $"  {result.Workers,7}  {result.Slots,5}  {result.Seconds,8:F2}  {generated / result.Seconds,10:F0}  {unpaced[0].Seconds / result.Seconds,6:F2}x  {result.Ticks,6}  {result.UpdateAverageMilliseconds,6:F3} / {result.UpdateMaximumMilliseconds:F2}"
            );
        }

        Console.WriteLine();
        Console.WriteLine($"Paced fill at {options.TickHz} Hz with {maximumWorkers} worker(s), where one tick publishes at most one section per free slot:");
        Console.WriteLine("  Slots  Fill (s)  Sections/s  Bound (slots x Hz)  Ticks  Update avg / max (ms)");

        foreach (var slots in options.PacedSlots)
        {
            var result = MeasureFill(graph, center, shape, options, maximumWorkers, slots, options.TickHz, afterFill: null);

            Console.WriteLine(
                $"  {result.Slots,5}  {result.Seconds,8:F2}  {generated / result.Seconds,10:F0}  {slots * options.TickHz,18:F0}  {result.Ticks,5}  {result.UpdateAverageMilliseconds,6:F3} / {result.UpdateMaximumMilliseconds:F2}"
            );

            if (options.Light)
            {
                Console.WriteLine($"         Light after each update avg / max (ms): {result.LightAverageMilliseconds:F3} / {result.LightMaximumMilliseconds:F3}");
            }
        }
    }

    /// <summary>
    ///     Times the three stages of one section on the calling thread, over the generated sections of the columns nearest the centre, so the sample holds the shape's mix of surface and deep sections.
    /// </summary>
    private static void MeasureSectionCost(TerrainGraph graph, SectionPos center, SectionPos[] shape, float skyMinimumY, BenchmarkOptions options)
    {
        var positions = shape
            .Where(position => !IsSky(position, skyMinimumY))
            .OrderBy(position => HorizontalDistanceSquared(position, center))
            .ThenBy(position => position.Y)
            .Take(options.CostSections)
            .ToArray();

        var source = new TerrainGraphSource(graph);
        var content = Memory3D<byte>.Allocate(SectionPos.SectionSize, SectionPos.SectionSize, SectionPos.SectionSize);
        var material = Memory3D<byte>.Allocate(SectionPos.SectionSize, SectionPos.SectionSize, SectionPos.SectionSize);
        var executor = new SectionGenerationExecutor(graph, workerCount: 1, slotCount: 1);

        var bestSampler = double.MaxValue;
        var bestGenerate = double.MaxValue;
        var bestCopy = double.MaxValue;

        // Round 0 is the warmup, which tiered compilation needs before anything is timed:
        for (var round = 0; round <= options.CostRounds; round++)
        {
            var watch = Stopwatch.StartNew();

            foreach (var position in positions)
            {
                var min = position.GetOrigin();
                source.Generate(min, min + SectionPos.SectionMask, content.Span, material.Span);
            }

            var sampler = watch.Elapsed.TotalMilliseconds;

            var generatedArena = new SectionArena();
            var sections = new Section[positions.Length];

            watch.Restart();

            for (var index = 0; index < positions.Length; index++)
            {
                sections[index] = executor.GenerateNow(positions[index], generatedArena).Section;
            }

            var generate = watch.Elapsed.TotalMilliseconds;

            var copyArena = new SectionArena();

            watch.Restart();

            foreach (var section in sections)
            {
                copyArena.CreateCopy(section);
            }

            var copy = watch.Elapsed.TotalMilliseconds;

            if (round == 0)
            {
                continue;
            }

            bestSampler = Math.Min(bestSampler, sampler);
            bestGenerate = Math.Min(bestGenerate, generate);
            bestCopy = Math.Min(bestCopy, copy);
        }

        var count = positions.Length;
        var brickWrite = (bestGenerate - bestSampler) / count;

        Console.WriteLine($"Per section on one thread, best of {options.CostRounds} round(s) over the {count} generated section(s) nearest the centre:");
        Console.WriteLine($"  Sampler:      {bestSampler / count:F3} ms  (TerrainGraphSource.Generate over 32^3)");
        Console.WriteLine($"  GenerateNow:  {bestGenerate / count:F3} ms  (the sampler, then the content sides scan and the brick write into an arena)");
        Console.WriteLine(
            brickWrite > 0.001
                ? $"  Brick write:  {brickWrite:F3} ms  (the difference, the content sides scan included)"
                : "  Brick write:  below the noise of the two timings"
        );
        Console.WriteLine($"  Copy:         {bestCopy / count:F3} ms  (CreateCopy into another arena, which the harvest does on the main thread)");
        Console.WriteLine();
    }

    /// <summary>
    ///     Loads the shape through a level on a running scheduler and times it until every section in the shape is ready.
    ///     A tick rate of 0 updates as fast as the main thread can, and a positive one waits for each tick's deadline.
    /// </summary>
    private static RunResult MeasureFill(
        TerrainGraph graph,
        SectionPos center,
        SectionPos[] shape,
        BenchmarkOptions options,
        int workers,
        int slots,
        double tickHz,
        Action<WorldLevel, LevelTicket>? afterFill)
    {
        // Only the paced runs follow the level with the light engine, since only they have no moves after the fill to record changes nobody takes:
        var withLight = options.Light && tickHz > 0.0;

        // The level is declared last so it is disposed first, while the scheduler still runs the work it waits on:
        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = workers });
        using var level = new WorldLevel(scheduler, graph, slots, options.UnloadsPerUpdate, recordChanges: withLight);
        var light = withLight ? new LightEngine(level, LightRadius(options), LightVerticalRadius(options)) : null;

        scheduler.Start();

        var ticket = level.AddTicket(center, options.Radius, options.VerticalRadius);
        var remaining = new List<SectionPos>(shape);
        var updateMilliseconds = new List<double>();
        var lightMilliseconds = new List<double>();
        var period = tickHz > 0.0 ? 1.0 / tickHz : 0.0;
        var watch = Stopwatch.StartNew();
        var nextTick = 0.0;

        while (true)
        {
            var updateStart = Stopwatch.GetTimestamp();

            level.Update();
            updateMilliseconds.Add(Stopwatch.GetElapsedTime(updateStart).TotalMilliseconds);

            if (light != null)
            {
                var lightStart = Stopwatch.GetTimestamp();

                light.Update(center);
                lightMilliseconds.Add(Stopwatch.GetElapsedTime(lightStart).TotalMilliseconds);
            }

            RemoveReady(level, remaining);

            if (remaining.Count == 0)
            {
                break;
            }

            if (watch.Elapsed.TotalSeconds > options.TimeoutSeconds)
            {
                throw new TimeoutException($"{remaining.Count} section(s) were still not loaded after {options.TimeoutSeconds} s.");
            }

            if (period > 0.0)
            {
                // A tick that overran starts the next one immediately instead of piling up:
                nextTick = Math.Max(nextTick + period, watch.Elapsed.TotalSeconds);
                WaitUntil(watch, nextTick);
            }
            else
            {
                Thread.Yield();
            }
        }

        var seconds = watch.Elapsed.TotalSeconds;

        afterFill?.Invoke(level, ticket);

        return new RunResult
        {
            Workers = workers,
            Slots = slots,
            Seconds = seconds,
            Ticks = updateMilliseconds.Count,
            UpdateAverageMilliseconds = updateMilliseconds.Average(),
            UpdateMaximumMilliseconds = updateMilliseconds.Max(),
            LightAverageMilliseconds = lightMilliseconds.Count > 0 ? lightMilliseconds.Average() : 0.0,
            LightMaximumMilliseconds = lightMilliseconds.Count > 0 ? lightMilliseconds.Max() : 0.0
        };
    }

    /// <summary>
    ///     Classifies every loaded section and counts its dense bricks, then projects the view radius at which each voxel page size fills the arena.
    /// </summary>
    private static void MeasureStorage(WorldLevel level, SectionPos[] shape, int columns)
    {
        var kinds = Enum.GetValues<SectionKind>();
        var sections = new long[kinds.Length];
        var contentDense = new long[kinds.Length];
        var materialDense = new long[kinds.Length];
        var surfaceMaximum = 0;
        var explicitSections = 0L;

        foreach (var position in shape)
        {
            if (!level.TryGetSection(position, out var section))
            {
                throw new InvalidOperationException($"Section {position.X}, {position.Y}, {position.Z} is not loaded.");
            }

            if (section.Type == SectionType.Explicit)
            {
                explicitSections++;
            }

            var content = CountDense(section, VoxelChannel.Content);
            var material = CountDense(section, VoxelChannel.Material);
            var kind = (int)Classify(section, content);

            sections[kind]++;
            contentDense[kind] += content;
            materialDense[kind] += material;

            if (kind == (int)SectionKind.Surface)
            {
                surfaceMaximum = Math.Max(surfaceMaximum, content + material);
            }
        }

        var totalSections = sections.Sum();
        var totalDense = contentDense.Sum() + materialDense.Sum();
        var densePerColumn = (double)totalDense / columns;
        var descriptorBytes = explicitSections * DescriptorBytesPerSection;

        Console.WriteLine("Storage after the fill:");
        Console.WriteLine("  Kind      Sections  Content dense  Material dense  Dense per section");

        foreach (var kind in kinds)
        {
            var index = (int)kind;
            var perSection = sections[index] > 0 ? (double)(contentDense[index] + materialDense[index]) / sections[index] : 0.0;

            Console.WriteLine($"  {kind,-8}  {sections[index],8}  {contentDense[index],13}  {materialDense[index],14}  {perSection,17:F1}");
        }

        Console.WriteLine($"  Descriptors:  {Mib(descriptorBytes):F1} MiB for {explicitSections} explicit section(s), and the {totalSections - explicitSections} shared section(s) hold none of their own");
        Console.WriteLine($"  Dense bricks: {totalDense} ({Mib(totalDense * Section.VoxelsPerBrick):F1} MiB), a surface section holds at most {surfaceMaximum}");
        Console.WriteLine($"  Per column:   {densePerColumn:F0} dense brick(s) and {descriptorBytes / 1024.0 / columns:F0} KiB of descriptors");
        Console.WriteLine();
        Console.WriteLine($"The arena addresses {PageNumbers} voxel pages. The radius at which it fills, for this vertical radius:");

        foreach (var pageMib in new[] { 4, 8, 16, 32 })
        {
            var capacity = (long)PageNumbers * pageMib * 1024 * 1024 / Section.VoxelsPerBrick;
            var radius = Math.Sqrt(capacity / densePerColumn / Math.PI);

            Console.WriteLine($"  {pageMib,2} MiB pages: {capacity / 1_000_000.0:F1} M dense bricks ({PageNumbers * pageMib} MiB), full at a radius of {radius:F0} sections ({radius * SectionMetres:F0} m)");
        }

        Console.WriteLine();
    }

    /// <summary>
    ///     Walks the ticket one section at a time along X and then up, timing the main thread's share: the move itself and the update after it.
    ///     Then jumps far away, removes the ticket so nothing new loads, and times the updates that drain the unload backlog, which is what the unload budget is calibrated from.
    /// </summary>
    private static void MeasureMoves(WorldLevel level, LevelTicket ticket, BenchmarkOptions options)
    {
        var start = ticket.Center;
        var (horizontalMoves, horizontalUpdates) = MeasureSteps(level, ticket, options.Moves, step => new SectionPos(start.X + step + 1, start.Y, start.Z));

        var turn = ticket.Center;
        var (verticalMoves, verticalUpdates) = MeasureSteps(level, ticket, 4, step => new SectionPos(turn.X, turn.Y + step + 1, turn.Z));

        // The steps outrun generation, so the shape is loaded first and the teleport releases all of it:
        UpdateUntilSettled(level, options);

        var teleportStart = Stopwatch.GetTimestamp();

        level.MoveTicket(ticket, new SectionPos(start.X + 10_000, start.Y, start.Z + 10_000));

        var teleport = Stopwatch.GetElapsedTime(teleportStart).TotalMilliseconds;

        level.RemoveTicket(ticket);

        var backlog = level.UnloadQueueCount;
        var drainMilliseconds = new List<double>();

        while (level.UnloadQueueCount > 0)
        {
            var updateStart = Stopwatch.GetTimestamp();

            level.Update();
            drainMilliseconds.Add(Stopwatch.GetElapsedTime(updateStart).TotalMilliseconds);
        }

        var drainTotal = drainMilliseconds.Sum();

        Console.WriteLine($"Ticket moves on the main thread, {options.Moves} step(s) of one section along X from the filled shape, then 4 up:");
        Console.WriteLine($"  MoveTicket along X:  {Describe(horizontalMoves)}");
        Console.WriteLine($"  Update after it:     {Describe(horizontalUpdates)}  (unloads, harvests, rebuilds the load queue and publishes)");
        Console.WriteLine($"  MoveTicket up:       {Describe(verticalMoves)}");
        Console.WriteLine($"  Update after it:     {Describe(verticalUpdates)}");
        Console.WriteLine($"  Teleport 10000 along X and Z: {teleport:F3} ms");
        Console.WriteLine(
            $"  Unload drain at {options.UnloadsPerUpdate} per update: {backlog} section(s) in {drainMilliseconds.Count} update(s), median {drainMilliseconds.Order().ElementAt(drainMilliseconds.Count / 2):F3} ms, max {drainMilliseconds.Max():F3} ms, {1000.0 * drainTotal / backlog:F2} us per section"
        );
        Console.WriteLine();
    }

    /// <summary>
    ///     Updates until nothing is waiting for generation or generating.
    /// </summary>
    private static void UpdateUntilSettled(WorldLevel level, BenchmarkOptions options)
    {
        var watch = Stopwatch.StartNew();

        while (true)
        {
            level.Update();

            var stats = level.GetStats();

            if (stats.PendingSections == 0 && stats.GeneratingSections == 0)
            {
                return;
            }

            if (watch.Elapsed.TotalSeconds > options.TimeoutSeconds)
            {
                throw new TimeoutException($"{stats.PendingSections} section(s) were still pending after {options.TimeoutSeconds} s.");
            }

            Thread.Yield();
        }
    }

    private static (double[] Moves, double[] Updates) MeasureSteps(WorldLevel level, LevelTicket ticket, int count, Func<int, SectionPos> target)
    {
        var moveMilliseconds = new double[count];
        var updateMilliseconds = new double[count];

        for (var step = 0; step < count; step++)
        {
            var moveStart = Stopwatch.GetTimestamp();

            level.MoveTicket(ticket, target(step));
            moveMilliseconds[step] = Stopwatch.GetElapsedTime(moveStart).TotalMilliseconds;

            var updateStart = Stopwatch.GetTimestamp();

            level.Update();
            updateMilliseconds[step] = Stopwatch.GetElapsedTime(updateStart).TotalMilliseconds;
        }

        return (moveMilliseconds, updateMilliseconds);
    }

    /// <summary>
    ///     The first sample apart, since it carries the first call's compilation, then the median and the maximum of the rest.
    /// </summary>
    private static string Describe(double[] milliseconds)
    {
        var rest = milliseconds.Skip(1).Order().ToArray();

        if (rest.Length == 0)
        {
            return $"{milliseconds[0]:F3} ms";
        }

        return $"first {milliseconds[0]:F3} ms, then median {rest[rest.Length / 2]:F3} ms, max {rest[^1]:F3} ms";
    }

    private static SectionKind Classify(Section section, int contentDense)
    {
        if (contentDense > 0)
        {
            return SectionKind.Surface;
        }

        var air = true;
        var solid = true;

        for (var brick = 0; brick < Section.Bricks; brick++)
        {
            var value = section.GetUniformValue(VoxelChannel.Content, brick);

            air &= value == 0;
            solid &= value > WorldConstants.ContentIsoLevel;
        }

        // A section whose uniform bricks are partly air and partly solid still holds the surface on a brick boundary:
        return air ? SectionKind.Air : solid ? SectionKind.Solid : SectionKind.Surface;
    }

    private static int CountDense(Section section, VoxelChannel channel)
    {
        var count = 0;

        for (var brick = 0; brick < Section.Bricks; brick++)
        {
            if (section.GetBrickType(channel, brick) == BrickType.Dense)
            {
                count++;
            }
        }

        return count;
    }

    private static void RemoveReady(WorldLevel level, List<SectionPos> remaining)
    {
        for (var index = remaining.Count - 1; index >= 0; index--)
        {
            if (level.GetStatus(remaining[index]) != SectionStatus.Ready)
            {
                continue;
            }

            remaining[index] = remaining[^1];
            remaining.RemoveAt(remaining.Count - 1);
        }
    }

    /// <summary>
    ///     Yields until the deadline, since a one millisecond sleep can last a whole timer tick on Windows.
    /// </summary>
    private static void WaitUntil(Stopwatch watch, double seconds)
    {
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            Thread.Yield();
        }
    }

    /// <summary>
    ///     The sections of a ticket's load shape, which is the vertical cylinder <see cref="WorldLevel"/> loads.
    /// </summary>
    internal static SectionPos[] GetLoadShape(SectionPos center, int horizontalRadius, int verticalRadius, out int columns)
    {
        var positions = new List<SectionPos>();

        columns = 0;

        for (var z = -horizontalRadius; z <= horizontalRadius; z++)
        {
            for (var x = -horizontalRadius; x <= horizontalRadius; x++)
            {
                if (x * x + z * z > horizontalRadius * horizontalRadius)
                {
                    continue;
                }

                columns++;

                for (var y = -verticalRadius; y <= verticalRadius; y++)
                {
                    positions.Add(new SectionPos(center.X + x, center.Y + y, center.Z + z));
                }
            }
        }

        return positions.ToArray();
    }

    /// <summary>
    ///     The same test <see cref="WorldLevel"/> uses to write a section as air without a job.
    /// </summary>
    private static bool IsSky(SectionPos position, float skyMinimumY)
    {
        return SectionPos.SectionToVoxelCoord(position.Y) * WorldConstants.VoxelSize >= skyMinimumY;
    }

    internal static SectionPos SurfaceSection(TerrainGraph graph, int sectionX, int sectionZ)
    {
        var x = SectionPos.SectionToVoxelCoord(sectionX) + SectionPos.SectionSize / 2;
        var z = SectionPos.SectionToVoxelCoord(sectionZ) + SectionPos.SectionSize / 2;
        var surfaceY = (int)MathF.Floor(new TerrainGraphSource(graph).Elevation(x, z) / WorldConstants.VoxelSize);

        return new SectionPos(sectionX, SectionPos.VoxelToSectionCoord(surfaceY), sectionZ);
    }

    private static long HorizontalDistanceSquared(SectionPos position, SectionPos center)
    {
        long deltaX = position.X - center.X;
        long deltaZ = position.Z - center.Z;

        return deltaX * deltaX + deltaZ * deltaZ;
    }

    private static double Mib(long bytes)
    {
        return bytes / (1024.0 * 1024.0);
    }
}

internal sealed class BenchmarkOptions
{
    public int Seed = 3141;
    public int Radius = 8;
    public int VerticalRadius = 4;
    public int[] Workers = DefaultWorkers();
    public int[] PacedSlots = [10, 20, 40];
    public double TickHz = 30.0;
    public int CostSections = 64;
    public int CostRounds = 3;
    public int Moves = 32;
    public int UnloadsPerUpdate = WorldLevel.DefaultUnloadsPerUpdate;
    public double TimeoutSeconds = 300.0;
    public bool Residency;
    public bool Light;
    public bool LoadQueue;
    public int Machines = 100;
    public int MachineRadius = 1;
    public int MachineVerticalRadius = 1;

    public static BenchmarkOptions Parse(string[] args)
    {
        var options = new BenchmarkOptions();

        for (var index = 0; index < args.Length; index++)
        {
            var name = args[index];
            var hasValue = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal);
            var value = hasValue ? args[index + 1] : "";

            switch (name)
            {
                case "--seed":
                    options.Seed = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--radius":
                    options.Radius = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--vertical-radius":
                    options.VerticalRadius = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--workers":
                    options.Workers = ParseList(value);
                    break;
                case "--slots":
                    options.PacedSlots = ParseList(value);
                    break;
                case "--tick-hz":
                    options.TickHz = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--cost-sections":
                    options.CostSections = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--cost-rounds":
                    options.CostRounds = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--moves":
                    options.Moves = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--unloads":
                    options.UnloadsPerUpdate = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--timeout":
                    options.TimeoutSeconds = double.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--residency":
                    options.Residency = true;
                    hasValue = false;
                    break;
                case "--light":
                    options.Light = true;
                    hasValue = false;
                    break;
                case "--load-queue":
                    options.LoadQueue = true;
                    hasValue = false;
                    break;
                case "--machines":
                    options.Machines = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--machine-radius":
                    options.MachineRadius = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--machine-vertical-radius":
                    options.MachineVerticalRadius = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{name}'. Options: --seed, --radius, --vertical-radius, --workers 1,2,4, --slots 10,20, --tick-hz, --cost-sections, --cost-rounds, --moves, --unloads, --timeout, --residency, --light (the residency and paced runs), --load-queue, --machines, --machine-radius, --machine-vertical-radius.");
            }

            if (hasValue)
            {
                index++;
            }
        }

        return options;
    }

    /// <summary>
    ///     Doubling worker counts up to one less than the core count, so the main thread keeps a core of its own.
    /// </summary>
    private static int[] DefaultWorkers()
    {
        var maximum = Math.Max(1, Environment.ProcessorCount - 1);
        var workers = new List<int>();

        for (var count = 1; count < maximum; count *= 2)
        {
            workers.Add(count);
        }

        workers.Add(maximum);

        return workers.ToArray();
    }

    private static int[] ParseList(string value)
    {
        return value.Split(',').Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
    }
}
