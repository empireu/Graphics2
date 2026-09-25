using System.Numerics;
using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.World;
using WinterGame.World.Render;

namespace WinterGame.Tests;

public class MultiLabelSurfaceNetsTests
{
    private const byte IsoLevel = 127;

    private static Memory3D<byte> Sphere(int size)
    {
        var content = Memory3D<byte>.Allocate(size, size, size);
        var span = content.Span;
        var centre = (size - 1) * 0.5f;
        var radius = size * 0.3f;

        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var dx = x - centre;
                    var dy = y - centre;
                    var dz = z - centre;

                    if (dx * dx + dy * dy + dz * dz <= radius * radius)
                    {
                        span[x, y, z] = 255;
                    }
                }
            }
        }

        return content;
    }

    private static Memory3D<byte> SolidBelow(int sizeX, int sizeY, int sizeZ, int surfaceY)
    {
        var content = Memory3D<byte>.Allocate(sizeX, sizeY, sizeZ);
        var span = content.Span;

        for (var z = 0; z < sizeZ; z++)
        {
            for (var x = 0; x < sizeX; x++)
            {
                for (var y = 0; y < surfaceY; y++)
                {
                    span[x, y, z] = 255;
                }
            }
        }

        return content;
    }

    private static Memory3D<byte> SingleMaterial(int sizeX, int sizeY, int sizeZ, byte material)
    {
        var labels = Memory3D<byte>.Allocate(sizeX, sizeY, sizeZ);
        var span = labels.Span;

        for (var z = 0; z < sizeZ; z++)
        {
            for (var y = 0; y < sizeY; y++)
            {
                for (var x = 0; x < sizeX; x++)
                {
                    span[x, y, z] = material;
                }
            }
        }

        return labels;
    }

    private static void Mesh(
        Memory3D<byte> content,
        Memory3D<byte> material,
        Vector3i cellMin,
        Vector3i cellCount,
        SurfaceFairingSettings fairing,
        List<VoxelVertex> vertices,
        List<int> indices,
        List<Vector3i>? cells = null
    )
    {
        MultiLabelSurfaceNets.Mesh(new MultiLabelSurfaceNetsDescription
        {
            Content = content,
            Material = material,
            IsoLevel = IsoLevel,
            CellMin = cellMin,
            CellCount = cellCount,
            Vertices = vertices,
            Indices = indices,
            VertexCells = cells,
            Fairing = fairing
        });
    }

    [Fact]
    public void GeometryMatchesSurfaceNetsWhenFairingIsOff()
    {
        const int size = 16;
        var content = Sphere(size);
        var material = SingleMaterial(size, size, size, SurfaceMaterial.Rock);

        var expectedVertices = new List<SurfaceNetsVertex>();
        var expectedIndices = new List<int>();

        SurfaceNetsMesher.Mesh(new SurfaceNetsMeshingDescription
        {
            Content = content,
            IsoLevel = IsoLevel,
            Vertices = expectedVertices,
            Indices = expectedIndices
        });

        var vertices = new List<VoxelVertex>();
        var indices = new List<int>();

        Mesh(content, material, new Vector3i(2), new Vector3i(size - 1 - 3), default, vertices, indices);

        Assert.NotEmpty(expectedVertices);
        Assert.Equal(expectedVertices.Count, vertices.Count);
        Assert.Equal(expectedIndices, indices);

        for (var index = 0; index < expectedVertices.Count; index++)
        {
            Assert.Equal(expectedVertices[index].Position.X, vertices[index].Position.X, 5);
            Assert.Equal(expectedVertices[index].Position.Y, vertices[index].Position.Y, 5);
            Assert.Equal(expectedVertices[index].Position.Z, vertices[index].Position.Z, 5);
            Assert.Equal(SurfaceMaterial.Rock, vertices[index].Material);
        }
    }

    [Fact]
    public void MaterialFollowsTheSolidCorners()
    {
        const int size = 16;

        var content = SolidBelow(size, size, size, 8);
        var material = Memory3D<byte>.Allocate(size, size, size);
        var span = material.Span;

        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    span[x, y, z] = x < size / 2 ? SurfaceMaterial.Ice : SurfaceMaterial.Snow;
                }
            }
        }

        var vertices = new List<VoxelVertex>();
        var indices = new List<int>();

        Mesh(content, material, new Vector3i(2), new Vector3i(size - 1 - 3), default, vertices, indices);

        Assert.NotEmpty(indices);

        foreach (var vertex in vertices)
        {
            // Only the sides away from the split are unambiguous, because a cell that straddles it holds both materials:
            // ReSharper disable once PossibleLossOfFraction
            if (vertex.Position.X < size / 2 - 1)
            {
                Assert.Equal(SurfaceMaterial.Ice, vertex.Material);
            }
            // ReSharper disable once PossibleLossOfFraction
            else if (vertex.Position.X > size / 2 + 1)
            {
                Assert.Equal(SurfaceMaterial.Snow, vertex.Material);
            }
        }

        Assert.Contains(vertices, vertex => vertex.Material == SurfaceMaterial.Ice);
        Assert.Contains(vertices, vertex => vertex.Material == SurfaceMaterial.Snow);
    }

    [Fact]
    public void RelaxStaysWithinTheAllowedRange()
    {
        const int size = 32;
        const float allowedRange = 0.3f;

        var content = Sphere(size);
        var material = SingleMaterial(size, size, size, SurfaceMaterial.Rock);

        var vertices = new List<VoxelVertex>();
        var indices = new List<int>();
        var cells = new List<Vector3i>();

        Mesh(
            content,
            material,
            new Vector3i(8),
            new Vector3i(16),
            new SurfaceFairingSettings
            {
                AllowedRange = allowedRange,
                StepSize = 0.5f,
                Iterations = 4
            },
            vertices,
            indices,
            cells
        );

        Assert.NotEmpty(vertices);
        Assert.Equal(vertices.Count, cells.Count);

        for (var index = 0; index < vertices.Count; index++)
        {
            var center = new Vector3(cells[index].X + 0.5f, cells[index].Y + 0.5f, cells[index].Z + 0.5f);
            var offset = vertices[index].Position - center;

            // The reference clamps each axis to a box, not to a sphere:
            Assert.True(MathF.Abs(offset.X) <= allowedRange + 1e-4f, $"Vertex {index} X moved {offset.X}.");
            Assert.True(MathF.Abs(offset.Y) <= allowedRange + 1e-4f, $"Vertex {index} Y moved {offset.Y}.");
            Assert.True(MathF.Abs(offset.Z) <= allowedRange + 1e-4f, $"Vertex {index} Z moved {offset.Z}.");
        }
    }

    /// <summary>
    ///     Two chunks sharing a boundary must compute the shared vertices identically, which is why relaxation is
    ///     Jacobi with one cell of margin per pass. A Gauss-Seidel pass or a smaller margin shows up as a seam.
    /// </summary>
    [Fact]
    public void ChunkBoundariesAgreeWithRelaxation()
    {
        const int sizeX = 64;
        const int sizeY = 32;
        const int sizeZ = 32;
        const int relaxIterations = 3;

        var content = SolidBelow(sizeX, sizeY, sizeZ, 16);
        var material = Memory3D<byte>.Allocate(sizeX, sizeY, sizeZ);
        var span = material.Span;

        for (var z = 0; z < sizeZ; z++)
        {
            for (var y = 0; y < sizeY; y++)
            {
                for (var x = 0; x < sizeX; x++)
                {
                    // The split sits on the chunk boundary, so the shared cells are junctions:
                    span[x, y, z] = x < 24 ? SurfaceMaterial.Ice : SurfaceMaterial.Snow;
                }
            }
        }

        var fairing = new SurfaceFairingSettings
        {
            AllowedRange = 0.5f,
            StepSize = 0.5f,
            Iterations = relaxIterations
        };

        var cellCount = new Vector3i(16, 12, 12);
        var halo = new Vector3i(relaxIterations);

        var leftCellMin = new Vector3i(8, 8, 8);
        var leftBox = SurfaceNetsMesher.GetSampleRange(leftCellMin - halo, cellCount + halo + halo);
        var leftVertices = new List<VoxelVertex>();
        var leftIndices = new List<int>();
        var leftCells = new List<Vector3i>();

        Mesh(content, material, leftCellMin - leftBox.Min, cellCount, fairing, leftVertices, leftIndices, leftCells);

        var rightCellMin = new Vector3i(24, 8, 8);
        var rightBox = SurfaceNetsMesher.GetSampleRange(rightCellMin - halo, cellCount + halo + halo);
        var rightVertices = new List<VoxelVertex>();
        var rightIndices = new List<int>();
        var rightCells = new List<Vector3i>();

        Mesh(content, material, rightCellMin - rightBox.Min, cellCount, fairing, rightVertices, rightIndices, rightCells);

        var rightByCell = new Dictionary<(int, int, int), Vector3>();

        for (var index = 0; index < rightVertices.Count; index++)
        {
            var worldCell = rightBox.Min + rightCells[index];
            rightByCell[(worldCell.X, worldCell.Y, worldCell.Z)] = rightBox.Min + rightVertices[index].Position;
        }

        var shared = 0;

        for (var index = 0; index < leftVertices.Count; index++)
        {
            var worldCell = leftBox.Min + leftCells[index];

            if (worldCell.X != 23 && worldCell.X != 24)
            {
                continue;
            }

            if (!rightByCell.TryGetValue((worldCell.X, worldCell.Y, worldCell.Z), out var rightPosition))
            {
                continue;
            }

            shared++;
            var leftWorld = leftBox.Min + leftVertices[index].Position;

            Assert.Equal(rightPosition.X, leftWorld.X, 5);
            Assert.Equal(rightPosition.Y, leftWorld.Y, 5);
            Assert.Equal(rightPosition.Z, leftWorld.Z, 5);
        }

        Assert.True(shared > 0, "The two chunks share no vertex, so the test proves nothing.");
    }
}
