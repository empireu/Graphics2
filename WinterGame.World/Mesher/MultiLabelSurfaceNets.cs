using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Graphics2.Data;
using Graphics2.Mathematics;

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

namespace WinterGame.World.Mesher;

/// <summary>
///     One surface vertex.
///     The material is the material of the sample on this vertex's own generating edge, read through the provoking vertex.
/// </summary>
public struct VoxelVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public uint Material;
}

/// <summary>
///     Surface relaxation parameters. A relaxed vertex stays within <see cref="AllowedRange"/> of its cell center on each axis.
/// </summary>
public struct SurfaceFairingSettings
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

public sealed class MultiLabelSurfaceNetsDescription
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
    public required List<VoxelVertex> Vertices { get; init; }

    /// <summary>
    ///     Indices into <see cref="Vertices" />. Three per triangle, six per quad. Must be empty when the call starts.
    /// </summary>
    public required List<int> Indices { get; init; }

    /// <summary>
    ///     Records the cell each vertex came from, in the content box's own cell coordinates.
    /// </summary>
    public List<Vector3i>? VertexCells { get; init; }

    public SurfaceFairingSettings Fairing { get; init; }
}

public static class MultiLabelSurfaceNets
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

    internal const int FaceCount = 6;
    internal const int BaseEdgeCount = 3;
    private const int SimdWidth = 32;

    public enum FaceCrossingType : byte
    {
        None = 0,
        Surface = 1,
        Junction = 2
    }

    public enum VertexType : byte
    {
        None = 0,
        Surface = 1,
        Edge = 2,
        Corner = 3
    }

    static MultiLabelSurfaceNets()
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
    public static void Mesh(MultiLabelSurfaceNetsStateStorage storage, MultiLabelSurfaceNetsDescription description)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(description.Vertices);
        ArgumentNullException.ThrowIfNull(description.Indices);

        if (storage.CellCount != description.CellCount)
        {
            throw new ArgumentException("The state was created for a different cell count than the description", nameof(storage));
        }

        var relaxHalo = Math.Max(0, description.Fairing.Iterations);

        if (storage.RelaxHalo != relaxHalo)
        {
            throw new ArgumentException("The state was created for a different relaxation iteration count than the description", nameof(storage));
        }

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
        var vertexMin = description.CellMin - new Vector3i(1 + relaxHalo);
        var vertexMax = description.CellMin + description.CellCount + new Vector3i(relaxHalo);

        ArgumentOutOfRangeException.ThrowIfLessThan(vertexMin.X - 1, 0, nameof(description.CellMin));
        ArgumentOutOfRangeException.ThrowIfLessThan(vertexMin.Y - 1, 0, nameof(description.CellMin));
        ArgumentOutOfRangeException.ThrowIfLessThan(vertexMin.Z - 1, 0, nameof(description.CellMin));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(vertexMax.X + 1, cells.X, nameof(description.CellCount));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(vertexMax.Y + 1, cells.Y, nameof(description.CellCount));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(vertexMax.Z + 1, cells.Z, nameof(description.CellCount));

        var range = vertexMax - vertexMin;
        var state = storage.Begin();

        try
        {
            MeshCore(ref state, description, vertexMin, vertexMax, range);
        }
        finally
        {
            state.Dispose();
        }
    }

    private static void MeshCore(
        ref MultiLabelSurfaceNetsStateStorage.State state,
        MultiLabelSurfaceNetsDescription description,
        Vector3i vertexMin,
        Vector3i vertexMax,
        Vector3i range
    )
    {
        var content = description.Content.Span;
        var material = description.Material.Span;
        var iso = description.IsoLevel;
        var vertices = description.Vertices;
        var indices = description.Indices;
        var vertexCells = description.VertexCells;

        Span<byte> cornerValues = stackalloc byte[8];
        Span<byte> cornerMaterials = stackalloc byte[8];
        Span<int> materialCounts = stackalloc int[SurfaceMaterial.Count];
        Span<FaceCrossingType> cellFaces = stackalloc FaceCrossingType[FaceCount];
        Span<uint> baseMaterials = stackalloc uint[BaseEdgeCount];
        Span<int> cornerOffsets = stackalloc int[8];

        var sampleStrideX = content.SizeX;
        var sampleStrideXy = sampleStrideX * content.SizeY;

        // A partial trailing block still loads a whole vector past its last cell, so the read has to stay inside the span:
        var simdMaxCellOffset = content.Length - sampleStrideX - sampleStrideXy - SimdWidth - 1;

        for (var corner = 0; corner < 8; corner++)
        {
            cornerOffsets[corner] = (corner & 1) + ((corner >> 1) & 1) * sampleStrideX + ((corner >> 2) & 1) * sampleStrideXy;
        }

        ref var contentRef = ref MemoryMarshal.GetReference(content.Data);

        for (var lz = vertexMin.Z; lz < vertexMax.Z; lz++)
        {
            for (var ly = vertexMin.Y; ly < vertexMax.Y; ly++)
            {
                var rowOffset = ly * sampleStrideX + lz * sampleStrideXy;
                var uniformBlockStart = vertexMin.X;
                var uniformBlockEnd = vertexMin.X;
                var uniformBits = 0u;

                for (var lx = vertexMin.X; lx < vertexMax.X; lx++)
                {
                    var cellOffset = lx + rowOffset;

                    // One vector test rejects a run of cells that cannot hold a surface, before any per-cell work:
                    if (Vector256.IsHardwareAccelerated && cellOffset <= simdMaxCellOffset)
                    {
                        if (lx >= uniformBlockEnd)
                        {
                            uniformBlockStart = lx;

                            var laneCount = Math.Min(SimdWidth, vertexMax.X - lx);
                            uniformBlockEnd = lx + laneCount;
                            var uniformValidBits = laneCount == SimdWidth ? uint.MaxValue : (1u << laneCount) - 1;
                            uniformBits = UniformCellBits(ref Unsafe.Add(ref contentRef, cellOffset), sampleStrideX, sampleStrideXy, iso) & uniformValidBits;

                            // A block with no crossing cell skips its whole run; the loop's own increment makes up the rest:
                            if (uniformBits == uniformValidBits)
                            {
                                lx = uniformBlockEnd - 1;
                                continue;
                            }
                        }

                        if (((uniformBits >> (lx - uniformBlockStart)) & 1u) != 0)
                        {
                            continue;
                        }
                    }

                    var mask = 0;

                    for (var corner = 0; corner < 8; corner++)
                    {
                        var value = content.GetUnsafeFlat(cellOffset + cornerOffsets[corner]);
                        cornerValues[corner] = value;

                        if (value > iso)
                        {
                            mask |= 1 << corner;
                        }
                    }

                    // A cell entirely on one side of the iso level holds no surface, so its labels are never read:
                    if (mask is 0 or 255)
                    {
                        continue;
                    }

                    materialCounts.Clear();

                    for (var corner = 0; corner < 8; corner++)
                    {
                        var id = material.GetUnsafeFlat(cellOffset + cornerOffsets[corner]);
                        cornerMaterials[corner] = id;

                        if (id != SurfaceMaterial.Null && id < SurfaceMaterial.Count)
                        {
                            materialCounts[id]++;
                        }
                    }

                    // The face and vertex types come from the labels, so a material junction is seen even where the content is uniform:
                    for (var face = 0; face < FaceCount; face++)
                    {
                        cellFaces[face] = ClassifyFace(
                            cornerMaterials[FaceCorners[face * 4]],
                            cornerMaterials[FaceCorners[face * 4 + 1]],
                            cornerMaterials[FaceCorners[face * 4 + 2]],
                            cornerMaterials[FaceCorners[face * 4 + 3]]
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
                        baseMaterials[edge] = cornerMaterials[solidCorner];
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
                        // The values are bytes, so the denominator is widened to float here to keep the division in floating point:
                        var t = (iso - valueA) / (float)(valueB - valueA);
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
                    state.SetCellIndex(CellOffset(range, vertexMin, lx, ly, lz), vertexIndex);
                    state.AddLocalCell(new Vector3i(lx, ly, lz));
                    state.AddCellCenter(new Vector3(lx + 0.5f, ly + 0.5f, lz + 0.5f));
                    state.AddVertexType(ClassifyVertex(cellFaces));
                    state.AddEdgeMask(edgeMask);
                    state.AddSolidBase(cornerValues[0] > iso);

                    for (var edge = 0; edge < BaseEdgeCount; edge++)
                    {
                        state.AddEdgeMaterial(baseMaterials[edge]);
                    }

                    for (var face = 0; face < FaceCount; face++)
                    {
                        state.AddFaceType(cellFaces[face]);
                    }

                    vertexCells?.Add(new Vector3i(lx, ly, lz));

                    vertices.Add(new VoxelVertex
                    {
                        Position = new Vector3(lx + local.X, ly + local.Y, lz + local.Z),
                        Normal = VertexNormal(lx, ly, lz, local, content),
                        Material = firstMaterial
                    });
                }
            }
        }

        // The base vertices are relaxed before the per-material duplicates are made, so a duplicate copies a relaxed position rather than being relaxed twice:
        Relax(ref state, vertices, range, vertexMin, description.Fairing);

        var baseVertexCount = vertices.Count;
        Span<uint> materials = stackalloc uint[BaseEdgeCount];
        Span<int> edgeVertices = stackalloc int[BaseEdgeCount];

        // Pass 2. Quads are emitted only for the cells this call owns, so two units never emit the same quad.
        // A cell whose owned edges disagree on material gets one vertex per material, and each quad takes its own edge's vertex as i0.
        // The quad's other three corners are the neighboring cells' first vertices: every one of those cells contains both samples of the crossing edge and so has a vertex of its own, which is why the lookup cannot come back -1 here.
        for (var cellOrder = 0; cellOrder < baseVertexCount; cellOrder++)
        {
            var cell = state.LocalCells(cellOrder);

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
                if ((state.EdgeMask(cellOrder) & (1 << edge)) == 0)
                {
                    continue;
                }

                var edgeMaterial = state.EdgeMaterial(cellOrder * BaseEdgeCount + edge);

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
                        edgeVertices[slot] = AddDuplicate(ref state, vertices, vertexCells, cellOrder, edgeMaterial);
                    }
                }

                var i0 = edgeVertices[slot];
                var solidBase = state.SolidBase(cellOrder);

                if (edge == 0)
                {
                    EmitQuad(
                        vertices,
                        content,
                        cornerOffsets,
                        cell,
                        edge,
                        iso,
                        i0,
                        CellVertexIndex(ref state, range, vertexMin, cell.X, cell.Y - 1, cell.Z),
                        CellVertexIndex(ref state, range, vertexMin, cell.X, cell.Y - 1, cell.Z - 1),
                        CellVertexIndex(ref state, range, vertexMin, cell.X, cell.Y, cell.Z - 1),
                        solidBase,
                        indices,
                        vertexCells,
                        ref state
                    );
                }
                else if (edge == 1)
                {
                    EmitQuad(
                        vertices,
                        content,
                        cornerOffsets,
                        cell,
                        edge,
                        iso,
                        i0,
                        CellVertexIndex(ref state, range, vertexMin, cell.X, cell.Y, cell.Z - 1),
                        CellVertexIndex(ref state, range, vertexMin, cell.X - 1, cell.Y, cell.Z - 1),
                        CellVertexIndex(ref state, range, vertexMin, cell.X - 1, cell.Y, cell.Z),
                        solidBase,
                        indices,
                        vertexCells,
                        ref state
                    );
                }
                else
                {
                    EmitQuad(
                        vertices,
                        content,
                        cornerOffsets,
                        cell,
                        edge,
                        iso,
                        i0,
                        CellVertexIndex(ref state, range, vertexMin, cell.X - 1, cell.Y, cell.Z),
                        CellVertexIndex(ref state, range, vertexMin, cell.X - 1, cell.Y - 1, cell.Z),
                        CellVertexIndex(ref state, range, vertexMin, cell.X, cell.Y - 1, cell.Z),
                        solidBase,
                        indices,
                        vertexCells,
                        ref state
                    );
                }
            }
        }

        // The outer relax halo exists only to give the shared vertices their full neighborhood, so it is dropped from what the caller uploads.
        // Quads never reach past one cell below the emit range, so nothing references it.
        Compact(ref state, vertices, indices, vertexCells, description.CellMin - new Vector3i(1), description.CellMin + description.CellCount);
    }

    /// <summary>
    ///     Appends a copy of a cell's vertex carrying a different material. The position and normal are copied, so the
    ///     duplicates of a cell are coincident and only the provoking material differs.
    /// </summary>
    private static int AddDuplicate(ref MultiLabelSurfaceNetsStateStorage.State state, List<VoxelVertex> vertices, List<Vector3i>? vertexCells, int cellOrder, uint material)
    {
        var index = vertices.Count;
        var vertex = vertices[cellOrder];
        vertex.Material = material;

        vertices.Add(vertex);
        state.AddLocalCell(state.LocalCells(cellOrder));
        vertexCells?.Add(vertexCells[cellOrder]);

        return index;
    }

    /// <summary>
    ///     Drops the relaxation's outer halo, which nothing references, and rewrites the index buffer through the remap.
    ///     The keep range is exactly the emit range plus one cell below it, because a quad reaches one cell down in the two axes its edge does not run along and never reaches up at all.
    ///     A referenced vertex outside the range would remap to -1, which as a ushort is 65535, so the range is a correctness boundary and not a heuristic.
    /// </summary>
    private static void Compact(
        ref MultiLabelSurfaceNetsStateStorage.State state,
        List<VoxelVertex> vertices,
        List<int> indices,
        List<Vector3i>? vertexCells,
        Vector3i keepMin,
        Vector3i keepMax
    )
    {
        var write = 0;

        for (var index = 0; index < vertices.Count; index++)
        {
            var cell = state.LocalCells(index);

            if (cell.X < keepMin.X || cell.Y < keepMin.Y || cell.Z < keepMin.Z ||
                cell.X >= keepMax.X || cell.Y >= keepMax.Y || cell.Z >= keepMax.Z)
            {
                state.Remap(index) = -1;
                continue;
            }

            state.Remap(index) = write;
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
            indices[index] = state.Remap(indices[index]);
        }

        vertices.RemoveRange(write, vertices.Count - write);
        vertexCells?.RemoveRange(write, vertexCells.Count - write);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CellOffset(Vector3i range, Vector3i vertexMin, int lx, int ly, int lz)
    {
        return (lz - vertexMin.Z) * range.X * range.Y + (ly - vertexMin.Y) * range.X + (lx - vertexMin.X);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CellVertexIndex(ref MultiLabelSurfaceNetsStateStorage.State state, Vector3i range, Vector3i vertexMin, int lx, int ly, int lz)
    {
        return state.CellIndex(CellOffset(range, vertexMin, lx, ly, lz));
    }

    /// <summary>
    ///     Resolves the vertex across each face once per vertex, so a Jacobi sweep only has to gather positions.
    ///     A face records -1 when the vertex does not pull on it, when the neighbor is outside the vertex range, or when that cell holds no vertex.
    ///     A surface vertex pulls on every face the surface crosses; an edge or corner vertex pulls only on its junction faces, so it slides along the material boundary instead of being drawn across it.
    /// </summary>
    private static void BuildRelaxNeighbors(ref MultiLabelSurfaceNetsStateStorage.State state, int baseVertexCount, Vector3i range, Vector3i vertexMin)
    {
        var maxX = vertexMin.X + range.X;
        var maxY = vertexMin.Y + range.Y;
        var maxZ = vertexMin.Z + range.Z;

        for (var index = 0; index < baseVertexCount; index++)
        {
            var cell = state.LocalCells(index);
            var vertexType = state.VertexType(index);

            for (var face = 0; face < FaceCount; face++)
            {
                var crossingType = state.FaceType(index * FaceCount + face);
                var useFace = vertexType == VertexType.Surface
                    ? crossingType != FaceCrossingType.None
                    : crossingType == FaceCrossingType.Junction;

                var neighbor = -1;

                if (useFace)
                {
                    var nx = cell.X + FaceOffsetX[face];
                    var ny = cell.Y + FaceOffsetY[face];
                    var nz = cell.Z + FaceOffsetZ[face];

                    if (nx >= vertexMin.X && ny >= vertexMin.Y && nz >= vertexMin.Z && nx < maxX && ny < maxY && nz < maxZ)
                    {
                        var neighborIndex = CellVertexIndex(ref state, range, vertexMin, nx, ny, nz);

                        if (neighborIndex >= 0)
                        {
                            neighbor = neighborIndex;
                        }
                    }
                }

                state.NeighborIndex(index * FaceCount + face) = neighbor;
            }
        }
    }

    /// <summary>
    ///     Returns one bit per lane, set where a cell of <see cref="SimdWidth"/> consecutive x positions has all eight of its samples on the same side of the iso level.
    ///     The four sample planes the corners come from are the row itself, the y+1 row, the z+1 row and the y+1,z+1 row.
    ///     The comparison is unsigned, which is the byte case <see cref="Vector256.GreaterThan{T}"/> handles correctly.
    ///     It reads a full vector regardless of how many lanes the caller uses, so the caller must prove the read stays inside the span.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static uint UniformCellBits(ref byte plane00, int strideX, int strideXy, byte iso)
    {
        var isoVector = Vector256.Create(iso);

        var s0 = Vector256.GreaterThan(Unsafe.ReadUnaligned<Vector256<byte>>(ref plane00), isoVector);
        var s1 = Vector256.GreaterThan(Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref plane00, 1)), isoVector);

        ref var plane10 = ref Unsafe.Add(ref plane00, strideX);
        var s2 = Vector256.GreaterThan(Unsafe.ReadUnaligned<Vector256<byte>>(ref plane10), isoVector);
        var s3 = Vector256.GreaterThan(Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref plane10, 1)), isoVector);

        ref var plane01 = ref Unsafe.Add(ref plane00, strideXy);
        var s4 = Vector256.GreaterThan(Unsafe.ReadUnaligned<Vector256<byte>>(ref plane01), isoVector);
        var s5 = Vector256.GreaterThan(Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref plane01, 1)), isoVector);

        ref var plane11 = ref Unsafe.Add(ref plane01, strideX);
        var s6 = Vector256.GreaterThan(Unsafe.ReadUnaligned<Vector256<byte>>(ref plane11), isoVector);
        var s7 = Vector256.GreaterThan(Unsafe.ReadUnaligned<Vector256<byte>>(ref Unsafe.Add(ref plane11, 1)), isoVector);

        var allSolid = s0 & s1 & s2 & s3 & s4 & s5 & s6 & s7;
        var anySolid = s0 | s1 | s2 | s3 | s4 | s5 | s6 | s7;

        return (allSolid | ~anySolid).ExtractMostSignificantBits();
    }

    /// <summary>
    ///     Classifies one face from its four corner labels.
    ///     Three or more labels is a junction.
    ///     Two labels count as a junction when they sit on opposite corners, because the boundary then crosses the face diagonally and a plain count would mistake it for a clean surface crossing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FaceCrossingType ClassifyFace(byte c0, byte c1, byte c2, byte c3)
    {
        var unique = 1;

        if (c1 != c0)
        {
            unique++;
        }

        if (c2 != c0 && c2 != c1)
        {
            unique++;
        }

        if (c3 != c0 && c3 != c1 && c3 != c2)
        {
            unique++;
        }

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
        ref MultiLabelSurfaceNetsStateStorage.State state,
        List<VoxelVertex> vertices,
        Vector3i range,
        Vector3i vertexMin,
        SurfaceFairingSettings settings
    )
    {
        if (settings.Iterations <= 0 || settings.StepSize <= 0.0f || settings.AllowedRange <= 0.0f || vertices.Count == 0)
        {
            return;
        }

        var allowedRange = settings.AllowedRange;

        // Resolve each vertex's face neighbors once, so the sweeps below do not repeat the bounds test and the cell map lookup.
        BuildRelaxNeighbors(ref state, vertices.Count, range, vertexMin);

        // Jacobi needs the previous iteration to stay intact while the next one is written, hence two buffers and a swap rather than an in-place sweep.
        // This is the property that makes two units agree on their shared cells.
        for (var index = 0; index < vertices.Count; index++)
        {
            state.Current(index) = vertices[index].Position;
        }

        for (var iteration = 0; iteration < settings.Iterations; iteration++)
        {
            for (var index = 0; index < vertices.Count; index++)
            {
                var sum = Vector3.Zero;
                var count = 0;

                for (var face = 0; face < FaceCount; face++)
                {
                    var neighborIndex = state.NeighborIndex(index * FaceCount + face);

                    if (neighborIndex < 0)
                    {
                        continue;
                    }

                    sum += state.Current(neighborIndex);
                    count++;
                }

                var position = count == 0
                    ? state.Current(index)
                    : state.Current(index) + settings.StepSize * (sum / count - state.Current(index));

                // The clamp applies to every vertex, so a vertex with no in-range neighbor still respects the bound:
                var center = state.CellCenter(index);

                position.X = Math.Clamp(position.X, center.X - allowedRange, center.X + allowedRange);
                position.Y = Math.Clamp(position.Y, center.Y - allowedRange, center.Y + allowedRange);
                position.Z = Math.Clamp(position.Z, center.Z - allowedRange, center.Z + allowedRange);

                state.Next(index) = position;
            }

            state.FlipRelaxation();
        }

        for (var index = 0; index < vertices.Count; index++)
        {
            var vertex = vertices[index];
            vertex.Position = state.Current(index);
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
            content.GetUnsafe(sx + 1, sy, sz) - content.GetUnsafe(sx - 1, sy, sz),
            content.GetUnsafe(sx, sy + 1, sz) - content.GetUnsafe(sx, sy - 1, sz),
            content.GetUnsafe(sx, sy, sz + 1) - content.GetUnsafe(sx, sy, sz - 1)
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
    ///     Records a quad as two triangles split along the diagonal from i0, or, when the smoothing has left the quad concave, as four triangles fanned around the crossing point of the quad's own lattice edge.
    ///     The order flips with the base corner sign so the normals face outward.
    ///     Both triangles start at i0, which makes i0 the provoking vertex for both of them.
    ///     A concave quad covers ground outside itself whichever diagonal it is split along, so the fan does not move the pivot to another cell's vertex.
    ///     Its apex is a copy of i0, carries i0's material, and lies on the lattice edge whose crossing generated the quad, which is inside the quad as long as a vertex stays inside its own cell.
    ///     The fan tiles any simple quad and leaves the quad's four boundary edges untouched.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void EmitQuad(
        List<VoxelVertex> vertices,
        in Span3D<byte> content,
        ReadOnlySpan<int> cornerOffsets,
        in Vector3i cell,
        int edge,
        byte iso,
        int i0,
        int i1,
        int i2,
        int i3,
        bool solidBase,
        List<int> indices,
        List<Vector3i>? vertexCells,
        ref MultiLabelSurfaceNetsStateStorage.State state
    )
    {
        var p0 = vertices[i0].Position;
        var p1 = vertices[i1].Position;
        var p2 = vertices[i2].Position;
        var p3 = vertices[i3].Position;
        var convex = Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), Vector3.Cross(p2 - p0, p3 - p0)) >= 0.0f;

        if (convex)
        {
            EmitDiagonalQuad(indices, solidBase, i0, i1, i2, i3);
            return;
        }

        var strideX = content.SizeX;
        var offset = cell.X + cell.Y * strideX + cell.Z * strideX * content.SizeY;
        var value0 = content.GetUnsafeFlat(offset);
        var value1 = content.GetUnsafeFlat(offset + cornerOffsets[BaseEdgeCorner[edge]]);
        var t = (iso - value0) / (float)(value1 - value0);
        var apexPosition = new Vector3(cell.X, cell.Y, cell.Z) + edge switch
        {
            0 => new Vector3(t, 0.0f, 0.0f),
            1 => new Vector3(0.0f, t, 0.0f),
            _ => new Vector3(0.0f, 0.0f, t)
        };

        if (!ApexIsInside(apexPosition, p0, p1, p2, p3, edge))
        {
            var interior = InteriorApex(p0, p1, p2, p3, edge);

            if (!ApexIsInside(interior, p0, p1, p2, p3, edge))
            {
                EmitDiagonalQuad(indices, solidBase, i0, i1, i2, i3);
                return;
            }

            apexPosition = interior;
        }

        var apex = vertices.Count;
        var vertex = vertices[i0];
        vertex.Position = apexPosition;
        vertices.Add(vertex);

        // The apex belongs to no cell of its own, so it takes the emitting cell's place in the cell-keyed lists the caller and Compact read:
        state.AddLocalCell(state.LocalCells(i0));
        vertexCells?.Add(state.LocalCells(i0));

        if (solidBase)
        {
            AddFanTriangle(indices, apex, i0, i1);
            AddFanTriangle(indices, apex, i1, i2);
            AddFanTriangle(indices, apex, i2, i3);
            AddFanTriangle(indices, apex, i3, i0);
        }
        else
        {
            AddFanTriangle(indices, apex, i1, i0);
            AddFanTriangle(indices, apex, i2, i1);
            AddFanTriangle(indices, apex, i3, i2);
            AddFanTriangle(indices, apex, i0, i3);
        }
    }

    private static void EmitDiagonalQuad(List<int> indices, bool solidBase, int i0, int i1, int i2, int i3)
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

    private static bool ApexIsInside(Vector3 apex, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, int edge)
    {
        var cross0 = Cross(Perpendicular(p0 - apex, edge), Perpendicular(p1 - apex, edge));
        var cross1 = Cross(Perpendicular(p1 - apex, edge), Perpendicular(p2 - apex, edge));
        var cross2 = Cross(Perpendicular(p2 - apex, edge), Perpendicular(p3 - apex, edge));
        var cross3 = Cross(Perpendicular(p3 - apex, edge), Perpendicular(p0 - apex, edge));

        return cross0 > 0.0f && cross1 > 0.0f && cross2 > 0.0f && cross3 > 0.0f || cross0 < 0.0f && cross1 < 0.0f && cross2 < 0.0f && cross3 < 0.0f;
    }

    private static Vector3 InteriorApex(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, int edge)
    {
        Span<Vector3> corners = [p0, p1, p2, p3];
        Span<float> turns = stackalloc float[4];
        var orientation = 0.0f;

        for (var corner = 0; corner < 4; corner++)
        {
            turns[corner] = Cross(
                Perpendicular(corners[(corner + 3) % 4] - corners[corner], edge),
                Perpendicular(corners[(corner + 1) % 4] - corners[corner], edge)
            );

            orientation += turns[corner];
        }

        for (var corner = 0; corner < 4; corner++)
        {
            if (turns[corner] * orientation < 0.0f)
            {
                return (corners[corner] + corners[(corner + 2) % 4]) * 0.5f;
            }
        }

        return (corners[0] + corners[2]) * 0.5f;
    }

    private static void AddFanTriangle(List<int> indices, int apex, int a, int b)
    {
        indices.Add(apex);
        indices.Add(a);
        indices.Add(b);
    }

    private static Vector2 Perpendicular(Vector3 value, int edge)
    {
        return edge switch
        {
            0 => new Vector2(value.Y, value.Z),
            1 => new Vector2(value.Z, value.X),
            _ => new Vector2(value.X, value.Y)
        };
    }
    
    private static float Cross(Vector2 a, Vector2 b)
    {
        return a.X * b.Y - a.Y * b.X;
    }
}
