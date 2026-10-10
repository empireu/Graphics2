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

        EmitQuadDirect(corners, true, out var vertices, out var indices);

        // The fan appends one apex and every triangle provokes it, so one material per vertex still stands in for one material per quad:
        Assert.Equal(5, vertices.Count);
        Assert.Equal(12, indices.Count);
        Assert.Equal(vertices[0].Material, vertices[4].Material);

        for (var triangle = 0; triangle < 4; triangle++)
        {
            Assert.Equal(4, indices[triangle * 3]);
        }

        Assert.True(PointInQuad(corners, vertices[4].Position), "The fan apex is not strictly inside the quad, so its triangles spill outside it.");

        // The apex takes the corners' mean normal, not i0's:
        Assert.Equal(0.0f, vertices[4].Normal.X, 5);
        Assert.Equal(1.0f, vertices[4].Normal.Y, 5);
        Assert.Equal(0.0f, vertices[4].Normal.Z, 5);
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

        EmitQuadDirect(corners, true, out var vertices, out var indices);

        Assert.Equal(4, vertices.Count);
        Assert.Equal(new[] { 0, 1, 2, 0, 2, 3 }, indices);
    }

    /// <summary>
    ///     A brush sphere resting on flat ground leaves concave quads around its base. An apex on its edge's own crossing sat up to a quarter voxel below the relaxed corners, a pit in the ground.
    /// </summary>
    [Fact]
    public void FanApexesStayOnTheirCorners()
    {
        const int size = 48;
        const float radius = 4.0f;
        var sphereCentre = new Vector3(24.75f, 17.0f, 24.25f);

        var content = HeightField(size, size, size, (_, _) => 16.3f);
        var material = SolidMaterial(content, SurfaceMaterial.Snow);
        var contentSpan = content.Span;
        var materialSpan = material.Span;

        // The debug brush's fill, which writes its material only where a sample turns solid:
        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var distance = ((new Vector3(x, y, z) - sphereCentre).Length() - radius) * WorldConstants.VoxelSize;
                    var brushContent = DensitySampler.ContentFromDensity(-distance, 0);

                    if (brushContent <= contentSpan[x, y, z])
                    {
                        continue;
                    }

                    if (contentSpan[x, y, z] <= IsoLevel && brushContent > IsoLevel)
                    {
                        materialSpan[x, y, z] = SurfaceMaterial.Rock;
                    }

                    contentSpan[x, y, z] = brushContent;
                }
            }
        }

        var vertices = new List<VoxelVertex>();
        var indices = new List<int>();
        var cells = new List<Vector3i>();

        Mesh(content, material, new Vector3i(8), new Vector3i(32), new SectionMeshingDescription().Fairing, vertices, indices, cells);

        // A cell's base vertex comes first and its material copies share its position, so a vertex of the cell elsewhere is a fan apex:
        var baseByCell = new Dictionary<Vector3i, int>();

        for (var index = 0; index < vertices.Count; index++)
        {
            baseByCell.TryAdd(cells[index], index);
        }

        var fanNormals = new Dictionary<int, Vector3>();
        var cornerSums = new Dictionary<int, Vector3>();

        // Every fan triangle starts at the apex, and each of the quad's corners comes second in exactly one of them:
        for (var triangle = 0; triangle < indices.Count; triangle += 3)
        {
            var apex = indices[triangle];
            var apexPosition = vertices[apex].Position;

            if (vertices[baseByCell[cells[apex]]].Position == apexPosition)
            {
                continue;
            }

            var a = vertices[indices[triangle + 1]].Position;
            var b = vertices[indices[triangle + 2]].Position;

            fanNormals[apex] = fanNormals.GetValueOrDefault(apex) + Vector3.Cross(a - apexPosition, b - apexPosition);
            cornerSums[apex] = cornerSums.GetValueOrDefault(apex) + a;
        }

        Assert.True(fanNormals.Count > 0, "No quad was fanned, so the test proves nothing.");

        var worstDepth = 0.0f;

        foreach (var (apex, normal) in fanNormals)
        {
            var depth = Vector3.Dot(vertices[apex].Position - cornerSums[apex] * 0.25f, Vector3.Normalize(normal));
            worstDepth = MathF.Max(worstDepth, MathF.Abs(depth));
        }

        Assert.True(worstDepth < 0.1f, $"A fan apex lies {worstDepth} voxels off its corners.");
    }

    /// <summary>
    ///     A brush sphere dug out of solid ground. While every vertex had to stay inside its own cell, a vertex whose cell the surface only grazes stayed on the cell's corner as the relaxation drew its neighbours into the air, and the folds around it drew dark stars in the ambient occlusion.
    /// </summary>
    [Fact]
    public void DugSphereHasNoSharpFolds()
    {
        const int size = 48;
        const float radius = 4.0f;
        var sphereCentre = new Vector3(24.4f, 24.28f, 24.48f);

        var content = HeightField(size, size, size, (_, _) => size + 8.0f);
        var material = SolidMaterial(content, SurfaceMaterial.Rock);
        var contentSpan = content.Span;
        var materialSpan = material.Span;

        // The debug brush's dig, which leaves the null material wherever a sample turns to air:
        for (var z = 0; z < size; z++)
        {
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var distance = ((new Vector3(x, y, z) - sphereCentre).Length() - radius) * WorldConstants.VoxelSize;
                    var brushContent = DensitySampler.ContentFromDensity(distance, 0);

                    if (brushContent >= contentSpan[x, y, z])
                    {
                        continue;
                    }

                    contentSpan[x, y, z] = brushContent;

                    if (brushContent <= IsoLevel)
                    {
                        materialSpan[x, y, z] = SurfaceMaterial.Null;
                    }
                }
            }
        }

        var vertices = new List<VoxelVertex>();
        var indices = new List<int>();

        Mesh(content, material, new Vector3i(8), new Vector3i(32), new SectionMeshingDescription().Fairing, vertices, indices);

        // Each edge by its two end positions, so a cell's coincident material copies meet as one edge:
        var trianglesByEdge = new Dictionary<(Vector3, Vector3), List<int>>();

        for (var triangle = 0; triangle < indices.Count; triangle += 3)
        {
            for (var corner = 0; corner < 3; corner++)
            {
                var a = vertices[indices[triangle + corner]].Position;
                var b = vertices[indices[triangle + (corner + 1) % 3]].Position;
                var key = a.X < b.X || a.X == b.X && (a.Y < b.Y || a.Y == b.Y && a.Z < b.Z) ? (a, b) : (b, a);

                if (!trianglesByEdge.TryGetValue(key, out var triangles))
                {
                    triangles = [];
                    trianglesByEdge[key] = triangles;
                }

                triangles.Add(triangle);
            }
        }

        var worstFold = 0.0f;
        var edgeCount = 0;

        foreach (var ((a, b), triangles) in trianglesByEdge)
        {
            if (triangles.Count != 2)
            {
                continue;
            }

            var first = TriangleNormal(vertices, indices, triangles[0]);
            var second = TriangleNormal(vertices, indices, triangles[1]);

            if (first == Vector3.Zero || second == Vector3.Zero)
            {
                continue;
            }

            // A fold is concave when the second triangle's far corner lies on the air side of the first:
            var farCorner = Enumerable.Range(0, 3).Select(corner => vertices[indices[triangles[1] + corner]].Position).First(position => position != a && position != b);

            if (Vector3.Dot(farCorner - a, first) <= 0.0f)
            {
                continue;
            }

            worstFold = MathF.Max(worstFold, MathF.Acos(Math.Clamp(Vector3.Dot(first, second), -1.0f, 1.0f)) * 180.0f / MathF.PI);
            edgeCount++;
        }

        Assert.True(edgeCount > 100, "The dig left too few concave edges, so the test proves nothing.");
        Assert.True(worstFold < 40.0f, $"Two triangles of the dug sphere fold {worstFold} degrees into each other.");
    }

    private static Vector3 TriangleNormal(List<VoxelVertex> vertices, List<int> indices, int triangle)
    {
        var a = vertices[indices[triangle]].Position;
        var cross = Vector3.Cross(vertices[indices[triangle + 1]].Position - a, vertices[indices[triangle + 2]].Position - a);

        return cross.LengthSquared() < 1e-18f ? Vector3.Zero : Vector3.Normalize(cross);
    }

    /// <summary>
    ///     Calls the emitter for one cell's edge-0 quad. The four cells are seeded so the fan can register its apex against the emitting cell the way the pass does.
    /// </summary>
    private static void EmitQuadDirect(
        Vector3[] corners,
        bool solidBase,
        out List<VoxelVertex> vertices,
        out List<int> indices
    )
    {
        var cell = new Vector3i(1, 1, 1);

        vertices = new List<VoxelVertex>();

        // The normals tilt apart around the vertical, so their mean points straight up:
        for (var index = 0; index < corners.Length; index++)
        {
            vertices.Add(new VoxelVertex
            {
                Position = corners[index],
                Normal = Vector3.Normalize(new Vector3(index - 1.5f, 1.0f, 0.0f)),
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

        MultiLabelSurfaceNets.EmitQuad(vertices, cell, 0, 0, 1, 2, 3, solidBase, indices, vertexCells, ref state);
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
