using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.Jobs;
using WinterGame.Utilities;
using WinterGame.World;
using WinterGame.World.Generation;
using WinterGame.World.Level;
using WinterGame.World.Level.Meshing;
using WinterGame.World.Mesher;
using WinterGame.World.Storage;

namespace WinterGame.Tests;

public class SectionMeshingTests
{
    private const int HorizontalRadius = 3;
    private const int VerticalRadius = 1;

    private static readonly SectionMeshingDescription Description = new();

    [Fact]
    public void Executor_MeshesAWindowExactlyAsMeshingTheGeneratorDirectly()
    {
        WgAssert.InitGameThread();
        WgAssert.InitRenderThread();

        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        var executor = new SectionMeshExecutor(Description, scheduler.ThreadCount);
        var providerId = scheduler.RegisterExecutor(executor);
        var source = new TerrainGraphSource(LevelTests.Graph);
        var references = new ReferenceMesher();
        var surface = LevelTests.SurfaceSection(0, 0);
        var sky = surface with { Y = LevelTests.LowestSkySectionY() };

        // The surface section comes back after another, in the slot that one used, so a freed slot has to start empty:
        foreach (var position in new[] { surface, surface with { X = surface.X + 1 }, surface, surface with { Y = surface.Y - 1 }, sky })
        {
            var slot = executor.Allocate(position);
            var min = executor.GetWindowMin(position);

            source.Generate(min, min + executor.WindowSize - 1, executor.GetContentWindow(slot), executor.GetMaterialWindow(slot));

            var job = scheduler.Pool.CreateJob();

            Assert.True(scheduler.Publish(job, providerId, slot));
            scheduler.Drain();
            Assert.True(scheduler.Pool.TryComplete(job));

            var result = executor.GetResult(slot);

            Assert.Equal(position, result.Position);
            AssertSameMesh(references.Get(position), result);

            scheduler.Pool.Release(job);
            executor.Free(slot);
        }

        Assert.NotEmpty(references.Get(surface).Indices);
        Assert.Empty(references.Get(sky).Indices);
        Assert.Equal(Description.SlotCount, executor.FreeSlotCount);
    }

    [Fact]
    public void Executor_RejectsAFairingThatReadsPastTheNeighbors()
    {
        var description = new SectionMeshingDescription
        {
            Fairing = new SurfaceFairingSettings { AllowedRange = 0.5f, StepSize = 0.5f, Iterations = 31 }
        };

        Assert.Throws<ArgumentException>(() => new SectionMeshExecutor(description, workerCount: 1));
    }

    [Fact]
    public void PublisherAndReceiver_MeshEverySectionWithASurfaceOnceItAndItsNeighborsAreReady()
    {
        WgAssert.InitGameThread();
        WgAssert.InitRenderThread();

        var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = Math.Max(2, Environment.ProcessorCount - 1) });
        var level = new WorldLevel(scheduler, LevelTests.Graph, slotCount: 32, unloadsPerUpdate: 8);
        var channel = new SectionMeshChannel();
        var publisher = new SectionMeshPublisher(scheduler, Description, channel);
        var receiver = new SectionMeshReceiver(channel);
        var sink = new RecordingSink(new ReferenceMesher());
        var region = new HashSet<SectionPos>();

        scheduler.Start();

        try
        {
            var center = LevelTests.SurfaceSection(0, 0);
            var ticket = level.AddTicket(center, HorizontalRadius, VerticalRadius);

            // Short steps, then a jump the shapes cannot overlap across and the jump back, which loads and meshes the first sections again:
            foreach (var (columnX, columnZ) in new[] { (0, 0), (1, 0), (2, 1), (12, 0), (0, 0) })
            {
                center = LevelTests.SurfaceSection(columnX, columnZ);
                level.MoveTicket(ticket, center);
                AddRegion(region, center);
                UpdateUntilSettled(scheduler, level, publisher, receiver, sink, center);
                AssertMeshedMatchesLevel(level, sink, region, $"At ({columnX}, {columnZ})");
            }

            level.RemoveTicket(ticket);
            UpdateUntilSettled(scheduler, level, publisher, receiver, sink, center);

            Assert.Empty(sink.Meshed);
            Assert.True(sink.SetCount > sink.DistinctCount, "No section was meshed again after it was loaded again.");
            Assert.Equal(Description.SlotCount, publisher.FreeSlotCount);
        }
        finally
        {
            level.Dispose();
            scheduler.Dispose();
        }
    }

    [Fact]
    public void Retire_WhileTheJobIsInFlight_DropsTheResultAndFreesTheSlot()
    {
        WgAssert.InitGameThread();
        WgAssert.InitRenderThread();

        var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 2 });
        var level = new WorldLevel(scheduler, LevelTests.Graph, slotCount: 32);
        var channel = new SectionMeshChannel();
        var publisher = new SectionMeshPublisher(scheduler, Description, channel);
        var receiver = new SectionMeshReceiver(channel);
        var sink = new RecordingSink(new ReferenceMesher());

        try
        {
            var center = LevelTests.SurfaceSection(0, 0);
            var ticket = level.AddTicket(center, HorizontalRadius, VerticalRadius);

            UpdateLevelUntilSettled(scheduler, level);
            publisher.Update(level, center);
            receiver.Update(sink);

            var published = receiver.InFlightCount;

            Assert.True(published > 0, "Nothing was published.");
            Assert.Equal(Description.SlotCount - published, publisher.FreeSlotCount);

            // The scheduler never started, so the jobs stay in flight while every section unloads:
            level.RemoveTicket(ticket);

            do
            {
                level.Update();
                publisher.Update(level, center);
                receiver.Update(sink);
            }
            while (level.UnloadQueueCount > 0);

            Assert.Equal(published, receiver.InFlightCount);

            scheduler.Drain();
            receiver.Update(sink);

            Assert.Equal(0, receiver.InFlightCount);
            Assert.Equal(0, sink.SetCount);
            Assert.Equal(0, publisher.QueuedCount);
            Assert.Equal(Description.SlotCount, publisher.FreeSlotCount);
            Assert.Equal(0, level.SectionEntryCount);
        }
        finally
        {
            scheduler.Drain();
            level.Dispose();
            scheduler.Dispose();
        }
    }

    [Fact]
    public void Section_WhoseNeighborUnloadsBeforeItsWindowIsRead_IsMeshedOnceTheNeighborIsBack()
    {
        WgAssert.InitGameThread();
        WgAssert.InitRenderThread();

        var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = Math.Max(2, Environment.ProcessorCount - 1) });
        var level = new WorldLevel(scheduler, LevelTests.Graph, slotCount: 32);
        var channel = new SectionMeshChannel();
        var publisher = new SectionMeshPublisher(scheduler, Description, channel);
        var receiver = new SectionMeshReceiver(channel);
        var sink = new RecordingSink(new ReferenceMesher());
        var region = new HashSet<SectionPos>();

        scheduler.Start();

        try
        {
            var center = LevelTests.SurfaceSection(0, 0);
            var ticket = level.AddTicket(center, HorizontalRadius, VerticalRadius);

            // The level runs alone, so the mesh changes wait for the publisher:
            UpdateLevelUntilSettled(scheduler, level);
            AddRegion(region, center);

            var due = GetSectionsDueAMesh(level, region);

            // Three sections along +X, the -X edge of the old neighborhoods stays loaded in the keep margin while the sections past it leave the keep shape:
            var moved = center with { X = center.X + 3 };

            level.MoveTicket(ticket, moved);
            UpdateLevelUntilSettled(scheduler, level);

            var broken = due.Where(position => level.GetStatus(position) == SectionStatus.Ready && !IsNeighborhoodReady(level, position)).ToList();

            Assert.NotEmpty(broken);

            // The publisher only now takes the changes, finds those windows incomplete and hands the sections back to the level:
            UpdateUntilSettled(scheduler, level, publisher, receiver, sink, moved);

            foreach (var position in broken)
            {
                Assert.DoesNotContain(position, sink.Meshed);
            }

            level.MoveTicket(ticket, center);
            UpdateUntilSettled(scheduler, level, publisher, receiver, sink, center);

            foreach (var position in due)
            {
                Assert.Contains(position, sink.Meshed);
            }
        }
        finally
        {
            level.Dispose();
            scheduler.Dispose();
        }
    }

    [Fact]
    public void Receiver_OnAnotherThreadThanThePublisher_AdoptsEveryJobAndGetsEveryMesh()
    {
        WgAssert.InitRenderThread();

        var channel = new SectionMeshChannel();
        var receiver = new SectionMeshReceiver(channel);
        var sink = new RecordingSink(new ReferenceMesher());
        var settled = 0;
        var stop = 0;
        HashSet<SectionPos>? due = null;
        ExceptionDispatchInfo? gameFault = null;

        // The game side runs on its own thread, the way GameThread runs it, and this thread is the render thread:
        var gameThread = new Thread(() =>
        {
            try
            {
                WgAssert.InitGameThread();

                var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = Math.Max(2, Environment.ProcessorCount - 2) });
                var level = new WorldLevel(scheduler, LevelTests.Graph, slotCount: 32);
                var publisher = new SectionMeshPublisher(scheduler, Description, channel);
                var center = LevelTests.SurfaceSection(0, 0);
                var region = new HashSet<SectionPos>();

                scheduler.Start();
                level.AddTicket(center, HorizontalRadius, VerticalRadius);
                AddRegion(region, center);

                try
                {
                    while (Volatile.Read(ref stop) == 0)
                    {
                        level.Update();
                        publisher.Update(level, center);

                        // Every slot back means the render thread has taken every job sent:
                        var isSettled = level.PendingCount == 0 && level.InFlightCount == 0 && level.NewlyReadyCount == 0 && publisher.QueuedCount == 0 &&
                                        publisher.FreeSlotCount == Description.SlotCount;

                        Volatile.Write(ref settled, isSettled ? 1 : 0);
                        Thread.Sleep(1);
                    }

                    due = GetSectionsDueAMesh(level, region);
                }
                finally
                {
                    level.Dispose();
                    scheduler.Dispose();
                }
            }
            catch (Exception exception)
            {
                gameFault = ExceptionDispatchInfo.Capture(exception);
            }
        });

        gameThread.Start();

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);

            while (Volatile.Read(ref gameFault) == null && (Volatile.Read(ref settled) == 0 || receiver.InFlightCount > 0))
            {
                Assert.True(DateTime.UtcNow < deadline, "The level and its meshes did not settle in time.");

                receiver.Update(sink);
                Thread.Sleep(1);
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            gameThread.Join();
        }

        gameFault?.Throw();

        Assert.NotNull(due);
        Assert.NotEmpty(due);
        Assert.True(due.SetEquals(sink.Meshed), $"Meshed {sink.Meshed.Count} section(s), but {due.Count} were due a mesh.");
    }

    [Fact]
    public void Edits_LeaveEverySectionMeshedFromWhatItsWindowHoldsNow()
    {
        WgAssert.InitGameThread();
        WgAssert.InitRenderThread();

        var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = Math.Max(2, Environment.ProcessorCount - 1) });
        var level = new WorldLevel(scheduler, LevelTests.Graph, slotCount: 32);
        var channel = new SectionMeshChannel();
        var publisher = new SectionMeshPublisher(scheduler, Description, channel);
        var receiver = new SectionMeshReceiver(channel);
        var sink = new LatestMeshSink();
        var region = new HashSet<SectionPos>();
        var random = new Random(5);

        scheduler.Start();

        try
        {
            var center = LevelTests.SurfaceSection(0, 0);

            level.AddTicket(center, HorizontalRadius, VerticalRadius);
            AddRegion(region, center);
            UpdateUntilSettled(scheduler, level, publisher, receiver, sink, center);
            AssertMeshesMatchWindows(level, sink, region, "Before any edit");

            for (var edit = 0; edit < 12; edit++)
            {
                // A box dug out or filled around the surface section, often across a section boundary:
                var size = new Vector3i(random.Next(2, 20), random.Next(2, 20), random.Next(2, 20));
                var min = center.GetOrigin() + new Vector3i(random.Next(-24, 40), random.Next(-8, 32), random.Next(-24, 40));
                var content = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
                var material = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
                var dig = random.Next(2) == 0;

                content.Span.Data.Fill(dig ? (byte)0 : (byte)255);
                material.Span.Data.Fill(dig ? SurfaceMaterial.Null : SurfaceMaterial.Basalt);

                Assert.True(level.WriteRange(min, min + size - 1, content.Span, material.Span));
                UpdateUntilSettled(scheduler, level, publisher, receiver, sink, center);
                AssertMeshesMatchWindows(level, sink, region, $"After edit {edit}");
            }
        }
        finally
        {
            level.Dispose();
            scheduler.Dispose();
        }
    }

    [Fact]
    public void Edit_IsPublishedBeforeTheSectionsStreamingWants()
    {
        WgAssert.InitGameThread();

        var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = Math.Max(2, Environment.ProcessorCount - 1) });
        var level = new WorldLevel(scheduler, LevelTests.Graph, slotCount: 32);
        var channel = new SectionMeshChannel();
        var publisher = new SectionMeshPublisher(scheduler, Description, channel);
        var region = new HashSet<SectionPos>();
        var messages = new List<SectionMeshMessage>();

        scheduler.Start();

        try
        {
            var center = LevelTests.SurfaceSection(0, 0);

            level.AddTicket(center, HorizontalRadius, VerticalRadius);

            // The level runs alone, so every Mesh change waits for the publisher:
            UpdateLevelUntilSettled(scheduler, level);
            AddRegion(region, center);

            var due = GetSectionsDueAMesh(level, region);

            Assert.True(due.Count > Description.SlotCount, $"Only {due.Count} section(s) are due a mesh, so the nearest first order alone would publish all of them.");

            // The section the nearest first order would publish last:
            var farthest = due.MaxBy(position => GetDistanceSquared(position, center));

            FlipVoxel(level, farthest.GetOrigin() + SectionPos.SectionSize / 2);
            publisher.Update(level, center);
            channel.Take(messages);

            Assert.Equal(Description.SlotCount, messages.Count);
            Assert.Equal(SectionMeshMessageKind.Started, messages[0].Kind);
            Assert.Equal(farthest, messages[0].Position);
        }
        finally
        {
            level.Dispose();
            scheduler.Dispose();
        }
    }

    [Fact]
    public void NewerEdit_WhileTheOlderJobIsInFlight_IsTheOnlyMeshThatLands()
    {
        WgAssert.InitGameThread();
        WgAssert.InitRenderThread();

        // Never started, so a job runs only when the test drains the scheduler:
        var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 2 });
        var level = new WorldLevel(scheduler, LevelTests.Graph, slotCount: 32);
        var channel = new SectionMeshChannel();
        var publisher = new SectionMeshPublisher(scheduler, Description, channel);
        var receiver = new SectionMeshReceiver(channel);
        var sink = new LatestMeshSink();

        try
        {
            var center = LevelTests.SurfaceSection(0, 0);

            level.AddTicket(center, 2, 1);
            UpdateUntilSettled(scheduler, level, publisher, receiver, sink, center);

            Assert.True(sink.Meshes.ContainsKey(center), "The surface section has no mesh to edit.");

            var setsBefore = sink.SetCounts[center];
            var voxel = center.GetOrigin() + SectionPos.SectionSize / 2;

            // The first edit's job is adopted and stays in flight:
            FlipVoxel(level, voxel);
            publisher.Update(level, center);
            receiver.Update(sink);

            Assert.Equal(1, receiver.InFlightCount);

            FlipVoxel(level, voxel + new Vector3i(1, 0, 0));
            publisher.Update(level, center);
            receiver.Update(sink);

            Assert.Equal(2, receiver.InFlightCount);

            // Both jobs complete in the same update, and only the newer one's mesh may land:
            scheduler.Drain();
            receiver.Update(sink);

            Assert.Equal(0, receiver.InFlightCount);
            Assert.Equal(setsBefore + 1, sink.SetCounts[center]);
            AssertSameMesh(new ReferenceMesher(level).Get(center), sink.Meshes[center], "The surface section");
            Assert.Equal(Description.SlotCount, publisher.FreeSlotCount);
        }
        finally
        {
            scheduler.Drain();
            level.Dispose();
            scheduler.Dispose();
        }
    }

    /// <summary>
    ///     Updates both sides on this thread until the level has nothing pending, generating, unloading or newly ready, and every mesh change has been meshed or dropped.
    ///     A scheduler that never started is drained between updates.
    /// </summary>
    private static void UpdateUntilSettled(JobScheduler scheduler, WorldLevel level, SectionMeshPublisher publisher, SectionMeshReceiver receiver, ISectionMeshSink sink, SectionPos focus)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (true)
        {
            level.Update();
            publisher.Update(level, focus);
            receiver.Update(sink);

            if (level.PendingCount == 0 && level.InFlightCount == 0 && level.UnloadQueueCount == 0 && level.NewlyReadyCount == 0 &&
                publisher.QueuedCount == 0 && receiver.InFlightCount == 0)
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, "The level and its meshes did not settle in time.");
            WaitForWork(scheduler);
        }
    }

    /// <summary>
    ///     Updates the level alone until it has nothing pending, generating or unloading, leaving its mesh changes for the publisher.
    /// </summary>
    private static void UpdateLevelUntilSettled(JobScheduler scheduler, WorldLevel level)
    {
        for (var update = 0; update < 1000; update++)
        {
            level.Update();

            if (level.PendingCount == 0 && level.InFlightCount == 0 && level.UnloadQueueCount == 0)
            {
                return;
            }

            WaitForWork(scheduler);
        }

        throw new InvalidOperationException("The level did not settle.");
    }

    private static void WaitForWork(JobScheduler scheduler)
    {
        if (scheduler.IsStarted)
        {
            Thread.Sleep(1);
        }
        else
        {
            scheduler.Drain();
        }
    }

    /// <summary>
    ///     Checks that every section due a mesh has one, and that every section with a mesh is still loaded. Each mesh was compared with the generator's when it arrived.
    ///     A section keeps its mesh when a neighbor unloads, so a meshed section need not be due a mesh any more.
    /// </summary>
    private static void AssertMeshedMatchesLevel(WorldLevel level, RecordingSink sink, HashSet<SectionPos> region, string context)
    {
        foreach (var position in GetSectionsDueAMesh(level, region))
        {
            if (!sink.Meshed.Contains(position))
            {
                Assert.Fail($"{context}: section ({position.X}, {position.Y}, {position.Z}) and its neighbors are ready and it holds a surface, but it has no mesh.");
            }
        }

        foreach (var position in sink.Meshed)
        {
            var status = level.GetStatus(position);

            if (status != SectionStatus.Ready)
            {
                Assert.Fail($"{context}: section ({position.X}, {position.Y}, {position.Z}) has a mesh but is {status}.");
            }
        }
    }

    /// <summary>
    ///     Checks that every section of the region whose neighborhood is ready has the mesh its window holds now, where an empty mesh counts as none, and that no section that is not ready has a mesh.
    ///     A section keeps its mesh while a neighbor is away, so a section whose neighborhood is incomplete may have one.
    /// </summary>
    private static void AssertMeshesMatchWindows(WorldLevel level, LatestMeshSink sink, HashSet<SectionPos> region, string context)
    {
        var references = new ReferenceMesher(level);

        foreach (var position in region)
        {
            var name = $"{context}: section ({position.X}, {position.Y}, {position.Z})";
            var hasMesh = sink.Meshes.TryGetValue(position, out var actual) && actual.Indices.Length > 0;

            if (level.GetStatus(position) != SectionStatus.Ready)
            {
                Assert.False(hasMesh, $"{name} has a mesh but is not ready.");
                continue;
            }

            if (!IsNeighborhoodReady(level, position))
            {
                continue;
            }

            var expected = references.Get(position);

            if (expected.Indices.Length == 0)
            {
                Assert.False(hasMesh, $"{name} has a mesh, but its window holds no surface.");
                continue;
            }

            Assert.True(hasMesh, $"{name} has no mesh, but its window holds a surface.");
            AssertSameMesh(expected, actual!, name);
        }
    }

    private static void FlipVoxel(WorldLevel level, Vector3i voxel)
    {
        var content = Memory3D<byte>.Allocate(1, 1, 1);
        var material = Memory3D<byte>.Allocate(1, 1, 1);

        Assert.True(level.ReadRange(voxel, voxel, content.Span, material.Span));

        // The high bit moves the sample across the iso level:
        content.Span[0, 0, 0] ^= 0x80;

        Assert.True(level.WriteRange(voxel, voxel, content.Span, material.Span));
    }

    private static long GetDistanceSquared(SectionPos position, SectionPos center)
    {
        long deltaX = position.X - center.X;
        long deltaY = position.Y - center.Y;
        long deltaZ = position.Z - center.Z;

        return deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ;
    }

    /// <summary>
    ///     The sections of the region that are ready with their 26 neighbors and hold a surface by their content sides.
    /// </summary>
    private static HashSet<SectionPos> GetSectionsDueAMesh(WorldLevel level, HashSet<SectionPos> region)
    {
        var due = new HashSet<SectionPos>();

        foreach (var position in region)
        {
            if (level.GetStatus(position) != SectionStatus.Ready || !IsNeighborhoodReady(level, position))
            {
                continue;
            }

            var hasSurface = SectionContentSides.HasSurface(
                level.GetContentSides(position),
                level.GetContentSides(position with { X = position.X + 1 }),
                level.GetContentSides(position with { Y = position.Y + 1 }),
                level.GetContentSides(position with { Z = position.Z + 1 })
            );

            if (hasSurface)
            {
                due.Add(position);
            }
        }

        return due;
    }

    private static bool IsNeighborhoodReady(WorldLevel level, SectionPos position)
    {
        foreach (var neighbor in LevelTests.Box(position, 1, 1))
        {
            if (level.GetStatus(neighbor) != SectionStatus.Ready)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Adds the sections a ticket at <paramref name="center"/> can load or complete.
    /// </summary>
    private static void AddRegion(HashSet<SectionPos> region, SectionPos center)
    {
        foreach (var position in LevelTests.Box(center, HorizontalRadius + WorldLevel.KeepMargin + 1, VerticalRadius + WorldLevel.KeepMargin + 1))
        {
            region.Add(position);
        }
    }

    private static void AssertSameMesh(ReferenceMesh expected, SectionMeshResult actual)
    {
        AssertSameMesh(expected, actual.Origin, actual.Vertices, actual.Indices, $"Section ({actual.Position.X}, {actual.Position.Y}, {actual.Position.Z})");
    }

    private static void AssertSameMesh(ReferenceMesh expected, ReferenceMesh actual, string name)
    {
        AssertSameMesh(expected, actual.Origin, actual.Vertices, actual.Indices, name);
    }

    private static void AssertSameMesh(ReferenceMesh expected, Vector3i origin, ReadOnlySpan<VoxelVertex> vertices, ReadOnlySpan<int> indices, string name)
    {
        Assert.Equal(expected.Origin, origin);

        if (!MemoryMarshal.AsBytes(expected.Vertices.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(vertices)))
        {
            Assert.Fail($"{name} has {vertices.Length} vertices that differ from the {expected.Vertices.Length} of the reference.");
        }

        if (!expected.Indices.AsSpan().SequenceEqual(indices))
        {
            Assert.Fail($"{name} has {indices.Length} indices that differ from the {expected.Indices.Length} of the reference.");
        }
    }

    private sealed class ReferenceMesh(Vector3i origin, VoxelVertex[] vertices, int[] indices)
    {
        public readonly Vector3i Origin = origin;
        public readonly VoxelVertex[] Vertices = vertices;
        public readonly int[] Indices = indices;
    }

    /// <summary>
    ///     Meshes sections straight from the generator, or from what a level holds now when one is given, with the window worked out here rather than by the executor.
    ///     The generator's content never changes, so each of its sections is meshed once.
    /// </summary>
    private sealed class ReferenceMesher(WorldLevel? level = null)
    {
        private static readonly Vector3i CellCount = new(SectionPos.SectionSize);

        private readonly TerrainGraphSource _source = new(LevelTests.Graph);
        private readonly MultiLabelSurfaceNetsStateStorage _state = new(CellCount, Description.Fairing.Iterations);
        private readonly Dictionary<SectionPos, ReferenceMesh> _meshes = [];

        public ReferenceMesh Get(SectionPos position)
        {
            if (level == null && _meshes.TryGetValue(position, out var cached))
            {
                return cached;
            }

            var halo = new Vector3i(Description.Fairing.Iterations);
            var cellMin = position.GetOrigin();
            var box = SurfaceNetsMesher.GetSampleRange(cellMin - halo, CellCount + halo + halo);
            var size = box.Max - box.Min + 1;
            var content = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
            var material = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
            var vertices = new List<VoxelVertex>();
            var indices = new List<int>();

            if (level == null)
            {
                _source.Generate(box.Min, box.Max, content.Span, material.Span);
            }
            else
            {
                Assert.True(level.ReadRange(box.Min, box.Max, content.Span, material.Span), $"The window of section ({position.X}, {position.Y}, {position.Z}) is not loaded.");
            }

            MultiLabelSurfaceNets.Mesh(_state, new MultiLabelSurfaceNetsDescription
            {
                Content = content,
                Material = material,
                IsoLevel = WorldConstants.ContentIsoLevel,
                CellMin = cellMin - box.Min,
                CellCount = CellCount,
                Vertices = vertices,
                Indices = indices,
                Fairing = Description.Fairing
            });

            var mesh = new ReferenceMesh(box.Min, vertices.ToArray(), indices.ToArray());

            if (level == null)
            {
                _meshes[position] = mesh;
            }

            return mesh;
        }
    }

    /// <summary>
    ///     Keeps a copy of the latest mesh each section was given, and counts the meshes each was given.
    /// </summary>
    private sealed class LatestMeshSink : ISectionMeshSink
    {
        public readonly Dictionary<SectionPos, ReferenceMesh> Meshes = [];
        public readonly Dictionary<SectionPos, int> SetCounts = [];

        public void SetMesh(SectionMeshResult mesh)
        {
            Meshes[mesh.Position] = new ReferenceMesh(mesh.Origin, mesh.Vertices.ToArray(), mesh.Indices.ToArray());
            SetCounts[mesh.Position] = SetCounts.GetValueOrDefault(mesh.Position) + 1;
        }

        public void RemoveMesh(SectionPos position)
        {
            if (!Meshes.Remove(position))
            {
                Assert.Fail($"Section ({position.X}, {position.Y}, {position.Z}) lost a mesh it never had.");
            }
        }
    }

    /// <summary>
    ///     Records which sections have a mesh, and compares every mesh it is given with the generator's.
    /// </summary>
    private sealed class RecordingSink(ReferenceMesher references) : ISectionMeshSink
    {
        private readonly HashSet<SectionPos> _everMeshed = [];

        public readonly HashSet<SectionPos> Meshed = [];

        public int SetCount { get; private set; }

        public int DistinctCount => _everMeshed.Count;

        public void SetMesh(SectionMeshResult mesh)
        {
            AssertSameMesh(references.Get(mesh.Position), mesh);

            Meshed.Add(mesh.Position);
            _everMeshed.Add(mesh.Position);
            SetCount++;
        }

        public void RemoveMesh(SectionPos position)
        {
            if (!Meshed.Remove(position))
            {
                Assert.Fail($"Section ({position.X}, {position.Y}, {position.Z}) lost a mesh it never had.");
            }
        }
    }
}
