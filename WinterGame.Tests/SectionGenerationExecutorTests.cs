using Graphics2.Data;
using WinterGame.Jobs;
using WinterGame.Utilities;
using WinterGame.World.Generation;
using WinterGame.World.Level.Loading;
using WinterGame.World.Storage;

namespace WinterGame.Tests;

public class SectionGenerationExecutorTests
{
    private static readonly TerrainGraph Graph = LevelTests.Graph;

    private sealed class Harness : IDisposable
    {
        public readonly JobScheduler Scheduler = new(new JobSchedulerDescription { ThreadCount = 2 });
        public readonly SectionGenerationExecutor Executor;
        public readonly int ProviderId;

        public Harness(int slotCount = SectionGenerationExecutor.DefaultSlotCount)
        {
            WgAssert.InitGameThread();
            Executor =new SectionGenerationExecutor(Graph, Scheduler.ThreadCount, slotCount);
            ProviderId = Scheduler.RegisterExecutor(Executor);
        }

        /// <summary>
        ///     Publishes one section, runs it on the given worker id and returns its slot, with the job completed and released.
        /// </summary>
        public int Generate(SectionPos position, int workerId = 0)
        {
            var job = Scheduler.Pool.CreateJob();
            var slot = Executor.Allocate(position);

            Assert.True(Scheduler.Publish(job, ProviderId, slot));
            Assert.Equal(1, Scheduler.Drain(workerId));
            Assert.True(Scheduler.Pool.TryComplete(job));

            Scheduler.Pool.Release(job);

            return slot;
        }

        public void Dispose()
        {
            Scheduler.Dispose();
        }
    }

    [Fact]
    public void Execute_OnASurfaceSection_StoresUniformAndDenseBricks()
    {
        using var harness = new Harness();

        var section = harness.Executor.GetResult(harness.Generate(LevelTests.SurfaceSection(0, 0))).Section;

        var types = Enumerable.Range(0, Section.Bricks).Select(index => section.GetBrickType(VoxelChannel.Content, index)).ToArray();

        Assert.Equal(SectionType.Explicit, section.Type);
        Assert.Contains(BrickType.Uniform, types);
        Assert.Contains(BrickType.Dense, types);
    }

    [Fact]
    public void GenerateNow_AtTheLowestSkySection_IsUniformAirInBothChannels()
    {
        WgAssert.InitGameThread();

        var executor = new SectionGenerationExecutor(Graph, workerCount: 1, slotCount: 1);
        var arena = new SectionArena();
        var skyY = LevelTests.LowestSkySectionY();

        for (var x = -2; x <= 2; x++)
        {
            for (var z = -2; z <= 2; z++)
            {
                LevelTests.AssertUniformAir(executor.GenerateNow(new SectionPos(x, skyY, z), arena).Section);
            }
        }
    }

    [Fact]
    public void Execute_DeepInTheBasement_IsSharedAndMatchesTheSource()
    {
        using var harness = new Harness();

        var section = harness.Executor.GetResult(harness.Generate(new SectionPos(0, -100, 0))).Section;

        Assert.Equal(SectionType.Shared, section.Type);
        Assert.Equal(byte.MaxValue, section.GetUniformValue(VoxelChannel.Content, 0));
        AssertMatchesSource(section);
    }

    [Fact]
    public void Execute_OnDifferentWorkers_ProducesTheSameBricks()
    {
        using var harness = new Harness();
        var position = LevelTests.SurfaceSection(1, -1);

        var first = harness.Executor.GetResult(harness.Generate(position, workerId: 0));
        var second = harness.Executor.GetResult(harness.Generate(position, workerId: 1));

        AssertSameVoxels(first.Section, second.Section);
        Assert.Equal(first.ContentSides, second.ContentSides);
    }

    [Fact]
    public void Free_LetsTheSlotGenerateAgain()
    {
        using var harness = new Harness(slotCount: 1);

        for (var round = 0; round < 3; round++)
        {
            var position = LevelTests.SurfaceSection(round, 0);
            var slot = harness.Generate(position);
            var section = harness.Executor.GetResult(slot).Section;

            Assert.Equal(position, section.Position);
            AssertMatchesSource(section);

            harness.Executor.Free(slot);
        }
    }

    [Fact]
    public void CopiedResult_OutlivesTheSlot()
    {
        using var harness = new Harness(slotCount: 1);
        var position = LevelTests.SurfaceSection(0, 0);
        var level = new SectionArena();

        var slot = harness.Generate(position);
        var copy = level.CreateCopy(harness.Executor.GetResult(slot).Section);

        harness.Executor.Free(slot);
        harness.Generate(LevelTests.SurfaceSection(2, 2));

        AssertMatchesSource(copy);
    }

    [Fact]
    public void CopiedSharedResult_OutlivesTheSlot()
    {
        using var harness = new Harness(slotCount: 1);
        var position = new SectionPos(0, -100, 0);
        var level = new SectionArena();

        var slot = harness.Generate(position);
        var copy = level.CreateCopy(harness.Executor.GetResult(slot).Section);

        harness.Executor.Free(slot);
        harness.Generate(LevelTests.SurfaceSection(2, 2));

        Assert.Equal(SectionType.Shared, copy.Type);
        AssertMatchesSource(copy);
    }

    [Fact]
    public void FreeSlotCount_TracksAllocateAndFree()
    {
        using var harness = new Harness(slotCount: 2);
        var executor = harness.Executor;

        Assert.Equal(2, executor.FreeSlotCount);

        var first = executor.Allocate(new SectionPos(0, 0, 0));
        var second = executor.Allocate(new SectionPos(1, 0, 0));

        Assert.Equal(0, executor.FreeSlotCount);
        Assert.Throws<InvalidOperationException>(() => executor.Allocate(new SectionPos(2, 0, 0)));

        executor.Free(first);
        executor.Free(second);

        Assert.Equal(2, executor.FreeSlotCount);
    }

    [Fact]
    public void GetResult_RejectsASlotThatWasNeverGenerated()
    {
        using var harness = new Harness();

        var slot = harness.Executor.Allocate(new SectionPos(0, 0, 0));

        Assert.Throws<InvalidOperationException>(() => harness.Executor.GetResult(slot));

        harness.Executor.Free(slot);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void Constructor_RejectsInvalidCounts(int workerCount, int slotCount)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SectionGenerationExecutor(Graph, workerCount, slotCount));
    }

    [Fact]
    public void GenerateNow_IntoAFullArena_ThrowsAndLeavesNoSectionBehind()
    {
        WgAssert.InitGameThread();

        var executor = new SectionGenerationExecutor(Graph, workerCount: 1, slotCount: 1);
        var position = LevelTests.SurfaceSection(0, 0);
        var reference = executor.GenerateNow(position, new SectionArena()).Section;

        var denseBricks = Enum.GetValues<VoxelChannel>().Sum(
            channel => Enumerable.Range(0, Section.Bricks).Count(index => reference.GetBrickType(channel, index) == BrickType.Dense)
        );

        // The arena addresses 32 dense bricks, which the surface section needs more than:
        var full = new SectionArena(descriptorArraysPerPage: 1, voxelArraysPerPage: 1);

        Assert.True(denseBricks > full.MaxVoxelArrays);
        Assert.Throws<InvalidOperationException>(() => executor.GenerateNow(position, full));
        Assert.Throws<InvalidOperationException>(() => full.Destroy(position));
    }

    private static void AssertMatchesSource(Section section)
    {
        var content = Memory3D<byte>.Allocate(SectionPos.SectionSize, SectionPos.SectionSize, SectionPos.SectionSize);
        var material = Memory3D<byte>.Allocate(SectionPos.SectionSize, SectionPos.SectionSize, SectionPos.SectionSize);
        var min = section.Position.GetOrigin();

        new TerrainGraphSource(Graph).Generate(min, min + SectionPos.SectionMask, content.Span, material.Span);

        var contentSpan = content.Span;
        var materialSpan = material.Span;

        for (var z = 0; z < SectionPos.SectionSize; z++)
        {
            for (var y = 0; y < SectionPos.SectionSize; y++)
            {
                for (var x = 0; x < SectionPos.SectionSize; x++)
                {
                    Assert.Equal(contentSpan[x, y, z], ReadVoxel(section, VoxelChannel.Content, x, y, z));
                    Assert.Equal(materialSpan[x, y, z], ReadVoxel(section, VoxelChannel.Material, x, y, z));
                }
            }
        }
    }

    private static void AssertSameVoxels(Section first, Section second)
    {
        foreach (var channel in Enum.GetValues<VoxelChannel>())
        {
            for (var z = 0; z < SectionPos.SectionSize; z++)
            {
                for (var y = 0; y < SectionPos.SectionSize; y++)
                {
                    for (var x = 0; x < SectionPos.SectionSize; x++)
                    {
                        Assert.Equal(ReadVoxel(first, channel, x, y, z), ReadVoxel(second, channel, x, y, z));
                    }
                }
            }
        }
    }

    private static byte ReadVoxel(Section section, VoxelChannel channel, int x, int y, int z)
    {
        var brickIndex = Section.GetBrickIndex(x >> Section.BrickLog, y >> Section.BrickLog, z >> Section.BrickLog);

        if (section.GetBrickType(channel, brickIndex) == BrickType.Uniform)
        {
            return section.GetUniformValue(channel, brickIndex);
        }

        const int mask = Section.BrickSize - 1;

        return section.GetDense(channel, brickIndex).Span[Section.GetBrickVoxelIndex(x & mask, y & mask, z & mask)];
    }
}
