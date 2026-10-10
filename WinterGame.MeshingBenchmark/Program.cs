using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.MeshingBenchmark.Reference;
using WinterGame.World;
using WinterGame.World.Generation;
using WinterGame.World.Mesher;

// ReSharper disable FieldCanBeMadeReadOnly.Global

Debug.Fail("Run the meshing benchmark in Release");

var options = BenchmarkOptions.Parse(args);

Console.WriteLine("Multi-label surface nets meshing benchmark");
Console.WriteLine($"  Runtime:    {RuntimeInformation.FrameworkDescription}, {(GCSettings.IsServerGC ? "server" : "workstation")} GC, {Environment.ProcessorCount} core(s)");
Console.WriteLine($"  Sections:   {options.SectionsX} x {options.SectionsY} x {options.SectionsZ} = {options.SectionsX * options.SectionsY * options.SectionsZ}");
Console.WriteLine($"  Cells:      {options.ChunkCells}^3, fairing {options.FairingIterations} iteration(s), step {options.FairingStep}, range {options.FairingRange} voxel(s)");
Console.WriteLine($"  Cut:        {(options.CutEnabled ? $"{options.CutWidth} x {options.CutDepth} m to {options.CutFloorY} m" : "off")}");
Console.WriteLine($"  Timer:      {options.Warmup} warmup + {options.Iterations} timed iteration(s), best of the timed set");
Console.WriteLine($"  Verify:     {(options.Verify ? "byte-compared against the frozen mesher" : "off")}");
Console.WriteLine($"  GC:         {(options.NoGc ? $"critical region, {options.GcBudgetMib} MiB" : "enabled")}");
Console.WriteLine();

Benchmark.Run(options);

internal static class Benchmark
{
    private sealed class SectionFixture
    {
        public required Memory3D<byte> Content { get; init; }
        public required Memory3D<byte> Material { get; init; }
        public required Vector3i LocalCellMin { get; init; }
    }

    public static void Run(BenchmarkOptions options)
    {
        var cellCount = new Vector3i(options.ChunkCells, options.ChunkCells, options.ChunkCells);
        var relaxHalo = Math.Clamp(options.FairingIterations, 0, 8);
        var halo = new Vector3i(relaxHalo);
        var mesherState = new MultiLabelSurfaceNetsStateStorage(cellCount, relaxHalo);

        var config = new TerrainConfig
        {
            BaseHeight = options.BaseHeight,
            ContinentalAmplitude = options.ContinentalAmplitude,
            HillAmplitude = options.HillAmplitude,
            MountainAmplitude = options.MountainAmplitude,
            ProvinceWavelength = options.ProvinceWavelength,
            FoldAmplitude = options.FoldAmplitude,
            FoldWavelength = options.FoldWavelength,
            SnowThickness = options.SnowThickness,
            IceThickness = options.IceThickness,
            Provinces = new StrataSet(options.ProvinceHalfWidth, TerrainStrata.Tables)
        };

        var generator = new TerrainGraphSource(options.Seed, config);

        // The column is centred on the surface at the world center, exactly as WorldLayer places it:
        var centreX = options.SectionsX * options.ChunkCells / 2;
        var centreZ = options.SectionsZ * options.ChunkCells / 2;
        var centreHeight = generator.Elevation(centreX, centreZ);
        var originY = (int)MathF.Floor(centreHeight / WorldConstants.VoxelSize) - options.SectionsY * options.ChunkCells / 2;

        var cutMaxX = options.CutEnabled ? (int)(options.CutWidth / WorldConstants.VoxelSize) : 0;
        var cutMaxZ = options.CutEnabled ? (int)(options.CutDepth / WorldConstants.VoxelSize) : 0;
        var cutMinY = options.CutEnabled ? (int)MathF.Floor(options.CutFloorY / WorldConstants.VoxelSize) : 0;

        var sectionCount = options.SectionsX * options.SectionsY * options.SectionsZ;
        var fixtures = new SectionFixture[sectionCount];

        var buildWatch = Stopwatch.StartNew();
        var fixtureIndex = 0;

        for (var sectionY = 0; sectionY < options.SectionsY; sectionY++)
        {
            for (var sectionZ = 0; sectionZ < options.SectionsZ; sectionZ++)
            {
                for (var sectionX = 0; sectionX < options.SectionsX; sectionX++)
                {
                    var cellMin = new Vector3i(sectionX * options.ChunkCells, originY + sectionY * options.ChunkCells, sectionZ * options.ChunkCells);
                    var box = SurfaceNetsMesher.GetSampleRange(cellMin - halo, cellCount + halo + halo);
                    var localCellMin = cellMin - box.Min;

                    var content = Memory3D<byte>.Allocate(
                        box.Max.X - box.Min.X + 1,
                        box.Max.Y - box.Min.Y + 1,
                        box.Max.Z - box.Min.Z + 1
                    );

                    var material = Memory3D<byte>.Allocate(content.SizeX, content.SizeY, content.SizeZ);

                    generator.Generate(box.Min, box.Max, content.Span, material.Span);

                    if (options.CutEnabled)
                    {
                        ClearCut(content.Span, material.Span, box.Min, cutMaxX, cutMaxZ, cutMinY);
                    }

                    fixtures[fixtureIndex++] = new SectionFixture
                    {
                        Content = content,
                        Material = material,
                        LocalCellMin = localCellMin
                    };
                }
            }
        }

        buildWatch.Stop();

        Console.WriteLine($"World built in {buildWatch.Elapsed.TotalMilliseconds:F0} ms ({sectionCount} sections)");

        ReferenceSection[]? reference = null;

        if (options.Verify)
        {
            reference = BuildReference(fixtures, options, cellCount, relaxHalo);
        }

        for (var warmup = 0; warmup < options.Warmup; warmup++)
        {
            MeshAll(fixtures, options, mesherState, cellCount, relaxHalo, out _, out _, out _, out _, out _);
        }

        if (reference != null)
        {
            if (!Verify(fixtures, reference, options, mesherState, cellCount, relaxHalo))
            {
                Environment.ExitCode = 1;
            }
        }

        var milliseconds = new double[options.Iterations];
        var runVertices = new long[options.Iterations];
        var runIndices = new long[options.Iterations];
        var runDrawn = new int[options.Iterations];
        var runAllocatedBytes = new long[options.Iterations];

        long expectedVertexChecksum = 0;
        long expectedIndexChecksum = 0;

        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);
        var noGcStarted = false;

        if (options.NoGc)
        {
            // Start from a clean heap, then suppress collection for the whole timed loop.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            noGcStarted = GC.TryStartNoGCRegion(options.GcBudgetMib * 1024L * 1024L);

            if (!noGcStarted)
            {
                Console.WriteLine($"Warning: a no-GC region of {options.GcBudgetMib} MiB is not available, so the timed loop runs with GC enabled.");
            }

            // Entering the region performs a full collection, so the loop delta is measured from here.
            gen0Before = GC.CollectionCount(0);
            gen1Before = GC.CollectionCount(1);
            gen2Before = GC.CollectionCount(2);
        }

        for (var iteration = 0; iteration < options.Iterations; iteration++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();

            MeshAll(fixtures, options, mesherState, cellCount, relaxHalo, out var vertexTotal, out var indexTotal, out var vertexChecksum, out var indexChecksum, out var drawn);

            watch.Stop();

            milliseconds[iteration] = watch.Elapsed.TotalMilliseconds;
            runVertices[iteration] = vertexTotal;
            runIndices[iteration] = indexTotal;
            runDrawn[iteration] = drawn;
            runAllocatedBytes[iteration] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            if (iteration == 0)
            {
                expectedVertexChecksum = vertexChecksum;
                expectedIndexChecksum = indexChecksum;
            }
            else if (vertexChecksum != expectedVertexChecksum || indexChecksum != expectedIndexChecksum)
            {
                Console.WriteLine($"WARNING: geometry changed on iteration {iteration}, so the runs are not comparable.");
            }
        }

        if (noGcStarted)
        {
            try
            {
                GC.EndNoGCRegion();
            }
            catch (InvalidOperationException)
            {
                // The runtime ends the region on its own once the budget is exceeded;
                // EndNoGCRegion then throws and the GC counters below carry the verdict.
            }
        }

        var gen0 = GC.CollectionCount(0) - gen0Before;
        var gen1 = GC.CollectionCount(1) - gen1Before;
        var gen2 = GC.CollectionCount(2) - gen2Before;

        var bestIteration = 0;

        for (var iteration = 1; iteration < options.Iterations; iteration++)
        {
            if (milliseconds[iteration] < milliseconds[bestIteration])
            {
                bestIteration = iteration;
            }
        }

        var total = 0.0;
        var worst = milliseconds[0];

        foreach (var sample in milliseconds)
        {
            total += sample;

            if (sample > worst)
            {
                worst = sample;
            }
        }

        var average = total / options.Iterations;
        var best = milliseconds[bestIteration];
        var allocatedPerIteration = runAllocatedBytes[bestIteration];

        Console.WriteLine();
        Console.WriteLine($"Best:    {best:F2} ms  ({best / sectionCount:F3} ms/section over {sectionCount}, {best / runDrawn[bestIteration]:F3} ms/section over {runDrawn[bestIteration]} drawn)");
        Console.WriteLine($"Average: {average:F2} ms");
        Console.WriteLine($"Worst:   {worst:F2} ms");
        Console.WriteLine($"Alloc:   {allocatedPerIteration / (1024.0 * 1024.0):F2} MiB/iteration, {allocatedPerIteration / (double)sectionCount:F0} bytes/section");

        if (options.NoGc)
        {
            var held = noGcStarted && gen0 + gen1 + gen2 == 0;
            Console.WriteLine($"NoGC:    {(held ? $"region held, {options.GcBudgetMib} MiB was enough" : "region exceeded, raise --gc-budget-mib")}, gen0 +{gen0}, gen1 +{gen1}, gen2 +{gen2}");
        }
        else
        {
            Console.WriteLine($"GC:      gen0 +{gen0}, gen1 +{gen1}, gen2 +{gen2}");
        }

        Console.WriteLine($"Geometry: {runVertices[bestIteration]} verts, {runIndices[bestIteration]} indices in {runDrawn[bestIteration]} drawn section(s)");
    }

    private static void MeshAll(
        SectionFixture[] fixtures,
        BenchmarkOptions options,
        MultiLabelSurfaceNetsStateStorage mesherState,
        Vector3i cellCount,
        int relaxHalo,
        out long vertexTotal,
        out long indexTotal,
        out long vertexChecksum,
        out long indexChecksum,
        out int drawn
    )
    {
        long vertexTotalOut = 0;
        long indexTotalOut = 0;
        long vertexHash = 0;
        long indexHash = 0;
        var drawnCount = 0;

        foreach (var fixture in fixtures)
        {
            // WorldLayer allocates both lists and the description per section, so the baseline includes that:
            var vertices = new List<VoxelVertex>();
            var indices = new List<int>();

            MeshCurrent(mesherState, fixture, options, cellCount, relaxHalo, vertices, indices);

            if (indices.Count == 0)
            {
                continue;
            }

            drawnCount++;
            vertexTotalOut += vertices.Count;
            indexTotalOut += indices.Count;
            vertexHash = (vertexHash * 31) + vertices.Count;
            indexHash = (indexHash * 31) + indices.Count;
        }

        vertexTotal = vertexTotalOut;
        indexTotal = indexTotalOut;
        vertexChecksum = vertexHash;
        indexChecksum = indexHash;
        drawn = drawnCount;
    }

    /// <summary>
    ///     The frozen reference output of one section, so an optimized mesher can be checked byte for byte.
    /// </summary>
    private sealed class ReferenceSection
    {
        public required byte[] Vertices { get; init; }
        public required byte[] Indices { get; init; }
    }

    private static void MeshCurrent(
        MultiLabelSurfaceNetsStateStorage storage,
        SectionFixture fixture,
        BenchmarkOptions options,
        Vector3i cellCount,
        int relaxHalo,
        List<VoxelVertex> vertices,
        List<int> indices
    )
    {
        MultiLabelSurfaceNets.Mesh(storage, new MultiLabelSurfaceNetsDescription
        {
            Content = fixture.Content,
            Material = fixture.Material,
            IsoLevel = WorldConstants.ContentIsoLevel,
            CellMin = fixture.LocalCellMin,
            CellCount = cellCount,
            Vertices = vertices,
            Indices = indices,
            Fairing = new SurfaceFairingSettings
            {
                AllowedRange = options.FairingRange,
                StepSize = options.FairingStep,
                Iterations = relaxHalo
            }
        });
    }

    /// <summary>
    ///     Runs the frozen mesher over every section once and keeps its raw vertex and index bytes.
    /// </summary>
    private static ReferenceSection[] BuildReference(
        SectionFixture[] fixtures,
        BenchmarkOptions options,
        Vector3i cellCount,
        int relaxHalo
    )
    {
        var reference = new ReferenceSection[fixtures.Length];
        var watch = Stopwatch.StartNew();

        for (var section = 0; section < fixtures.Length; section++)
        {
            var fixture = fixtures[section];
            var vertices = new List<ReferenceVoxelVertex>();
            var indices = new List<int>();

            ReferenceMultiLabelSurfaceNets.Mesh(new ReferenceMultiLabelSurfaceNetsDescription
            {
                Content = fixture.Content,
                Material = fixture.Material,
                IsoLevel = WorldConstants.ContentIsoLevel,
                CellMin = fixture.LocalCellMin,
                CellCount = cellCount,
                Vertices = vertices,
                Indices = indices,
                Fairing = new ReferenceSurfaceFairingSettings
                {
                    AllowedRange = options.FairingRange,
                    StepSize = options.FairingStep,
                    Iterations = relaxHalo
                }
            });

            reference[section] = new ReferenceSection
            {
                Vertices = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(vertices)).ToArray(),
                Indices = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(indices)).ToArray()
            };
        }

        watch.Stop();
        Console.WriteLine($"Reference built in {watch.Elapsed.TotalMilliseconds:F0} ms");

        return reference;
    }

    /// <summary>
    ///     Re-meshes every section with the live mesher and compares the raw bytes against the frozen reference.
    /// </summary>
    private static bool Verify(
        SectionFixture[] fixtures,
        ReferenceSection[] reference,
        BenchmarkOptions options,
        MultiLabelSurfaceNetsStateStorage mesherState,
        Vector3i cellCount,
        int relaxHalo
    )
    {
        var watch = Stopwatch.StartNew();
        var mismatches = 0;

        for (var section = 0; section < fixtures.Length; section++)
        {
            var vertices = new List<VoxelVertex>();
            var indices = new List<int>();

            MeshCurrent(mesherState, fixtures[section], options, cellCount, relaxHalo, vertices, indices);

            var vertexBytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(vertices));
            var indexBytes = MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(indices));

            if (vertexBytes.SequenceEqual(reference[section].Vertices) && indexBytes.SequenceEqual(reference[section].Indices))
            {
                continue;
            }

            mismatches++;

            if (mismatches <= 10)
            {
                Console.WriteLine(
                    $"  section {section}: reference {reference[section].Vertices.Length} vertex bytes / {reference[section].Indices.Length} index bytes, " +
                    $"current {vertexBytes.Length} vertex bytes / {indexBytes.Length} index bytes"
                );
            }
        }

        watch.Stop();

        if (mismatches == 0)
        {
            Console.WriteLine($"Validation: {fixtures.Length} of {fixtures.Length} section(s) byte-identical to the reference ({watch.Elapsed.TotalMilliseconds:F0} ms)");
            return true;
        }

        Console.WriteLine($"Validation: FAILED, {mismatches} of {fixtures.Length} section(s) differ from the reference");
        return false;
    }

    /// <summary>
    ///     Clears a notch out of the near corner of the world, copied from <c>WorldLayer.ClearCut</c>.
    /// </summary>
    private static void ClearCut(Span3D<byte> content, Span3D<byte> material, Vector3i boxMin, int cutMaxX, int cutMaxZ, int cutMinY)
    {
        for (var z = 0; z < content.SizeZ; z++)
        {
            var worldZ = boxMin.Z + z;

            if (worldZ >= cutMaxZ)
            {
                continue;
            }

            for (var y = 0; y < content.SizeY; y++)
            {
                var worldY = boxMin.Y + y;

                if (worldY <= cutMinY)
                {
                    continue;
                }

                for (var x = 0; x < content.SizeX; x++)
                {
                    if (boxMin.X + x >= cutMaxX)
                    {
                        continue;
                    }

                    content[x, y, z] = 0;
                    material[x, y, z] = SurfaceMaterial.Null;
                }
            }
        }
    }
}

internal sealed class BenchmarkOptions
{
    public int Seed = 3141;
    public int ChunkCells = 32;
    public int SectionsX = 4;
    public int SectionsY = 10;
    public int SectionsZ = 4;

    public float BaseHeight = 4.0f;
    public float ContinentalAmplitude = 8.0f;
    public float HillAmplitude = 1.5f;
    public float MountainAmplitude = 12.0f;
    public float ProvinceWavelength = 24.0f;
    public float ProvinceHalfWidth = 0.3f;
    public float FoldAmplitude = 4.0f;
    public float FoldWavelength = 48.0f;
    public float SnowThickness = 0.35f;
    public float IceThickness = 0.65f;

    public int FairingIterations = 4;
    public float FairingStep = 0.5f;
    public float FairingRange = 0.75f;

    public bool CutEnabled = true;
    public float CutWidth = 24.0f;
    public float CutDepth = 12.0f;
    public float CutFloorY = -34.0f;

    public int Iterations = 100;
    public int Warmup = 3;
    public bool Verify = true;
    public bool NoGc;
    public int GcBudgetMib = 1024;

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
                case "--chunk-cells":
                    options.ChunkCells = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--sections":
                {
                    var parts = value.Split('x', 'X', ',');
                    options.SectionsX = int.Parse(parts[0], CultureInfo.InvariantCulture);
                    options.SectionsY = int.Parse(parts[1], CultureInfo.InvariantCulture);
                    options.SectionsZ = int.Parse(parts[2], CultureInfo.InvariantCulture);
                    break;
                }
                case "--iterations":
                    options.Iterations = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--warmup":
                    options.Warmup = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--fairing-iterations":
                    options.FairingIterations = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--fairing-step":
                    options.FairingStep = float.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--fairing-range":
                    options.FairingRange = float.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "--no-cut":
                    options.CutEnabled = false;
                    break;
                case "--no-verify":
                    options.Verify = false;
                    break;
                case "--no-gc":
                    options.NoGc = true;
                    break;
                case "--gc-budget-mib":
                    options.GcBudgetMib = int.Parse(value, CultureInfo.InvariantCulture);
                    options.NoGc = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{name}'. Options: --seed, --chunk-cells, --sections XxYxZ, --fairing-iterations, --fairing-step, --fairing-range, --iterations, --warmup, --no-cut, --no-verify, --no-gc, --gc-budget-mib.");
            }

            if (hasValue)
            {
                index++;
            }
        }

        return options;
    }
}
