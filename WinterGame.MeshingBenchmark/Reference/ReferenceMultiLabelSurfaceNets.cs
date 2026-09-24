
using System.Numerics;
using System.Runtime.CompilerServices;
using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.World;

/*
Multi-label surface nets: a dual-grid mesher that takes a content field (a signed distance sampled to bytes) and a material label per sample, and produces a triangle mesh with one material per triangle.

The geometry is exactly SurfaceNetsMesher. The material half and the relaxation are from:
    S. Frisken, "SurfaceNets for Multi-Label Segmentations with Preservation of Sharp Boundaries",
    Journal of Computer Graphics Techniques, 11(1), 2022.

1. The dual grid
    The samples form a lattice, and eight neighboring samples form one cell. 
    A cell whose eight content bytes straddle the iso level contains part of the surface and emits exactly one vertex. 
    The cell's twelve edges are then examined: an edge whose two endpoint samples sit on opposite sides of the iso level is a crossing edge, and every crossing edge emits one quad.

    A quad joins the vertices of the four cells that share its edge. 
    So that every grid edge is emitted exactly once, a cell owns only the three edges that leave its low corner, along x, y and z. 
    Those are bits 0, 1 and 2 of the cell's edge mask, which is why CubeEdges lists those three first. 
    The owner is therefore the cell at the edge's low corner, and it is that cell's vertex which becomes the quad's first vertex.

    The quad's winding comes from the sign of its cell's corner 0. 
    EmitQuad fans both triangles of the quad from the cell's own vertex and flips the triangle order when corner 0 is air, so the surface faces outward either way.

    Corner indices pack their lattice offset into three bits: x in bit 0, y in bit 1, z in bit 2. 
    Corner 0 is therefore the cell's low corner and corner 7 its high corner.

2. Where the vertex goes
    The vertex sits at the average of the crossing points on the cell's twelve edges, each crossing placed by linear interpolation between its two samples.
    That is the content field's own answer for where the surface is, and it is what makes the mesh fit the terrain.
    The reference instead starts every vertex at its cell center and lets the relaxation move it, which is right for pure label data because a label says nothing between samples.
    We have the content field, so we use it and let the relaxation only round the result off.

    The vertex normal is the central difference of the content field at each of the eight corners, blended trilinearly to the vertex and negated, because content grows toward the solid.

3. Which material a quad gets
    A quad takes the material of the sample on its own generating edge: the edge joins two corners, one solid and one air, and the solid one carries the material. 
    That is the reference's rule.

    A majority vote over the cell's solid corners is tempting and wrong. 
    Where the surface runs along an axis, as it does on a cut wall, the cell's solid corners span two samples in each of the other two axes. 
    So the vote is really a 2x2 block, and it hands the quad whichever side holds more corners. 
    Adjacent cells then take each other's material and the boundary reads as a sawtooth. 
    On the initial prototype's cut wall, that was 81 of 1258 quads, in a strictly alternating two-column pattern.

    A cell's three owned edges can carry three different materials, so a cell that sees more than one gets one vertex per material, all coincident. 
    That is the only duplication the scheme needs: a cell whose own edges agree keeps a single vertex. 
    Only the cell's own vertex is ever the provoking vertex for its quads, because EmitQuad fans both triangles from that vertex.
    So, a neighbor can keep referencing the cell's first vertex and no quad can accidentally pick up a duplicate's material.

4. Classification, which decides how a vertex relaxes
    Each of the cell's six faces is classified from its four corner labels:

        no crossing         the four corners hold at most one label
        surface crossing    exactly two labels, and not on opposite corners
        junction crossing   three or more labels, or two labels on opposite corners

    The cell's vertex type then comes from the count of junction faces among the six:

        surface vertex   0 junction faces   relax toward every neighbor across an intersecting face
        edge vertex      1 or 2             relax toward neighbors across junction faces only
        corner vertex    3 or more          the same as an edge vertex

    Relaxing an edge vertex only toward its junction neighbors is the whole point: it slides the vertex along the material edge rather than smoothing across it, so the boundary stays sharp while the surrounding surface rounds off.

5. Relaxation, and why it is chunk-safe
    A vertex moves a fraction of the way toward the average of its eligible neighbors, then is clamped on each axis to a box of AllowedRange around its cell center. 
    The clamp is the accuracy bound, and it is what stops smoothing from moving the surface away from where the data put it. 
    It is applied to every vertex, including one with no in-range neighbor, so the bound always holds.

    The update is Jacobi, meaning it reads the previous iteration and writes a separate buffer, not Gauss-Seidel.
    That matters for us. A Jacobi pass reaches exactly one cell further per iteration, so two units that each carry Iterations cells of vertices around the cells they emit compute identical values for the vertices they share.
    A Gauss-Seidel sweep depends on visit order, so two units would disagree and a crease would appear along every unit boundary.
    This is why the vertex range is CellMin - 1 - Iterations through CellMin + CellCount + Iterations, and why the caller has to supply the samples for it.

    The per-material duplicates are made after the relaxation and copy the base vertex's position, so they cannot drift away from the base they belong to.

6. The passes, in order

        pass 1   traverse the vertex range, create one base vertex per crossing cell, and record per cell the edge mask, the material of each owned edge, the sign of corner 0, the six face types and the vertex type
        relax    move the base vertices
        pass 2   for every cell this call owns, emit one quad per crossing owned edge, creating a duplicate vertex for any edge whose material differs from the first
        compact  drop the relaxation's outer halo, which no quad references

    The caller's content and material boxes must cover the whole vertex range plus one sample on every side, because the normal stencil reads one sample past the lowest and the highest vertex cells.
    The margin per axis is therefore 1 + Iterations cells below the emit range and Iterations above it.
    At 32 cells per unit and 4 iterations that is a 36 to 44 sample box per axis, about 1.8x the samples, and that margin is the cost of the fairing.
*/

namespace WinterGame.MeshingBenchmark.Reference;

/// <summary>
///     One surface vertex.
///     The material is the material of the sample on this vertex's own generating edge, read through the provoking vertex.
/// </summary>
public struct ReferenceVoxelVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public uint Material;
}

/// <summary>
///     Surface relaxation parameters. A relaxed vertex stays within <see cref="AllowedRange"/> of its cell center on each axis.
/// </summary>
public struct ReferenceSurfaceFairingSettings
{
    /// <summary>
    ///     Voxels a vertex may sit from its cell center on each axis. The accuracy bound.
    /// </summary>
    public float AllowedRange { get; init; }

    /// <summary>
    ///     Fraction of the way to the neighbor average taken each iteration.
    /// </summary>
    public float StepSize { get; init; }

    public int Iterations { get; init; }
}

public sealed class ReferenceMultiLabelSurfaceNetsDescription
{
    /// <summary>
    ///     Content field, one byte per sample, padded so that the vertex range plus the gradient stencil fits.
    /// </summary>
    public required Memory3D<byte> Content { get; init; }

    /// <summary>
    ///     Material label per sample, the same shape as <see cref="Content" />. Air is <see cref="SurfaceMaterial.Null" />.
    /// </summary>
    public required Memory3D<byte> Material { get; init; }

    /// <summary>
    ///     Content above this level counts as solid.
    /// </summary>
    public required byte IsoLevel { get; init; }

    /// <summary>
    ///     First cell to emit quads for, in the content box's own cell coordinates.
    /// </summary>
    public required Vector3i CellMin { get; init; }

    /// <summary>
    ///     Number of cells to emit quads for on each axis.
    /// </summary>
    public required Vector3i CellCount { get; init; }

    /// <summary>
    ///     The vertices generated. Must be empty when the call starts, because a vertex index is its position here.
    /// </summary>
    public required List<ReferenceVoxelVertex> Vertices { get; init; }

    /// <summary>
    ///     Indices into <see cref="Vertices" />. Three per triangle, six per quad. Must be empty when the call starts.
    /// </summary>
    public required List<int> Indices { get; init; }

    /// <summary>
    ///     Records the cell each vertex came from, in the content box's own cell coordinates.
    /// </summary>
    public List<Vector3i>? VertexCells { get; init; }

    public ReferenceSurfaceFairingSettings Fairing { get; init; }
}

public static class ReferenceMultiLabelSurfaceNets
{
    /// <summary>
    ///     Corner offsets are packed as bits, x in bit 0, y in bit 1, z in bit 2.
    /// </summary>
    private static readonly int[] CubeEdges = new int[24];
    private static readonly int[] EdgeTable = new int[256];

    // The three base edges of a cell, as the corner the edge leaves corner 0 for.
    // Base edge 0 leaves along x, 1 along y, 2 along z, so they are bits 0, 1 and 2 of the cell's edge mask.
    // Only these three are owned by the cell, which is what makes every grid edge produce exactly one quad.
    private static readonly int[] BaseEdgeCorner = [1, 2, 4];

    // The four corners of each of the six faces, in a cyclic order around the face, which is what makes corners 0 and 2 opposite and corners 1 and 3 opposite.
    // ClassifyFace uses that to catch the two-materials-on-a-diagonal case, which a plain label count cannot see.
    // Face order is left, right, back, front, bottom, top.
    private static readonly int[] FaceCorners =
    [
        0, 4, 6, 2,
        1, 5, 7, 3,
        0, 1, 3, 2,
        4, 5, 7, 6,
        0, 1, 5, 4,
        2, 3, 7, 6
    ];

    // The neighbor cell across each face, in the same face order.
    private static readonly int[] FaceOffsetX = [-1, 1, 0, 0, 0, 0];
    private static readonly int[] FaceOffsetY = [0, 0, 0, 0, -1, 1];
    private static readonly int[] FaceOffsetZ = [0, 0, -1, 1, 0, 0];

    private const int FaceCount = 6;
    private const int BaseEdgeCount = 3;

    private enum FaceCrossingType
    {
        None = 0,
        Surface = 1,
        Junction = 2
    }

    private enum VertexType
    {
        None = 0,
        Surface = 1,
        Edge = 2,
        Corner = 3
    }

    static ReferenceMultiLabelSurfaceNets()
    {
        // The 12 cube edges as corner index pairs, with the three edges from corner 0 first:
        var index = 0;
        
        for (var corner = 0; corner < 8; corner++)
        {
            for (var bit = 1; bit <= 4; bit <<= 1)
            {
                var other = corner ^ bit;

                if (corner <= other)
                {
                    CubeEdges[index++] = corner;
                    CubeEdges[index++] = other;
                }
            }
        }

        for (var mask = 0; mask < 256; mask++)
        {
            var edges = 0;

            for (var edge = 0; edge < 12; edge++)
            {
                var a = (mask >> CubeEdges[edge * 2]) & 1;
                var b = (mask >> CubeEdges[edge * 2 + 1]) & 1;

                if (a != b)
                {
                    edges |= 1 << edge;
                }
            }

            EdgeTable[mask] = edges;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Mesh(ReferenceMultiLabelSurfaceNetsDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(description.Vertices);
        ArgumentNullException.ThrowIfNull(description.Indices);

        if (description.Content.IsEmpty)
        {
            throw new ArgumentException("The content box is empty.", nameof(description));
        }

        if (description.Material.IsEmpty)
        {
            throw new ArgumentException("The material box is empty.", nameof(description));
        }

        if (description.Material.SizeX != description.Content.SizeX ||
            description.Material.SizeY != description.Content.SizeY ||
            description.Material.SizeZ != description.Content.SizeZ)
        {
            throw new ArgumentException("The material box must match the content box.", nameof(description));
        }

        var cells = new Vector3i(description.Content.SizeX - 1, description.Content.SizeY - 1, description.Content.SizeZ - 1);

        ArgumentOutOfRangeException.ThrowIfLessThan(description.CellCount.X, 1, nameof(description.CellCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(description.CellCount.Y, 1, nameof(description.CellCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(description.CellCount.Z, 1, nameof(description.CellCount));

        // A relaxation pass reaches one cell further, and a quad reaches one cell below the emit range, so the vertex range is that much wider.
        // Two units then compute the same value for the cells they share.
        // The extra cells are traversal and relaxation work only. Compact drops them before the caller sees them.
        var relaxHalo = Math.Max(0, description.Fairing.Iterations);
        var vertexMin = description.CellMin - new Vector3i(1 + relaxHalo);
        var vertexMax = description.CellMin + description.CellCount + new Vector3i(relaxHalo);

        ArgumentOutOfRangeException.ThrowIfLessThan(vertexMin.X - 1, 0, nameof(description.CellMin));
        ArgumentOutOfRangeException.ThrowIfLessThan(vertexMin.Y - 1, 0, nameof(description.CellMin));
        ArgumentOutOfRangeException.ThrowIfLessThan(vertexMin.Z - 1, 0, nameof(description.CellMin));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(vertexMax.X + 1, cells.X, nameof(description.CellCount));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(vertexMax.Y + 1, cells.Y, nameof(description.CellCount));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(vertexMax.Z + 1, cells.Z, nameof(description.CellCount));

        var content = description.Content.Span;
        var material = description.Material.Span;
        var iso = description.IsoLevel;
        var vertices = description.Vertices;
        var indices = description.Indices;
        var vertexCells = description.VertexCells;

        var range = vertexMax - vertexMin;
        var cellIndices = new int[range.X * range.Y * range.Z];
        Array.Fill(cellIndices, -1);

        var localCells = new List<Vector3i>();
        var cellCenters = new List<Vector3>();
        var vertexTypes = new List<VertexType>();
        var faceTypes = new List<FaceCrossingType>();
        var edgeMasks = new List<int>();
        var solidBases = new List<bool>();
        var edgeMaterials = new List<uint>();

        Span<float> cornerValues = stackalloc float[8];
        Span<int> materialCounts = stackalloc int[SurfaceMaterial.Count];
        Span<FaceCrossingType> cellFaces = stackalloc FaceCrossingType[FaceCount];
        Span<uint> baseMaterials = stackalloc uint[BaseEdgeCount];

        for (var lz = vertexMin.Z; lz < vertexMax.Z; lz++)
        {
            for (var ly = vertexMin.Y; ly < vertexMax.Y; ly++)
            {
                for (var lx = vertexMin.X; lx < vertexMax.X; lx++)
                {
                    var mask = 0;
                    materialCounts.Clear();

                    for (var corner = 0; corner < 8; corner++)
                    {
                        var cx = lx + (corner & 1);
                        var cy = ly + ((corner >> 1) & 1);
                        var cz = lz + ((corner >> 2) & 1);

                        var value = content[cx, cy, cz];
                        cornerValues[corner] = value;

                        if (value > iso)
                        {
                            mask |= 1 << corner;
                        }

                        var id = material[cx, cy, cz];

                        if (id != SurfaceMaterial.Null && id < SurfaceMaterial.Count)
                        {
                            materialCounts[id]++;
                        }
                    }

                    // A cell entirely on one side of the iso level holds no surface:
                    if (mask is 0 or 255)
                    {
                        continue;
                    }

                    // The face and vertex types come from the labels, so a material junction is seen even where the content is uniform:
                    for (var face = 0; face < FaceCount; face++)
                    {
                        cellFaces[face] = ClassifyFace(
                            LabelAt(material, lx, ly, lz, FaceCorners[face * 4]),
                            LabelAt(material, lx, ly, lz, FaceCorners[face * 4 + 1]),
                            LabelAt(material, lx, ly, lz, FaceCorners[face * 4 + 2]),
                            LabelAt(material, lx, ly, lz, FaceCorners[face * 4 + 3])
                        );
                    }

                    var edgeMask = EdgeTable[mask];

                    // Each base edge takes the material of its own solid endpoint sample, which is the reference's rule.
                    // The two corners of a crossing edge differ in sign, so exactly one of them is solid, and that sample's label is the material of the quad the edge will generate.
                    for (var edge = 0; edge < BaseEdgeCount; edge++)
                    {
                        var otherCorner = BaseEdgeCorner[edge];
                        var solidA = (mask & 1) != 0;
                        var solidB = (mask & (1 << otherCorner)) != 0;

                        if (solidA == solidB)
                        {
                            baseMaterials[edge] = SurfaceMaterial.Null;
                            continue;
                        }

                        var solidCorner = solidA ? 0 : otherCorner;
                        baseMaterials[edge] = material[
                            lx + (solidCorner & 1),
                            ly + ((solidCorner >> 1) & 1),
                            lz + ((solidCorner >> 2) & 1)
                        ];
                    }

                    // The first crossing base edge seeds the cell's single base vertex, so that edge's quad can use the base directly and only a later edge with a different material needs a duplicate.
                    // A cell with no crossing owned edge still gets a base vertex, because its neighbors' quads reference it, and then any material will do.
                    var firstMaterial = (uint)SurfaceMaterial.Null;

                    for (var edge = 0; edge < BaseEdgeCount; edge++)
                    {
                        if (baseMaterials[edge] != SurfaceMaterial.Null)
                        {
                            firstMaterial = baseMaterials[edge];
                            break;
                        }
                    }

                    if (firstMaterial == SurfaceMaterial.Null)
                    {
                        firstMaterial = DominantMaterial(materialCounts);
                    }

                    var sumX = 0.0f;
                    var sumY = 0.0f;
                    var sumZ = 0.0f;
                    var crossings = 0;
                    var remaining = edgeMask;

                    while (remaining != 0)
                    {
                        var edge = BitOperations.TrailingZeroCount(remaining);
                        remaining &= remaining - 1;

                        var cornerA = CubeEdges[edge * 2];
                        var cornerB = CubeEdges[edge * 2 + 1];
                        var valueA = cornerValues[cornerA];
                        var valueB = cornerValues[cornerB];

                        // The two corners differ in exactly one axis, found from the XOR.
                        // t is the fraction along that axis, and the other two coordinates are simply the corner's own bits, so the crossing point is assembled per axis rather than with a full corner subtraction.
                        var t = (iso - valueA) / (valueB - valueA);
                        var axis = BitOperations.TrailingZeroCount(cornerA ^ cornerB);

                        var bitX = (cornerA & 1) != 0;
                        var bitY = (cornerA & 2) != 0;
                        var bitZ = (cornerA & 4) != 0;

                        // ReSharper disable ArrangeRedundantParentheses
                        sumX += axis == 0 ? (bitX ? 1.0f - t : t) : (bitX ? 1.0f : 0.0f);
                        sumY += axis == 1 ? (bitY ? 1.0f - t : t) : (bitY ? 1.0f : 0.0f);
                        sumZ += axis == 2 ? (bitZ ? 1.0f - t : t) : (bitZ ? 1.0f : 0.0f);
                        // ReSharper restore ArrangeRedundantParentheses

                        crossings++;
                    }

                    var local = new Vector3(sumX / crossings, sumY / crossings, sumZ / crossings);
                    var vertexIndex = vertices.Count;

                    // The neighbor map is indexed by a local cell, row-major over the vertex range, so a lookup is a plain offset and the arrays never grow with the world coordinate:
                    cellIndices[(lz - vertexMin.Z) * range.X * range.Y + (ly - vertexMin.Y) * range.X + (lx - vertexMin.X)] = vertexIndex;
                    localCells.Add(new Vector3i(lx, ly, lz));
                    cellCenters.Add(new Vector3(lx + 0.5f, ly + 0.5f, lz + 0.5f));
                    vertexTypes.Add(ClassifyVertex(cellFaces));
                    edgeMasks.Add(edgeMask);
                    solidBases.Add(cornerValues[0] > iso);

                    for (var edge = 0; edge < BaseEdgeCount; edge++)
                    {
                        edgeMaterials.Add(baseMaterials[edge]);
                    }

                    for (var face = 0; face < FaceCount; face++)
                    {
                        faceTypes.Add(cellFaces[face]);
                    }

                    vertexCells?.Add(new Vector3i(lx, ly, lz));

                    vertices.Add(new ReferenceVoxelVertex
                    {
                        Position = new Vector3(lx + local.X, ly + local.Y, lz + local.Z),
                        Normal = VertexNormal(lx, ly, lz, local, content),
                        Material = firstMaterial
                    });
                }
            }
        }

        // The base vertices are relaxed before the per-material duplicates are made, so a duplicate copies a relaxed position rather than being relaxed twice:
        Relax(vertices, cellIndices, range, vertexMin, localCells, cellCenters, vertexTypes, faceTypes, description.Fairing);

        var baseVertexCount = vertices.Count;
        Span<uint> materials = stackalloc uint[BaseEdgeCount];
        Span<int> edgeVertices = stackalloc int[BaseEdgeCount];

        // Pass 2. Quads are emitted only for the cells this call owns, so two units never emit the same quad.
        // A cell whose owned edges disagree on material gets one vertex per material, and each quad takes its own edge's vertex as i0.
        // The quad's other three corners are the neighboring cells' first vertices: every one of those cells contains both samples of the crossing edge and so has a vertex of its own, which is why the lookup cannot come back -1 here.
        for (var cellOrder = 0; cellOrder < baseVertexCount; cellOrder++)
        {
            var cell = localCells[cellOrder];

            if (cell.X < description.CellMin.X || cell.Y < description.CellMin.Y || cell.Z < description.CellMin.Z ||
                cell.X >= description.CellMin.X + description.CellCount.X ||
                cell.Y >= description.CellMin.Y + description.CellCount.Y ||
                cell.Z >= description.CellMin.Z + description.CellCount.Z)
            {
                continue;
            }

            var materialCount = 0;

            for (var edge = 0; edge < BaseEdgeCount; edge++)
            {
                if ((edgeMasks[cellOrder] & (1 << edge)) == 0)
                {
                    continue;
                }

                var edgeMaterial = edgeMaterials[cellOrder * BaseEdgeCount + edge];

                // A crossing edge whose solid sample carried no material cannot happen for well-formed data, so this only guards a bad label box and keeps the quad on the cell's own material.
                if (edgeMaterial == SurfaceMaterial.Null)
                {
                    edgeMaterial = vertices[cellOrder].Material;
                }

                var slot = -1;

                for (var existing = 0; existing < materialCount; existing++)
                {
                    if (materials[existing] == edgeMaterial)
                    {
                        slot = existing;
                        break;
                    }
                }

                if (slot < 0)
                {
                    slot = materialCount++;

                    if (slot == 0)
                    {
                        materials[slot] = edgeMaterial;
                        edgeVertices[slot] = cellOrder;

                        // The first crossing edge's material is written onto the base vertex, so its quad uses the base and only the later, differently-shaded edges need a duplicate.
                        var vertex = vertices[cellOrder];
                        vertex.Material = edgeMaterial;
                        vertices[cellOrder] = vertex;
                    }
                    else
                    {
                        materials[slot] = edgeMaterial;
                        edgeVertices[slot] = AddDuplicate(vertices, localCells, vertexCells, cellOrder, edgeMaterial);
                    }
                }

                var i0 = edgeVertices[slot];
                var solidBase = solidBases[cellOrder];

                if (edge == 0)
                {
                    EmitQuad(
                        i0,
                        CellIndex(cellIndices, range, vertexMin, cell.X, cell.Y - 1, cell.Z),
                        CellIndex(cellIndices, range, vertexMin, cell.X, cell.Y - 1, cell.Z - 1),
                        CellIndex(cellIndices, range, vertexMin, cell.X, cell.Y, cell.Z - 1),
                        solidBase,
                        indices
                    );
                }
                else if (edge == 1)
                {
                    EmitQuad(
                        i0,
                        CellIndex(cellIndices, range, vertexMin, cell.X, cell.Y, cell.Z - 1),
                        CellIndex(cellIndices, range, vertexMin, cell.X - 1, cell.Y, cell.Z - 1),
                        CellIndex(cellIndices, range, vertexMin, cell.X - 1, cell.Y, cell.Z),
                        solidBase,
                        indices
                    );
                }
                else
                {
                    EmitQuad(
                        i0,
                        CellIndex(cellIndices, range, vertexMin, cell.X - 1, cell.Y, cell.Z),
                        CellIndex(cellIndices, range, vertexMin, cell.X - 1, cell.Y - 1, cell.Z),
                        CellIndex(cellIndices, range, vertexMin, cell.X, cell.Y - 1, cell.Z),
                        solidBase,
                        indices
                    );
                }
            }
        }

        // The outer relax halo exists only to give the shared vertices their full neighborhood, so it is dropped from what the caller uploads.
        // Quads never reach past one cell below the emit range, so nothing references it.
        Compact(vertices, indices, localCells, vertexCells, description.CellMin - new Vector3i(1), description.CellMin + description.CellCount);
    }

    /// <summary>
    ///     Appends a copy of a cell's vertex carrying a different material. The position and normal are copied, so the
    ///     duplicates of a cell are coincident and only the provoking material differs.
    /// </summary>
    private static int AddDuplicate(List<ReferenceVoxelVertex> vertices, List<Vector3i> localCells, List<Vector3i>? vertexCells, int cellOrder, uint material)
    {
        var index = vertices.Count;
        var vertex = vertices[cellOrder];
        vertex.Material = material;

        vertices.Add(vertex);
        localCells.Add(localCells[cellOrder]);
        vertexCells?.Add(vertexCells[cellOrder]);

        return index;
    }

    /// <summary>
    ///     Drops the relaxation's outer halo, which nothing references, and rewrites the index buffer through the remap.
    ///     The keep range is exactly the emit range plus one cell below it, because a quad reaches one cell down in the two axes its edge does not run along and never reaches up at all.
    ///     A referenced vertex outside the range would remap to -1, which as a ushort is 65535, so the range is a correctness boundary and not a heuristic.
    /// </summary>
    private static void Compact(
        List<ReferenceVoxelVertex> vertices,
        List<int> indices,
        List<Vector3i> localCells,
        List<Vector3i>? vertexCells,
        Vector3i keepMin,
        Vector3i keepMax
    )
    {
        var remap = new int[vertices.Count];
        var write = 0;

        for (var index = 0; index < vertices.Count; index++)
        {
            var cell = localCells[index];

            if (cell.X < keepMin.X || cell.Y < keepMin.Y || cell.Z < keepMin.Z ||
                cell.X >= keepMax.X || cell.Y >= keepMax.Y || cell.Z >= keepMax.Z)
            {
                remap[index] = -1;
                continue;
            }

            remap[index] = write;
            vertices[write] = vertices[index];

            if (vertexCells != null)
            {
                vertexCells[write] = vertexCells[index];
            }

            write++;
        }

        if (write == vertices.Count)
        {
            return;
        }

        for (var index = 0; index < indices.Count; index++)
        {
            indices[index] = remap[indices[index]];
        }

        vertices.RemoveRange(write, vertices.Count - write);
        vertexCells?.RemoveRange(write, vertexCells.Count - write);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CellIndex(int[] cellIndices, Vector3i range, Vector3i vertexMin, int lx, int ly, int lz)
    {
        return cellIndices[(lz - vertexMin.Z) * range.X * range.Y + (ly - vertexMin.Y) * range.X + (lx - vertexMin.X)];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static byte LabelAt(Span3D<byte> material, int lx, int ly, int lz, int corner)
    {
        return material[
            lx + (corner & 1),
            ly + ((corner >> 1) & 1),
            lz + ((corner >> 2) & 1)
        ];
    }

    /// <summary>
    ///     Classifies one face from its four corner labels.
    ///     Three or more labels is a junction.
    ///     Two labels count as a junction when they sit on opposite corners, because the boundary then crosses the face diagonally and a plain count would mistake it for a clean surface crossing.
    /// </summary>
    private static FaceCrossingType ClassifyFace(byte c0, byte c1, byte c2, byte c3)
    {
        Span<byte> types = stackalloc byte[4];
        var unique = 0;

        AddUnique(types, ref unique, c0);
        AddUnique(types, ref unique, c1);
        AddUnique(types, ref unique, c2);
        AddUnique(types, ref unique, c3);

        if (unique <= 1)
        {
            return FaceCrossingType.None;
        }

        if (unique == 2)
        {
            return c0 == c2 && c1 == c3
                ? FaceCrossingType.Junction
                : FaceCrossingType.Surface;
        }

        return FaceCrossingType.Junction;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddUnique(Span<byte> types, ref int unique, byte value)
    {
        for (var index = 0; index < unique; index++)
        {
            if (types[index] == value)
            {
                return;
            }
        }

        types[unique++] = value;
    }

    private static VertexType ClassifyVertex(ReadOnlySpan<FaceCrossingType> faces)
    {
        var crossings = 0;
        var junctions = 0;

        for (var face = 0; face < FaceCount; face++)
        {
            if (faces[face] == FaceCrossingType.None)
            {
                continue;
            }

            crossings++;

            if (faces[face] == FaceCrossingType.Junction)
            {
                junctions++;
            }
        }

        if (crossings == 0)
        {
            return VertexType.None;
        }

        if (junctions == 0)
        {
            return VertexType.Surface;
        }

        return junctions <= 2 ? VertexType.Edge : VertexType.Corner;
    }

    /// <summary>
    ///     The most common solid material among a cell's corners, with ties going to the lower id.
    /// </summary>
    private static uint DominantMaterial(ReadOnlySpan<int> counts)
    {
        var bestId = 0;
        var bestCount = 0;

        for (var id = 0; id < counts.Length; id++)
        {
            if (counts[id] > bestCount)
            {
                bestCount = counts[id];
                bestId = id;
            }
        }

        return (uint)bestId;
    }

    /// <summary>
    ///     Moves every vertex toward the average of its neighbors, bounded by the cell radius on each axis.
    ///     A surface vertex uses every intersecting face; an edge or corner vertex uses only junction faces, so a material edge or corner slides along itself and stays sharp.
    /// </summary>
    private static void Relax(
        List<ReferenceVoxelVertex> vertices,
        int[] cellIndices,
        Vector3i range,
        Vector3i vertexMin,
        List<Vector3i> localCells,
        List<Vector3> cellCenters,
        List<VertexType> vertexTypes,
        List<FaceCrossingType> faceTypes,
        ReferenceSurfaceFairingSettings settings
    )
    {
        if (settings.Iterations <= 0 || settings.StepSize <= 0.0f || settings.AllowedRange <= 0.0f || vertices.Count == 0)
        {
            return;
        }

        var allowedRange = settings.AllowedRange;

        // Jacobi needs the previous iteration to stay intact while the next one is written, hence two buffers and a swap rather than an in-place sweep.
        // This is the property that makes two units agree on their shared cells.
        var current = new Vector3[vertices.Count];
        var next = new Vector3[vertices.Count];

        for (var index = 0; index < vertices.Count; index++)
        {
            current[index] = vertices[index].Position;
        }

        for (var iteration = 0; iteration < settings.Iterations; iteration++)
        {
            for (var index = 0; index < vertices.Count; index++)
            {
                var cell = localCells[index];
                var vertexType = vertexTypes[index];
                var sum = Vector3.Zero;
                var count = 0;

                for (var face = 0; face < FaceCount; face++)
                {
                    var crossingType = faceTypes[index * FaceCount + face];

                    // A surface vertex is pulled by every face the surface crosses, so it smooths freely.
                    // An edge or corner vertex is pulled only along its junction faces, so it slides along the material boundary instead of being drawn across it, which is what keeps the boundary sharp.
                    var useFace = vertexType == VertexType.Surface
                        ? crossingType != FaceCrossingType.None
                        : crossingType == FaceCrossingType.Junction;

                    if (!useFace)
                    {
                        continue;
                    }

                    var nx = cell.X + FaceOffsetX[face];
                    var ny = cell.Y + FaceOffsetY[face];
                    var nz = cell.Z + FaceOffsetZ[face];

                    if (nx < vertexMin.X || ny < vertexMin.Y || nz < vertexMin.Z || nx >= vertexMin.X + range.X || ny >= vertexMin.Y + range.Y || nz >= vertexMin.Z + range.Z)
                    {
                        continue;
                    }

                    var neighbourIndex = CellIndex(cellIndices, range, vertexMin, nx, ny, nz);

                    if (neighbourIndex < 0)
                    {
                        continue;
                    }

                    sum += current[neighbourIndex];
                    count++;
                }

                var position = count == 0
                    ? current[index]
                    : current[index] + settings.StepSize * (sum / count - current[index]);

                // The clamp applies to every vertex, so a vertex with no in-range neighbor still respects the bound:
                var center = cellCenters[index];

                position.X = Math.Clamp(position.X, center.X - allowedRange, center.X + allowedRange);
                position.Y = Math.Clamp(position.Y, center.Y - allowedRange, center.Y + allowedRange);
                position.Z = Math.Clamp(position.Z, center.Z - allowedRange, center.Z + allowedRange);

                next[index] = position;
            }

            (current, next) = (next, current);
        }

        for (var index = 0; index < vertices.Count; index++)
        {
            var vertex = vertices[index];
            vertex.Position = current[index];
            vertices[index] = vertex;
        }
    }

    /// <summary>
    ///     Central difference of the content field at one sample point.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 CornerGradient(int sx, int sy, int sz, Span3D<byte> content)
    {
        return new Vector3(
            content[sx + 1, sy, sz] - content[sx - 1, sy, sz],
            content[sx, sy + 1, sz] - content[sx, sy - 1, sz],
            content[sx, sy, sz + 1] - content[sx, sy, sz - 1]
        );
    }

    /// <summary>
    ///     Blends the gradients of the 8 cell corners to the vertex, negated because content grows toward the solid.
    /// </summary>
    private static Vector3 VertexNormal(int sx, int sy, int sz, Vector3 local, Span3D<byte> content)
    {
        var g000 = CornerGradient(sx, sy, sz, content);
        var g100 = CornerGradient(sx + 1, sy, sz, content);
        var g010 = CornerGradient(sx, sy + 1, sz, content);
        var g110 = CornerGradient(sx + 1, sy + 1, sz, content);
        var g001 = CornerGradient(sx, sy, sz + 1, content);
        var g101 = CornerGradient(sx + 1, sy, sz + 1, content);
        var g011 = CornerGradient(sx, sy + 1, sz + 1, content);
        var g111 = CornerGradient(sx + 1, sy + 1, sz + 1, content);

        var g0 = Vector3.Lerp(
            Vector3.Lerp(g000, g100, local.X),
            Vector3.Lerp(g010, g110, local.X),
            local.Y
        );

        var g1 = Vector3.Lerp(
            Vector3.Lerp(g001, g101, local.X),
            Vector3.Lerp(g011, g111, local.X),
            local.Y
        );

        var outward = -Vector3.Lerp(g0, g1, local.Z);

        return outward.LengthSquared() > 1e-12f
            ? Vector3.Normalize(outward)
            : Vector3.UnitY;
    }

    /// <summary>
    ///     Records a quad as two triangles.
    ///     The order flips with the base corner sign so the normals face outward.
    ///     Both triangles start at i0, which is what makes i0 the provoking vertex for both of them and therefore what lets one material per vertex stand in for one material per quad.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void EmitQuad(int i0, int i1, int i2, int i3, bool solidBase, List<int> indices)
    {
        indices.Add(i0);

        if (solidBase)
        {
            indices.Add(i1);
            indices.Add(i2);
            indices.Add(i0);
            indices.Add(i2);
            indices.Add(i3);
        }
        else
        {
            indices.Add(i2);
            indices.Add(i1);
            indices.Add(i0);
            indices.Add(i3);
            indices.Add(i2);
        }
    }
}
