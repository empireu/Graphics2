using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.Jobs;
using WinterGame.Utilities;
using WinterGame.World;
using WinterGame.World.Level;
using WinterGame.World.Level.Meshing;
using WinterGame.World.Storage;

namespace WinterGame.Tests;

public class LevelEditTests
{
    private const int HorizontalRadius = 3;
    private const int VerticalRadius = 2;

    /// <summary>
    ///     The default window worked out by hand: the fairing's 4 cells around the section, two samples below the cells and one above them.
    /// </summary>
    private static readonly Vector3i WindowMin = new(-6);

    private static readonly Vector3i WindowMax = new(37);

    /// <summary>
    ///     A level with a ticket on the surface over a running scheduler, filled before the test starts.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        public readonly JobScheduler Scheduler;
        public readonly WorldLevel Level;
        public readonly SectionPos Center = LevelTests.SurfaceSection(0, 0);

        private readonly List<SectionMeshChange> _changes = [];

        public Harness()
        {
            WgAssert.InitGameThread();

            Scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = Math.Max(2, Environment.ProcessorCount - 1) });
            Level = new WorldLevel(Scheduler, LevelTests.Graph, slotCount: 32);
            Scheduler.Start();
            Level.AddTicket(Center, HorizontalRadius, VerticalRadius);

            var deadline = DateTime.UtcNow.AddSeconds(60);

            do
            {
                Assert.True(DateTime.UtcNow < deadline, "The level did not fill in time.");
                Thread.Sleep(1);
                Level.Update();
            }
            while (Level.PendingCount > 0 || Level.InFlightCount > 0);
        }

        /// <summary>
        ///     The mesh changes since the last call.
        /// </summary>
        public List<SectionMeshChange> TakeChanges()
        {
            _changes.Clear();
            Level.TakeMeshChanges(_changes);

            return _changes;
        }

        public void Dispose()
        {
            Level.Dispose();
            Scheduler.Dispose();
        }
    }

    [Fact]
    public void WriteRange_MatchesABruteForceModelOverRandomWrites()
    {
        using var harness = new Harness();

        const int regionSize = 3 * SectionPos.SectionSize;

        var level = harness.Level;
        var center = harness.Center;
        var random = new Random(77);
        var first = new SectionPos(center.X - 1, center.Y - 1, center.Z - 1);
        var regionMin = first.GetOrigin();
        var regionMax = regionMin + (regionSize - 1);
        var modelContent = Memory3D<byte>.Allocate(regionSize, regionSize, regionSize);
        var modelMaterial = Memory3D<byte>.Allocate(regionSize, regionSize, regionSize);
        var readContent = Memory3D<byte>.Allocate(regionSize, regionSize, regionSize);
        var readMaterial = Memory3D<byte>.Allocate(regionSize, regionSize, regionSize);

        Assert.True(level.ReadRange(regionMin, regionMax, modelContent.Span, modelMaterial.Span));
        AssertContentSides(level, modelContent, first, "Before any write");

        for (var write = 0; write < 150; write++)
        {
            var size = new Vector3i(random.Next(1, 41), random.Next(1, 41), random.Next(1, 41));
            var offset = new Vector3i(random.Next(0, regionSize - size.X + 1), random.Next(0, regionSize - size.Y + 1), random.Next(0, regionSize - size.Z + 1));
            var content = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
            var material = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
            var kind = random.Next(4);
            var constantContent = new byte[] { 0, WorldConstants.ContentIsoLevel, WorldConstants.ContentIsoLevel + 1, 255, (byte)random.Next(256) }[random.Next(5)];
            var constantMaterial = (byte)random.Next(SurfaceMaterial.Count);

            for (var z = 0; z < size.Z; z++)
            {
                for (var y = 0; y < size.Y; y++)
                {
                    for (var x = 0; x < size.X; x++)
                    {
                        ref var modelContentValue = ref modelContent.Span[offset.X + x, offset.Y + y, offset.Z + z];
                        ref var modelMaterialValue = ref modelMaterial.Span[offset.X + x, offset.Y + y, offset.Z + z];

                        // Random values, one value per channel, the values already there, or the content flipped across the iso level:
                        switch (kind)
                        {
                            case 0:
                                modelContentValue = (byte)random.Next(256);
                                modelMaterialValue = (byte)random.Next(256);
                                break;
                            case 1:
                                modelContentValue = constantContent;
                                modelMaterialValue = constantMaterial;
                                break;
                            case 3:
                                modelContentValue ^= 0x80;
                                break;
                        }

                        content.Span[x, y, z] = modelContentValue;
                        material.Span[x, y, z] = modelMaterialValue;
                    }
                }
            }

            var context = $"Write {write} of kind {kind}, {size.X} x {size.Y} x {size.Z} at ({offset.X}, {offset.Y}, {offset.Z})";

            Assert.True(level.WriteRange(regionMin + offset, regionMin + offset + size - 1, content.Span, material.Span), $"{context} was refused.");
            Assert.True(level.ReadRange(regionMin, regionMax, readContent.Span, readMaterial.Span));

            AssertSameBox(modelContent, readContent, regionMin, $"{context}: the content");
            AssertSameBox(modelMaterial, readMaterial, regionMin, $"{context}: the material");
            AssertContentSides(level, modelContent, first, context);
        }
    }

    [Fact]
    public void WriteRange_StoresABrickLeftHoldingOneValueUniform()
    {
        using var harness = new Harness();

        var level = harness.Level;

        Assert.True(level.TryGetSection(harness.Center, out var section));

        var brickIndex = FindBrick(section, BrickType.Dense);

        Assert.True(brickIndex >= 0, "The surface section has no dense content brick.");

        var brickMin = section.Position.GetOrigin() + GetBrickOrigin(brickIndex);
        var brickMax = brickMin + (Section.BrickSize - 1);
        var content = Memory3D<byte>.Allocate(Section.BrickSize, Section.BrickSize, Section.BrickSize);
        var material = Memory3D<byte>.Allocate(Section.BrickSize, Section.BrickSize, Section.BrickSize);

        // Covering the brick with one value:
        Assert.True(level.ReadRange(brickMin, brickMax, content.Span, material.Span));
        content.Span.Data.Fill(200);
        Assert.True(level.WriteRange(brickMin, brickMax, content.Span, material.Span));

        Assert.Equal(BrickType.Uniform, section.GetBrickType(VoxelChannel.Content, brickIndex));
        Assert.Equal(200, section.GetUniformValue(VoxelChannel.Content, brickIndex));

        // One voxel of another value makes it dense, and writing the voxel back makes it uniform again:
        var voxelContent = Memory3D<byte>.Allocate(1, 1, 1);
        var voxelMaterial = Memory3D<byte>.Allocate(1, 1, 1);
        var voxel = brickMin + 1;

        Assert.True(level.ReadRange(voxel, voxel, voxelContent.Span, voxelMaterial.Span));
        voxelContent.Span[0, 0, 0] = 10;
        Assert.True(level.WriteRange(voxel, voxel, voxelContent.Span, voxelMaterial.Span));

        Assert.Equal(BrickType.Dense, section.GetBrickType(VoxelChannel.Content, brickIndex));
        Assert.True(level.ReadPoint(voxel, out var written, out _));
        Assert.Equal(10, written);

        voxelContent.Span[0, 0, 0] = 200;
        Assert.True(level.WriteRange(voxel, voxel, voxelContent.Span, voxelMaterial.Span));

        Assert.Equal(BrickType.Uniform, section.GetBrickType(VoxelChannel.Content, brickIndex));
        Assert.Equal(200, section.GetUniformValue(VoxelChannel.Content, brickIndex));
    }

    [Fact]
    public void WriteRange_OfTheValuesAlreadyThere_ReportsNothing()
    {
        using var harness = new Harness();

        var level = harness.Level;
        var min = harness.Center.GetOrigin() - 20;
        var max = harness.Center.GetOrigin() + 50;
        var size = max - min + 1;
        var content = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
        var material = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);

        harness.TakeChanges();

        Assert.True(level.ReadRange(min, max, content.Span, material.Span));
        Assert.True(level.WriteRange(min, max, content.Span, material.Span));

        Assert.Empty(harness.TakeChanges());
    }

    [Fact]
    public void WriteRange_ChangingASharedSection_MakesOnlyItExplicit()
    {
        WgAssert.InitGameThread();

        using var scheduler = new JobScheduler(new JobSchedulerDescription { ThreadCount = 1 });
        var level = new WorldLevel(scheduler, LevelTests.Graph);
        var first = new SectionPos(0, LevelTests.LowestSkySectionY() + 10, 0);
        var second = first with { X = first.X + 1 };

        // A sky section loads as shared air without a job:
        level.AddTicket(first, 1, 1);
        level.Update();

        Assert.True(level.TryGetSection(first, out var firstSection));
        Assert.True(level.TryGetSection(second, out var secondSection));
        Assert.Equal(SectionType.Shared, firstSection.Type);
        Assert.Equal(SectionType.Shared, secondSection.Type);

        // The air both sections hold, written back across them, changes nothing:
        var min = first.GetOrigin() + 16;
        var max = second.GetOrigin() + 16;
        var size = max - min + 1;
        var content = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
        var material = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);

        Assert.True(level.ReadRange(min, max, content.Span, material.Span));
        Assert.True(level.WriteRange(min, max, content.Span, material.Span));

        Assert.Equal(SectionType.Shared, firstSection.Type);
        Assert.Equal(SectionType.Shared, secondSection.Type);

        // One solid voxel inside the first section:
        var voxel = first.GetOrigin() + 5;
        var voxelContent = Memory3D<byte>.Allocate(1, 1, 1);
        var voxelMaterial = Memory3D<byte>.Allocate(1, 1, 1);

        voxelContent.Span[0, 0, 0] = 200;
        voxelMaterial.Span[0, 0, 0] = SurfaceMaterial.Basalt;

        Assert.True(level.WriteRange(voxel, voxel, voxelContent.Span, voxelMaterial.Span));

        Assert.Equal(SectionType.Explicit, firstSection.Type);
        Assert.Equal(SectionType.Shared, secondSection.Type);
        Assert.True(level.ReadPoint(voxel, out var writtenContent, out var writtenMaterial));
        Assert.Equal(200, writtenContent);
        Assert.Equal(SurfaceMaterial.Basalt, writtenMaterial);
        Assert.True(level.ReadPoint(second.GetOrigin() + 5, out var otherContent, out var otherMaterial));
        Assert.Equal(0, otherContent);
        Assert.Equal(SurfaceMaterial.Null, otherMaterial);
        Assert.Equal(new SectionContentSides(ContentSide.Mixed, ContentSide.Air, ContentSide.Air, ContentSide.Air), level.GetContentSides(first));
        Assert.Equal(SectionContentSides.FromOneSide(ContentSide.Air), level.GetContentSides(second));

        level.Dispose();
    }

    [Fact]
    public void WriteRange_TouchingASectionThatIsNotReady_WritesNothing()
    {
        using var harness = new Harness();

        var level = harness.Level;
        var loaded = harness.Center with { X = harness.Center.X + HorizontalRadius };
        var keepMargin = loaded with { X = loaded.X + 1 };

        Assert.Equal(SectionStatus.Ready, level.GetStatus(loaded));
        Assert.Equal(SectionStatus.None, level.GetStatus(keepMargin));

        var min = loaded.GetOrigin() + new Vector3i(20, 5, 5);
        var max = keepMargin.GetOrigin() + new Vector3i(5, 10, 10);
        var size = max - min + 1;
        var before = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
        var after = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
        var material = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
        var solid = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);

        harness.TakeChanges();
        level.ReadRange(min, max, before.Span, material.Span);
        solid.Span.Data.Fill(255);

        Assert.False(level.WriteRange(min, max, solid.Span, material.Span));

        level.ReadRange(min, max, after.Span, material.Span);

        AssertSameBox(before, after, min, "The content");
        Assert.Empty(harness.TakeChanges());
    }

    [Fact]
    public void WriteRange_ReportsExactlyTheSectionsWhoseWindowItChanged()
    {
        using var harness = new Harness();

        var level = harness.Level;
        var center = harness.Center;
        var random = new Random(99);
        var reported = new HashSet<SectionPos>();

        Assert.Equal(WindowMin, level.MeshWindow.Min);
        Assert.Equal(WindowMax, level.MeshWindow.Max);

        foreach (var change in harness.TakeChanges())
        {
            Assert.Equal(SectionMeshChangeKind.Mesh, change.Kind);
            reported.Add(change.Position);
        }

        Assert.NotEmpty(reported);

        // Every section of this area is loaded, so every write lands:
        var areaMin = new SectionPos(center.X - 2, center.Y - 1, center.Z - 2).GetOrigin();
        var areaSize = new Vector3i(5, 3, 5) * SectionPos.SectionSize;
        var edits = 0;

        for (var write = 0; write < 80; write++)
        {
            var size = new Vector3i(random.Next(1, 13), random.Next(1, 13), random.Next(1, 13));
            var offset = new Vector3i(random.Next(0, areaSize.X - size.X + 1), random.Next(0, areaSize.Y - size.Y + 1), random.Next(0, areaSize.Z - size.Z + 1));
            var min = areaMin + offset;
            var max = min + size - 1;
            var content = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
            var material = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);

            Assert.True(level.ReadRange(min, max, content.Span, material.Span));

            // Flipping the high bit changes every sample, so the changed box is the written box:
            var data = content.Span.Data;

            for (var index = 0; index < data.Length; index++)
            {
                data[index] ^= 0x80;
            }

            Assert.True(level.WriteRange(min, max, content.Span, material.Span));

            var context = $"Write {write}, {size.X} x {size.Y} x {size.Z} at ({min.X}, {min.Y}, {min.Z})";
            var expected = GetSectionsDueAnEdit(level, reported, min, max);
            var actual = new HashSet<SectionPos>();

            foreach (var change in harness.TakeChanges())
            {
                var position = change.Position;

                Assert.True(change.Kind == SectionMeshChangeKind.Edit, $"{context}: section ({position.X}, {position.Y}, {position.Z}) was reported as {change.Kind}.");
                Assert.True(actual.Add(position), $"{context}: section ({position.X}, {position.Y}, {position.Z}) was reported twice.");
            }

            foreach (var position in expected.Except(actual))
            {
                Assert.Fail($"{context}: section ({position.X}, {position.Y}, {position.Z}) needs its mesh again but was not reported.");
            }

            foreach (var position in actual.Except(expected))
            {
                Assert.Fail($"{context}: section ({position.X}, {position.Y}, {position.Z}) was reported, but the write changed nothing its mesh reads or it has no mesh to replace or surface to show.");
            }

            reported.UnionWith(actual);
            edits += actual.Count;
        }

        Assert.True(edits > 80, $"Only {edits} edit(s) were reported over 80 writes.");
    }

    /// <summary>
    ///     The ready sections whose window holds a sample of the box and that have a mesh, or now hold a surface with their neighborhood ready.
    /// </summary>
    private static HashSet<SectionPos> GetSectionsDueAnEdit(WorldLevel level, HashSet<SectionPos> reported, Vector3i min, Vector3i max)
    {
        var due = new HashSet<SectionPos>();
        var sectionMin = (min - 2 * SectionPos.SectionSize) >> SectionPos.Log;
        var sectionMax = (max + 2 * SectionPos.SectionSize) >> SectionPos.Log;

        for (var z = sectionMin.Z; z <= sectionMax.Z; z++)
        {
            for (var y = sectionMin.Y; y <= sectionMax.Y; y++)
            {
                for (var x = sectionMin.X; x <= sectionMax.X; x++)
                {
                    var position = new SectionPos(x, y, z);
                    var windowMin = position.GetOrigin() + WindowMin;
                    var windowMax = position.GetOrigin() + WindowMax;

                    var overlaps = windowMin.X <= max.X && windowMin.Y <= max.Y && windowMin.Z <= max.Z &&
                                   windowMax.X >= min.X && windowMax.Y >= min.Y && windowMax.Z >= min.Z;

                    if (!overlaps || level.GetStatus(position) != SectionStatus.Ready)
                    {
                        continue;
                    }

                    if (reported.Contains(position) || (IsNeighborhoodReady(level, position) && HasSurface(level, position)))
                    {
                        due.Add(position);
                    }
                }
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
    ///     Whether an edge some cell of the section owns crosses the iso level, scanned from what the level holds now.
    /// </summary>
    private static bool HasSurface(WorldLevel level, SectionPos position)
    {
        const int ownedSize = SectionPos.SectionSize + 1;

        var content = Memory3D<byte>.Allocate(ownedSize, ownedSize, ownedSize);
        var material = Memory3D<byte>.Allocate(ownedSize, ownedSize, ownedSize);
        var min = position.GetOrigin();

        Assert.True(level.ReadRange(min, min + SectionPos.SectionSize, content.Span, material.Span));

        var span = content.Span;

        for (var z = 0; z < SectionPos.SectionSize; z++)
        {
            for (var y = 0; y < SectionPos.SectionSize; y++)
            {
                for (var x = 0; x < SectionPos.SectionSize; x++)
                {
                    var solid = span[x, y, z] > WorldConstants.ContentIsoLevel;

                    if (solid != span[x + 1, y, z] > WorldConstants.ContentIsoLevel ||
                        solid != span[x, y + 1, z] > WorldConstants.ContentIsoLevel ||
                        solid != span[x, y, z + 1] > WorldConstants.ContentIsoLevel)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Checks the content sides the level holds for every section of a region against a scan of the region's model, which starts at <paramref name="first"/>.
    /// </summary>
    private static void AssertContentSides(WorldLevel level, Memory3D<byte> model, SectionPos first, string context)
    {
        const int size = SectionPos.SectionSize;

        var sectionBox = Memory3D<byte>.Allocate(size, size, size);
        var sections = model.SizeX / size;
        var modelData = model.Span.Data;
        var boxData = sectionBox.Span.Data;

        for (var sectionZ = 0; sectionZ < sections; sectionZ++)
        {
            for (var sectionY = 0; sectionY < sections; sectionY++)
            {
                for (var sectionX = 0; sectionX < sections; sectionX++)
                {
                    for (var z = 0; z < size; z++)
                    {
                        for (var y = 0; y < size; y++)
                        {
                            var row = sectionX * size + (sectionY * size + y + (sectionZ * size + z) * model.SizeY) * model.SizeX;

                            modelData.Slice(row, size).CopyTo(boxData.Slice((y + z * size) * size, size));
                        }
                    }

                    var position = new SectionPos(first.X + sectionX, first.Y + sectionY, first.Z + sectionZ);
                    var expected = SectionContentSides.FromContent(sectionBox.Span);
                    var actual = level.GetContentSides(position);

                    Assert.True(expected == actual, $"{context}: section ({position.X}, {position.Y}, {position.Z}) holds the sides {actual}, but its content is {expected}.");
                }
            }
        }
    }

    private static void AssertSameBox(Memory3D<byte> expected, Memory3D<byte> actual, Vector3i min, string name)
    {
        if (expected.Span.Data.SequenceEqual(actual.Span.Data))
        {
            return;
        }

        var expectedSpan = expected.Span;
        var actualSpan = actual.Span;

        for (var z = 0; z < expected.SizeZ; z++)
        {
            for (var y = 0; y < expected.SizeY; y++)
            {
                for (var x = 0; x < expected.SizeX; x++)
                {
                    if (expectedSpan[x, y, z] != actualSpan[x, y, z])
                    {
                        Assert.Fail($"{name} at ({min.X + x}, {min.Y + y}, {min.Z + z}) is {actualSpan[x, y, z]}, expected {expectedSpan[x, y, z]}.");
                    }
                }
            }
        }
    }

    /// <summary>
    ///     The first content brick of the type, or -1.
    /// </summary>
    private static int FindBrick(Section section, BrickType type)
    {
        for (var brickIndex = 0; brickIndex < Section.Bricks; brickIndex++)
        {
            if (section.GetBrickType(VoxelChannel.Content, brickIndex) == type)
            {
                return brickIndex;
            }
        }

        return -1;
    }

    private static Vector3i GetBrickOrigin(int brickIndex)
    {
        return new Vector3i(
            brickIndex % Section.BricksPerAxis,
            brickIndex / Section.BricksPerAxis % Section.BricksPerAxis,
            brickIndex / (Section.BricksPerAxis * Section.BricksPerAxis)
        ) * Section.BrickSize;
    }
}
