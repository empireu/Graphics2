using System.Numerics;
using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.World;
using WinterGame.World.Generation.GraphProgram;
using WinterGame.World.Level.Meshing;
using WinterGame.World.Mesher;

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

    /// <summary>
    ///     Solid below a height in samples, encoded the way the generator encodes a distance, so it saturates one sample either side of the surface.
    /// </summary>
    private static Memory3D<byte> HeightField(int sizeX, int sizeY, int sizeZ, Func<int, int, float> height)
    {
        var content = Memory3D<byte>.Allocate(sizeX, sizeY, sizeZ);
        var span = content.Span;

        for (var z = 0; z < sizeZ; z++)
        {
            for (var x = 0; x < sizeX; x++)
            {
                var surface = height(x, z);

                for (var y = 0; y < sizeY; y++)
                {
                    span[x, y, z] = DensitySampler.ContentFromDensity((surface - y) * WorldConstants.VoxelSize, 0);
                }
            }
        }

        return content;
    }

    /// <summary>
    ///     One material on the solid samples and air on the rest, as the generator labels them, so the faces the surface crosses are surface crossings and the vertices relax.
    /// </summary>
    private static Memory3D<byte> SolidMaterial(Memory3D<byte> content, byte material)
    {
        var labels = Memory3D<byte>.Allocate(content.SizeX, content.SizeY, content.SizeZ);
        var contentSpan = content.Span;
        var span = labels.Span;

        for (var z = 0; z < content.SizeZ; z++)
        {
            for (var y = 0; y < content.SizeY; y++)
            {
                for (var x = 0; x < content.SizeX; x++)
                {
                    span[x, y, z] = contentSpan[x, y, z] > IsoLevel ? material : SurfaceMaterial.Null;
                }
            }
        }

        return labels;
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
        var storage = new MultiLabelSurfaceNetsStateStorage(cellCount, Math.Max(0, fairing.Iterations));

        MultiLabelSurfaceNets.Mesh(storage, new MultiLabelSurfaceNetsDescription
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

    /// <summary>
    ///     A normal taken from the saturated content field tilts in bands along a slope's contour lines, several degrees off at the middle of a cell.
    /// </summary>
    [Fact]
    public void NormalsFollowASlopedPlane()
    {
        const int size = 32;
        var content = HeightField(size, size, size, (x, z) => 10.3f + 0.3f * x + 0.2f * z);
        var material = SolidMaterial(content, SurfaceMaterial.Rock);
        var expected = Vector3.Normalize(new Vector3(-0.3f, 1.0f, -0.2f));

        var vertices = new List<VoxelVertex>();
        var indices = new List<int>();

        Mesh(content, material, new Vector3i(8), new Vector3i(16), new SectionMeshingDescription().Fairing, vertices, indices);

        Assert.NotEmpty(vertices);

        var worstDegrees = 0.0f;

        foreach (var vertex in vertices)
        {
            var degrees = MathF.Acos(Math.Clamp(Vector3.Dot(vertex.Normal, expected), -1.0f, 1.0f)) * 180.0f / MathF.PI;
            worstDegrees = MathF.Max(worstDegrees, degrees);
        }

        Assert.True(worstDegrees < 0.5f, $"A normal is {worstDegrees} degrees off the plane's.");
    }

    /// <summary>
    ///     A normal reads the quads around its vertex, so two units agree on a shared vertex's normal only when the corners of those quads are relaxed exactly in both.
    /// </summary>
    [Fact]
    public void ChunkBoundaryNormalsAgree()
    {
        const int sizeX = 64;
        const int sizeY = 32;
        const int sizeZ = 32;

        var content = HeightField(sizeX, sizeY, sizeZ, (x, z) => 16.0f + 2.5f * MathF.Sin(0.45f * x) + 2.0f * MathF.Cos(0.35f * z));
        var material = SolidMaterial(content, SurfaceMaterial.Rock);

        var fairing = new SurfaceFairingSettings
        {
            AllowedRange = 0.5f,
            StepSize = 0.5f,
            Iterations = 3
        };

        var cellCount = new Vector3i(16, 16, 12);

        var leftVertices = new List<VoxelVertex>();
        var leftCells = new List<Vector3i>();
        Mesh(content, material, new Vector3i(8, 8, 8), cellCount, fairing, leftVertices, new List<int>(), leftCells);

        var rightVertices = new List<VoxelVertex>();
        var rightCells = new List<Vector3i>();
        Mesh(content, material, new Vector3i(24, 8, 8), cellCount, fairing, rightVertices, new List<int>(), rightCells);

        // A cell's base vertex comes before any copy of it, so the first vertex per cell is the base:
        var rightByCell = new Dictionary<Vector3i, VoxelVertex>();

        for (var index = 0; index < rightVertices.Count; index++)
        {
            rightByCell.TryAdd(rightCells[index], rightVertices[index]);
        }

        var shared = 0;
        var seen = new HashSet<Vector3i>();

        for (var index = 0; index < leftVertices.Count; index++)
        {
            if (!seen.Add(leftCells[index]) || !rightByCell.TryGetValue(leftCells[index], out var right))
            {
                continue;
            }

            shared++;

            Assert.Equal(right.Position.X, leftVertices[index].Position.X, 5);
            Assert.Equal(right.Position.Y, leftVertices[index].Position.Y, 5);
            Assert.Equal(right.Position.Z, leftVertices[index].Position.Z, 5);
            Assert.Equal(right.Normal.X, leftVertices[index].Normal.X, 5);
            Assert.Equal(right.Normal.Y, leftVertices[index].Normal.Y, 5);
            Assert.Equal(right.Normal.Z, leftVertices[index].Normal.Z, 5);
        }

        Assert.True(shared > 0, "The two chunks share no vertex, so the test proves nothing.");
    }

    /// <summary>
    ///     The quad emitter is exercised directly because it is the one place a concave quad is detected and re-triangulated, and reaching it through
    ///     the mesher needs the fairing pushed hard enough that the test would depend on a scene. The four corners are one cell of the emitter's
    ///     edge-0 block each, which is the geometry the mesher always produces while a vertex stays inside its own cell.
    /// </summary>
    [Fact]
    public void ConcaveQuadFansAroundAnInteriorApex()
    {
        // The dart from the seam post-mortem: the p0-p2 diagonal lies outside it, which is what folded the old two-triangle split.
        var corners = new[]
        {
            new Vector3(1.5f, 1.76f, 1.24f),
            new Vector3(1.5f, 0.63f, 1.37f),
            new Vector3(1.5f, 0.90f, 0.10f),
            new Vector3(1.5f, 1.29f, 0.71f)
        };

        EmitQuadDirect(corners, 255, 0, true, out var vertices, out var indices);

        // The fan appends one apex and every triangle provokes it, so one material per vertex still stands in for one material per quad:
        Assert.Equal(5, vertices.Count);
        Assert.Equal(12, indices.Count);
        Assert.Equal(vertices[0].Material, vertices[4].Material);

        for (var triangle = 0; triangle < 4; triangle++)
        {
            Assert.Equal(4, indices[triangle * 3]);
        }

        Assert.True(PointInQuad(corners, vertices[4].Position), "The fan apex is not strictly inside the quad, so its triangles spill outside it.");
    }

    [Fact]
    public void ConvexQuadKeepsTheDiagonalSplit()
    {
        var corners = new[]
        {
            new Vector3(1.5f, 1.25f, 1.25f),
            new Vector3(1.5f, 0.75f, 1.25f),
            new Vector3(1.5f, 0.75f, 0.75f),
            new Vector3(1.5f, 1.25f, 0.75f)
        };

        EmitQuadDirect(corners, 255, 0, true, out var vertices, out var indices);

        Assert.Equal(4, vertices.Count);
        Assert.Equal(new[] { 0, 1, 2, 0, 2, 3 }, indices);
    }

    /// <summary>
    ///     Calls the emitter for one cell's edge-0 quad. The content bytes fix the crossing point on that edge, and the four cells are seeded so
    ///     the fan can register its apex against the emitting cell the way the pass does.
    /// </summary>
    private static void EmitQuadDirect(
        Vector3[] corners,
        byte sample0,
        byte sample1,
        bool solidBase,
        out List<VoxelVertex> vertices,
        out List<int> indices
    )
    {
        var cell = new Vector3i(1, 1, 1);
        var content = Memory3D<byte>.Allocate(4, 4, 4);
        var span = content.Span;

        // Edge 0 leaves corner 0 along +x, so these two samples place the quad's crossing apex:
        span[cell.X, cell.Y, cell.Z] = sample0;
        span[cell.X + 1, cell.Y, cell.Z] = sample1;

        Span<int> cornerOffsets = stackalloc int[8];

        for (var corner = 0; corner < 8; corner++)
        {
            cornerOffsets[corner] = (corner & 1) + ((corner >> 1) & 1) * span.SizeX + ((corner >> 2) & 1) * span.SizeX * span.SizeY;
        }

        vertices = new List<VoxelVertex>();

        for (var index = 0; index < corners.Length; index++)
        {
            vertices.Add(new VoxelVertex
            {
                Position = corners[index],
                Normal = Vector3.UnitY,
                Material = (uint)(SurfaceMaterial.Rock + index)
            });
        }

        indices = new List<int>();

        var vertexCells = new List<Vector3i>();
        var storage = new MultiLabelSurfaceNetsStateStorage(new Vector3i(4), 0);
        var state = storage.Begin();

        for (var index = 0; index < corners.Length; index++)
        {
            vertexCells.Add(cell);
            state.AddLocalCell(cell);
        }

        MultiLabelSurfaceNets.EmitQuad(vertices, span, cornerOffsets, cell, 0, IsoLevel, 0, 1, 2, 3, solidBase, indices, vertexCells, ref state);
    }

    /// <summary>
    ///     An independent point-in-polygon test, so the assertion does not lean on the mesher's own apex predicate. Edge 0 runs along x, so a fan
    ///     tiles in the (y, z) plane, and the crossing-number test is valid for the concave quad this exists to check.
    /// </summary>
    private static bool PointInQuad(Vector3[] corners, Vector3 point)
    {
        Span<Vector2> projected = stackalloc Vector2[4];

        for (var index = 0; index < 4; index++)
        {
            projected[index] = new Vector2(corners[index].Y, corners[index].Z);
        }

        var target = new Vector2(point.Y, point.Z);
        var inside = false;

        for (int current = 0, previous = 3; current < 4; previous = current++)
        {
            var a = projected[current];
            var b = projected[previous];

            if (a.Y > target.Y != b.Y > target.Y && target.X < (b.X - a.X) * (target.Y - a.Y) / (b.Y - a.Y) + a.X)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
