using System.Numerics;
using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.World;
using WinterGame.World.Render;

namespace Graphics2.Tests;

public class SurfaceNetsRangeTests
{
    private const byte Iso = 127;

    private static int SamplesFor(int cells) => cells + 1 + LowSampleMargin + HighSampleMargin;
    private const int LowSampleMargin = 2;
    private const int HighSampleMargin = 1;

    [Fact]
    public void GetSampleRange_AddsTheHaloAndTheStencils()
    {
        var cellMin = new Vector3i(7, 3, 11);
        var cellCount = new Vector3i(32, 8, 16);

        var window = SurfaceNetsMesher.GetSampleRange(cellMin, cellCount);

        Assert.Equal(cellMin - LowSampleMargin, window.Min);
        Assert.Equal(cellMin + cellCount + HighSampleMargin, window.Max);
        Assert.Equal(new Vector3i(SamplesFor(cellCount.X), SamplesFor(cellCount.Y), SamplesFor(cellCount.Z)), window.Max - window.Min + 1);
    }

    [Fact]
    public void GetSampleRange_RejectsAnEmptyCellCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SurfaceNetsMesher.GetSampleRange(new Vector3i(2), new Vector3i(1, 0, 1))
        );
    }

    [Fact]
    public void GetSampleRange_AdjacentChunks_OverlapByFourSamples()
    {
        var cells = new Vector3i(32);

        var first = SurfaceNetsMesher.GetSampleRange(Vector3i.Zero, cells);
        var second = SurfaceNetsMesher.GetSampleRange(cells, cells);

        Assert.Equal(4, first.Max.X - second.Min.X + 1);
        Assert.Equal(4, first.Max.Y - second.Min.Y + 1);
        Assert.Equal(4, first.Max.Z - second.Min.Z + 1);
    }

    [Fact]
    public void ChunkedMeshing_MatchesWholeBoxMeshing()
    {
        const int chunkCells = 8;
        const int chunkCount = 3;
        var cells = chunkCells * chunkCount;
        var cellCount = new Vector3i(chunkCells);

        var field = SphereField(SamplesFor(cells));

        var whole = Mesh(field);

        var chunkVertices = new List<SurfaceNetsVertex>();
        var chunkIndices = new List<int>();

        for (var chunkZ = 0; chunkZ < chunkCount; chunkZ++)
        {
            for (var chunkY = 0; chunkY < chunkCount; chunkY++)
            {
                for (var chunkX = 0; chunkX < chunkCount; chunkX++)
                {
                    var cellMin = new Vector3i(
                        LowSampleMargin + chunkX * chunkCells,
                        LowSampleMargin + chunkY * chunkCells,
                        LowSampleMargin + chunkZ * chunkCells
                    );

                    var window = SurfaceNetsMesher.GetSampleRange(cellMin, cellCount);
                    var windowField = CopyWindow(field, window);

                    var (vertices, indices) = Mesh(windowField);
                    var offset = chunkVertices.Count;

                    foreach (var vertex in vertices)
                    {
                        chunkVertices.Add(new SurfaceNetsVertex
                        {
                            Position = vertex.Position + new Vector3(window.Min.X, window.Min.Y, window.Min.Z)
                        });
                    }

                    foreach (var index in indices)
                    {
                        chunkIndices.Add(index + offset);
                    }
                }
            }
        }

        Assert.NotEmpty(whole.Indices);
        Assert.NotEmpty(chunkIndices);
        Assert.Equal(whole.Indices.Count, chunkIndices.Count);
        Assert.Equal(TriangleSet(whole.Vertices, whole.Indices), TriangleSet(chunkVertices, chunkIndices));
    }

    [Fact]
    public void GetAffectedChunkRange_MatchesEveryOverlappingWindow()
    {
        var chunkCells = new Vector3i(8);

        var boxes = new[]
        {
            new BoundingBox3i(Vector3i.Zero, Vector3i.Zero),
            new BoundingBox3i(new Vector3i(7), new Vector3i(8)),
            new BoundingBox3i(new Vector3i(14), new Vector3i(14)),
            new BoundingBox3i(new Vector3i(-3), new Vector3i(20, 1, 1)),
            new BoundingBox3i(Vector3i.Zero, new Vector3i(39))
        };

        foreach (var box in boxes)
        {
            var range = SurfaceNetsMesher.GetAffectedChunkRange(box, chunkCells);

            for (var z = range.Min.Z - 1; z <= range.Max.Z + 1; z++)
            {
                for (var y = range.Min.Y - 1; y <= range.Max.Y + 1; y++)
                {
                    for (var x = range.Min.X - 1; x <= range.Max.X + 1; x++)
                    {
                        var chunk = new Vector3i(x, y, z);
                        var window = SurfaceNetsMesher.GetSampleRange(chunk * chunkCells, chunkCells);

                        Assert.Equal(Overlaps(window, box), range.Contains(chunk));
                    }
                }
            }
        }
    }

    [Fact]
    public void GetAffectedChunkRange_IncludesTheChunkWhoseHaloCellChanged()
    {
        var chunkCells = new Vector3i(32);

        // Sample 62 is inside chunk 1's cells and inside chunk 2's halo, so both meshes can change:
        var range = SurfaceNetsMesher.GetAffectedChunkRange(new BoundingBox3i(new Vector3i(62), new Vector3i(62)), chunkCells);

        Assert.Equal(new Vector3i(1), range.Min);
        Assert.Equal(new Vector3i(2), range.Max);
    }

    [Fact]
    public void GetAffectedChunkRange_HandlesNegativeSamples()
    {
        var chunkCells = new Vector3i(32);

        // The first chunk's window starts two samples below its first cell, so negative indices are reachable:
        var range = SurfaceNetsMesher.GetAffectedChunkRange(new BoundingBox3i(new Vector3i(-2), new Vector3i(-1)), chunkCells);

        Assert.Equal(new Vector3i(-1), range.Min);
        Assert.Equal(new Vector3i(0), range.Max);
    }

    [Fact]
    public void GetAffectedChunkRange_RejectsAnEmptyBoxOrAnEmptyChunk()
    {
        var box = new BoundingBox3i(new Vector3i(1), new Vector3i(1));

        Assert.Throws<ArgumentException>(() =>
            SurfaceNetsMesher.GetAffectedChunkRange(new BoundingBox3i(new Vector3i(1), Vector3i.Zero), new Vector3i(8))
        );

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SurfaceNetsMesher.GetAffectedChunkRange(box, new Vector3i(8, 0, 8))
        );
    }

    [Fact]
    public void ReadingChunkWindows_AsksTheStorageForExactlyThoseWindows()
    {
        const int chunkCells = 8;
        const int chunkCount = 3;
        var cellCount = new Vector3i(chunkCells);
        var storage = new RecordingStorage(new Vector3i(SamplesFor(chunkCells * chunkCount)));

        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            // The first chunk's cells start one low margin into the storage, which is what keeps every window inside it:
            var cellMin = new Vector3i(LowSampleMargin + chunk * chunkCells);
            var window = SurfaceNetsMesher.GetSampleRange(cellMin, cellCount);
            var size = window.Max - window.Min + 1;

            var content = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);
            var material = Memory3D<byte>.Allocate(size.X, size.Y, size.Z);

            storage.ReadRange(0, window.Min, window.Max, content.Span, material.Span);
        }

        Assert.Equal(chunkCount, storage.Reads.Count);

        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            var window = storage.Reads[chunk];
            var size = window.Max - window.Min + 1;

            Assert.Equal(new Vector3i(SamplesFor(chunkCells)), size);
            Assert.Equal(new Vector3i(chunk * chunkCells), window.Min);
            Assert.True(window.Min.X >= 0 && window.Min.Y >= 0 && window.Min.Z >= 0);
            Assert.True(window.Max.X < storage.SizeInVoxels.X);
            Assert.True(window.Max.Y < storage.SizeInVoxels.Y);
            Assert.True(window.Max.Z < storage.SizeInVoxels.Z);
        }
    }

    private static (List<SurfaceNetsVertex> Vertices, List<int> Indices) Mesh(Memory3D<byte> content)
    {
        var vertices = new List<SurfaceNetsVertex>();
        var indices = new List<int>();

        SurfaceNetsMesher.Mesh(new SurfaceNetsMeshingDescription
        {
            Content = content,
            IsoLevel = Iso,
            Vertices = vertices,
            Indices = indices
        });

        return (vertices, indices);
    }

    private static Memory3D<byte> SphereField(int size)
    {
        var field = Memory3D<byte>.Allocate(size, size, size);
        var content = field.Span;
        var center = new Vector3(size / 2.0f);
        var radius = size * 0.3f;

        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var distance = Vector3.Distance(new Vector3(x, y, z), center) - radius;
                    content[x, y, z] = (byte)Math.Clamp(Iso - (distance * Iso), 0.0f, 255.0f);
                }
            }
        }

        return field;
    }

    private static Memory3D<byte> CopyWindow(Memory3D<byte> source, BoundingBox3i window)
    {
        var sizeX = window.Max.X - window.Min.X + 1;
        var sizeY = window.Max.Y - window.Min.Y + 1;
        var sizeZ = window.Max.Z - window.Min.Z + 1;

        var destination = Memory3D<byte>.Allocate(sizeX, sizeY, sizeZ);
        var sourceSpan = source.Span;
        var destinationSpan = destination.Span;

        for (var z = 0; z < sizeZ; z++)
        {
            for (var y = 0; y < sizeY; y++)
            {
                for (var x = 0; x < sizeX; x++)
                {
                    destinationSpan[x, y, z] = sourceSpan[window.Min.X + x, window.Min.Y + y, window.Min.Z + z];
                }
            }
        }

        return destination;
    }
    
    private static HashSet<(Vector3, Vector3, Vector3)> TriangleSet(List<SurfaceNetsVertex> vertices, List<int> indices)
    {
        var triangles = new HashSet<(Vector3, Vector3, Vector3)>();

        for (var i = 0; i < indices.Count; i += 3)
        {
            var positions = new[]
            {
                Quantize(vertices[indices[i]].Position),
                Quantize(vertices[indices[i + 1]].Position),
                Quantize(vertices[indices[i + 2]].Position)
            };

            Array.Sort(positions, ComparePositions);

            triangles.Add((positions[0], positions[1], positions[2]));
        }

        return triangles;
    }

    private static Vector3 Quantize(Vector3 position) => new(MathF.Round(position.X, 3), MathF.Round(position.Y, 3), MathF.Round(position.Z, 3));

    private static int ComparePositions(Vector3 left, Vector3 right)
    {
        var byX = left.X.CompareTo(right.X);

        if (byX != 0)
        {
            return byX;
        }

        var byY = left.Y.CompareTo(right.Y);

        return byY != 0 ? byY : left.Z.CompareTo(right.Z);
    }

    private static bool Overlaps(BoundingBox3i left, BoundingBox3i right) =>
        left.Min.X <= right.Max.X && left.Max.X >= right.Min.X &&
        left.Min.Y <= right.Max.Y && left.Max.Y >= right.Min.Y &&
        left.Min.Z <= right.Max.Z && left.Max.Z >= right.Min.Z;

    private sealed class RecordingStorage(Vector3i sizeInVoxels) : IVoxelStorage
    {
        public readonly List<BoundingBox3i> Reads = [];

        public Vector3i SizeInVoxels { get; } = sizeInVoxels;

        public void ReadRange(int lod, Vector3i min, Vector3i maxInclusive, Span3D<byte> content, Span3D<byte> material)
        {
            if (content.SizeX != maxInclusive.X - min.X + 1 ||
                content.SizeY != maxInclusive.Y - min.Y + 1 ||
                content.SizeZ != maxInclusive.Z - min.Z + 1)
            {
                throw new ArgumentException("The content box must be maxInclusive - min + 1 on every axis.", nameof(content));
            }

            Reads.Add(new BoundingBox3i(min, maxInclusive));

            content.Data.Fill(0);
            material.Data.Fill(255);
        }
    }
}