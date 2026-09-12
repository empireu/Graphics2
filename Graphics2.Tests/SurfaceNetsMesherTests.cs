using System.Numerics;
using Graphics2.Data;
using Graphics2.Mathematics;
using Graphics2.Voxels;

namespace Graphics2.Tests;

public class SurfaceNetsMesherTests
{
    private const byte Iso = 127;

    private static (List<SurfaceNetsVertex> Vertices, List<int> Indices) Mesh(Memory3D<byte> content, Vector3i? cellMin = null, Vector3i? cellCount = null)
    {
        var vertices = new List<SurfaceNetsVertex>();
        var indices = new List<int>();

        SurfaceNetsMesher.Mesh(new SurfaceNetsMeshingDescription
        {
            Content = content,
            IsoLevel = Iso,
            Vertices = vertices,
            Indices = indices,
            CellMin = cellMin,
            CellCount = cellCount
        });

        return (vertices, indices);
    }

    private static Memory3D<byte> DenseField(int size, byte value)
    {
        var grid = Memory3D<byte>.Allocate(size, size, size);
        grid.Span.Data.Fill(value);

        return grid;
    }

    private static Memory3D<byte> SphereField(int radius)
    {
        return PeanutField(radius, 0);
    }

    private static Memory3D<byte> PeanutField(int radius, int offset)
    {
        var grid = Memory3D<byte>.Allocate(23, 23, 23);
        var content = grid.Span;
        var center = new Vector3(grid.SizeX / 2.0f, grid.SizeY / 2.0f, grid.SizeZ / 2.0f);
        var shift = new Vector3(offset, 0.0f, 0.0f);

        for (var z = 0; z < grid.SizeZ; z++)
        {
            for (var y = 0; y < grid.SizeY; y++)
            {
                for (var x = 0; x < grid.SizeX; x++)
                {
                    var position = new Vector3(x, y, z);
                    var left = Vector3.Distance(position, center - shift) - radius;
                    var right = Vector3.Distance(position, center + shift) - radius;
                    var distance = MathF.Min(left, right);

                    content[x, y, z] = (byte)Math.Clamp(Iso - distance * Iso, 0.0f, 255.0f);
                }
            }
        }

        return grid;
    }

    private static Memory3D<byte> CarvedSphereField(int radius)
    {
        var grid = SphereField(radius);
        var content = grid.Span;
        var middle = grid.SizeX / 2;

        for (var x = middle - 2; x < middle + 1; x++)
        {
            for (var y = middle - 2; y < middle + 1; y++)
            {
                for (var z = 0; z < grid.SizeZ; z++)
                {
                    content.GetChecked(x, y, z) = 0;
                }
            }
        }

        return grid;
    }

    private static Vector3 Center(Memory3D<byte> field)
    {
        return new Vector3(field.SizeX / 2.0f, field.SizeY / 2.0f, field.SizeZ / 2.0f);
    }

    private static Dictionary<(int, int), int> EdgeUses(List<int> indices)
    {
        var uses = new Dictionary<(int, int), int>();

        for (var i = 0; i < indices.Count; i += 3)
        {
            AddEdge(uses, indices[i], indices[i + 1]);
            AddEdge(uses, indices[i + 1], indices[i + 2]);
            AddEdge(uses, indices[i + 2], indices[i]);
        }

        return uses;
    }

    private static void AddEdge(Dictionary<(int, int), int> uses, int a, int b)
    {
        var key = a < b ? (a, b) : (b, a);
        uses[key] = uses.GetValueOrDefault(key) + 1;
    }

    private static int EulerCharacteristic(List<SurfaceNetsVertex> vertices, List<int> indices)
    {
        return vertices.Count - EdgeUses(indices).Count + indices.Count / 3;
    }

    private static float SignedVolume(List<SurfaceNetsVertex> vertices, List<int> indices)
    {
        var volume = 0.0f;

        for (var i = 0; i < indices.Count; i += 3)
        {
            var a = vertices[indices[i]].Position;
            var b = vertices[indices[i + 1]].Position;
            var c = vertices[indices[i + 2]].Position;

            volume += Vector3.Dot(a, Vector3.Cross(b, c));
        }

        return volume / 6.0f;
    }

    private static void AssertIndicesValid(List<SurfaceNetsVertex> vertices, List<int> indices)
    {
        Assert.Equal(0, indices.Count % 3);

        foreach (var index in indices)
        {
            Assert.InRange(index, 0, vertices.Count - 1);
        }
    }

    private static void AssertClosedManifold(List<SurfaceNetsVertex> vertices, List<int> indices)
    {
        foreach (var (edge, uses) in EdgeUses(indices))
        {
            Assert.True(
                uses == 2,
                $"Edge {edge} is used {uses} times, expected 2. A={vertices[edge.Item1].Position} B={vertices[edge.Item2].Position}"
            );
        }
    }

    private static void AssertWindingPointsAwayFrom(List<SurfaceNetsVertex> vertices, List<int> indices, Vector3 origin)
    {
        for (var i = 0; i < indices.Count; i += 3)
        {
            var a = vertices[indices[i]].Position;
            var b = vertices[indices[i + 1]].Position;
            var c = vertices[indices[i + 2]].Position;

            var face = Vector3.Cross(b - a, c - a);
            var outward = (a + b + c) / 3.0f - origin;

            Assert.True(Vector3.Dot(face, outward) > 0.0f, $"Triangle {i / 3} is wound inward.");
        }
    }

    private static void AssertWindingAgreesWithNormals(List<SurfaceNetsVertex> vertices, List<int> indices)
    {
        for (var i = 0; i < indices.Count; i += 3)
        {
            var a = vertices[indices[i]];
            var b = vertices[indices[i + 1]];
            var c = vertices[indices[i + 2]];

            var face = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            var normal = a.Normal + b.Normal + c.Normal;

            Assert.True(Vector3.Dot(face, normal) > 0.0f, $"Triangle {i / 3} is wound against its vertex normals.");
        }
    }

    private static void AssertNormalsPointAwayFrom(List<SurfaceNetsVertex> vertices, Vector3 origin)
    {
        for (var i = 0; i < vertices.Count; i++)
        {
            Assert.True(
                Vector3.Dot(vertices[i].Normal, vertices[i].Position - origin) > 0.0f,
                $"Vertex {i} has an inward normal."
            );
        }
    }

    [Fact]
    public void EmptyField_EmitsNothing()
    {
        var (vertices, indices) = Mesh(DenseField(7, 0));

        Assert.Empty(vertices);
        Assert.Empty(indices);
    }

    [Fact]
    public void FullField_EmitsNothing()
    {
        var (vertices, indices) = Mesh(DenseField(7, 255));

        Assert.Empty(vertices);
        Assert.Empty(indices);
    }

    [Fact]
    public void FieldAtExactlyIsoLevel_EmitsNothing()
    {
        var (vertices, indices) = Mesh(DenseField(7, Iso));

        Assert.Empty(vertices);
        Assert.Empty(indices);
    }

    [Fact]
    public void LoneVoxel_IsAClosedHexahedron()
    {
        var field = DenseField(7, 0);
        field.Span[3, 3, 3] = 255;

        var (vertices, indices) = Mesh(field);

        AssertIndicesValid(vertices, indices);
        Assert.Equal(8, vertices.Count);
        Assert.Equal(6, indices.Count / 6);
        AssertClosedManifold(vertices, indices);
        Assert.Equal(2, EulerCharacteristic(vertices, indices));
        AssertWindingPointsAwayFrom(vertices, indices, new Vector3(3, 3, 3));
    }

    [Fact]
    public void Sphere_IsAClosedOutwardSurface()
    {
        var field = SphereField(5);
        var (vertices, indices) = Mesh(field);

        AssertIndicesValid(vertices, indices);
        AssertClosedManifold(vertices, indices);
        Assert.Equal(2, EulerCharacteristic(vertices, indices));
        Assert.True(SignedVolume(vertices, indices) > 0.0f, "The sphere encloses a negative volume.");
        AssertWindingPointsAwayFrom(vertices, indices, Center(field));
        AssertWindingAgreesWithNormals(vertices, indices);
        AssertNormalsPointAwayFrom(vertices, Center(field));
    }

    [Fact]
    public void Peanut_IsAClosedOutwardSurface()
    {
        var field = PeanutField(5, 2);
        var (vertices, indices) = Mesh(field);

        AssertIndicesValid(vertices, indices);
        AssertClosedManifold(vertices, indices);
        Assert.Equal(2, EulerCharacteristic(vertices, indices));
        Assert.True(SignedVolume(vertices, indices) > 0.0f, "The peanut encloses a negative volume.");
        AssertWindingPointsAwayFrom(vertices, indices, Center(field));
        AssertWindingAgreesWithNormals(vertices, indices);
    }

    [Fact]
    public void CarvedSphere_PinchesWhereThreeSurfacesMeetInsideOneVoxel()
    {
        var field = CarvedSphereField(8);
        var (vertices, indices) = Mesh(field);

        AssertIndicesValid(vertices, indices);
        Assert.NotEmpty(indices);
        Assert.True(SignedVolume(vertices, indices) > 0.0f, "The carved sphere encloses a negative volume.");
        AssertWindingAgreesWithNormals(vertices, indices);
    }

    [Fact]
    public void DefaultRange_MatchesTheRangeTheDemoPasses()
    {
        var field = SphereField(5);

        var implicitRange = Mesh(field);
        var explicitRange = Mesh(
            field,
            new Vector3i(2, 2, 2),
            new Vector3i(field.SizeX - 4, field.SizeY - 4, field.SizeZ - 4)
        );

        Assert.Equal(explicitRange.Vertices.Count, implicitRange.Vertices.Count);
        Assert.Equal(explicitRange.Indices, implicitRange.Indices);
        Assert.Equal(480, implicitRange.Indices.Count / 6);
    }

    [Fact]
    public void VertexPositions_DoNotDependOnTheMeshedRange()
    {
        var field = SphereField(6);

        var (wideVertices, wideIndices) = Mesh(field);
        var (narrowVertices, narrowIndices) = Mesh(field, new Vector3i(3, 3, 3), new Vector3i(11, 11, 11));

        AssertIndicesValid(narrowVertices, narrowIndices);
        Assert.NotEmpty(narrowVertices);

        var wide = new HashSet<Vector3>(wideVertices.Select(vertex => vertex.Position));

        foreach (var vertex in narrowVertices)
        {
            Assert.Contains(vertex.Position, wide);
        }

        Assert.True(wideVertices.Count > narrowVertices.Count);
        Assert.True(wideIndices.Count > narrowIndices.Count);
    }

    [Fact]
    public void SubRange_NeverEmitsAnIndexOutsideTheTraversal()
    {
        var field = SphereField(6);

        var (vertices, indices) = Mesh(field, new Vector3i(4, 4, 4), new Vector3i(9, 9, 9));

        AssertIndicesValid(vertices, indices);
        Assert.NotEmpty(vertices);
        Assert.NotEmpty(indices);
    }
}
