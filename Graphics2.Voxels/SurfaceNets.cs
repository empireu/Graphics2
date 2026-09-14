using System.Numerics;
using System.Runtime.CompilerServices;
using Graphics2.Data;
using Graphics2.Mathematics;

/*
    Surface nets: a dual grid mesher for signed distance fields.

    The input is a dense box of content bytes, where content above the iso level counts as solid.
    Every cell whose 8 corners straddle the iso level produces exactly one vertex, placed at the average of the crossings on its 12 edges.
    Every cube edge that crosses the iso level produces exactly one quad, joining the four cells that share that edge.
    A quad is emitted by the cell at the edge's low corner, so every edge in the grid is emitted exactly once and no seam needs stitching.
    Normals are the central finite difference of the content field at the 8 cell corners, with trilinear interpolation to the vertex.

    The meshed range is [CellMin, CellMin + CellCount) in cells, and the traversal also visits one cell below it.
    That extra cell is the halo: the quads of the first meshed cell need its vertex, so it gets one, but it emits no quads of its own.
    The halo cell's gradient stencil reaches one sample further down, which is why the low margin is two samples and the high margin is one.

    Corner indices pack their lattice offset into three bits, x in bit 0, y in bit 1, z in bit 2.
    The 8 corner signs form the index into the edge table, which returns a 12-bit mask of the crossing edges.
    The CubeEdges lists the three edges from corner 0 first, so a cell's three base edges are bits 0, 1 and 2 of that mask.
*/

namespace Graphics2.Voxels;

/// <summary>
///     One vertex per cell that crosses the iso level, in sample coordinates with an outward unit normal.
/// </summary>
public struct SurfaceNetsVertex
{
    public Vector3 Position;
    public Vector3 Normal;
}

public sealed class SurfaceNetsMeshingDescription
{
    /// <summary>
    ///     Content field.
    /// </summary>
    public required Memory3D<byte> Content { get; init; }
    
    /// <summary>
    ///     Content above this level counts as solid.
    /// </summary>
    public required byte IsoLevel { get; init; }
    
    /// <summary>
    ///     The vertices generated. One per crossing cell.
    /// </summary>
    public required List<SurfaceNetsVertex> Vertices { get; init; }
    
    /// <summary>
    ///     Indices into <see cref="Vertices"/>. Three per triangle, six per quad.
    /// </summary>
    public required List<int> Indices { get; init; }
    
    /// <summary>
    ///     First cell to mesh on each axis. Null meshes as much of the grid as the halo and stencil allow.
    ///     One cell of halo sits below it, holding the quad neighbors of the first meshed cell.
    /// </summary>
    public Vector3i? CellMin { get; init; }

    /// <summary>
    ///     Number of cells to mesh from <see cref="CellMin"/> on each axis. Null meshes up to the same limit.
    ///     The gradient stencil needs one cell of margin above.
    /// </summary>
    public Vector3i? CellCount { get; init; }
}

/// <summary>
///     Dual grid mesher. Emits one vertex per crossing cell and one quad per crossing cube edge.
/// </summary>
public class SurfaceNetsMesher
{
    /// <summary>
    ///     The halo is the ring of cells around the meshed range that the mesher reads but emits no quads for.
    /// </summary>
    private const int Halo = 1;

    /// <summary>
    ///     Samples read below the first meshed cell: the halo cell, plus the gradient stencil of its own corners.
    /// </summary>
    private const int LowSampleMargin = Halo + 1;

    /// <summary>
    ///     Samples read above the top corner of the last meshed cell: its gradient stencil.
    /// </summary>
    private const int HighSampleMargin = 1;

    /// <summary>
    ///     Corner offsets are packed as bits, x in bit 0, y in bit 1, z in bit 2.
    /// </summary>
    private static readonly int[] CubeEdges = new int[24];
    private static readonly int[] EdgeTable = new int[256];

    static SurfaceNetsMesher()
    {
        // The 12 cube edges as corner index pairs, with the three edges from corner 0 first.
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

        // Maps an 8-bit corner sign mask to a 12-bit mask of the edges crossing the iso level:
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

    /// <summary>
    ///     The inclusive box of samples this mesher reads to mesh <see cref="cellCount"/> cells from <see cref="cellMin"/>.
    ///     Read exactly this box and the default range meshes exactly those cells.
    /// </summary>
    public static BoundingBox3i GetSampleRange(Vector3i cellMin, Vector3i cellCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cellCount.X, 1, nameof(cellCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(cellCount.Y, 1, nameof(cellCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(cellCount.Z, 1, nameof(cellCount));

        return new BoundingBox3i(cellMin - LowSampleMargin, cellMin + cellCount + HighSampleMargin);
    }

    /// <summary>
    ///     The range of uniformly spaced blocks of <see cref="chunkCellCount"/> cells whose meshes can change when any sample in <see cref="sampleBox"/> changes.
    ///     A block is affected when its read window overlaps the box rather than when the box touches its cells, because the block's mesh depends on the halo cell below its first cell and on the surrounding stencils.
    ///     The result can reach outside the grid.
    /// </summary>
    public static BoundingBox3i GetAffectedChunkRange(BoundingBox3i sampleBox, Vector3i chunkCellCount)
    {
        if (!sampleBox.IsValid)
        {
            throw new ArgumentException("The box is empty.", nameof(sampleBox));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(chunkCellCount.X, 1, nameof(chunkCellCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkCellCount.Y, 1, nameof(chunkCellCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkCellCount.Z, 1, nameof(chunkCellCount));

        // A block at index k reads [k * cells - Low, k * cells + cells + High], which overlaps [min, max] when k * cells <= max + Low and k * cells + cells + High >= min:
        var min = new Vector3i(
            MathX.CeilDiv(sampleBox.Min.X - chunkCellCount.X - HighSampleMargin, chunkCellCount.X), 
            MathX.CeilDiv(sampleBox.Min.Y - chunkCellCount.Y - HighSampleMargin, chunkCellCount.Y),
            MathX.CeilDiv(sampleBox.Min.Z - chunkCellCount.Z - HighSampleMargin, chunkCellCount.Z)
        );

        var max = new Vector3i(
            MathX.FloorDiv(sampleBox.Max.X + LowSampleMargin, chunkCellCount.X),
            MathX.FloorDiv(sampleBox.Max.Y + LowSampleMargin, chunkCellCount.Y),
            MathX.FloorDiv(sampleBox.Max.Z + LowSampleMargin, chunkCellCount.Z)
        );

        return new BoundingBox3i(min, max);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public static void Mesh(SurfaceNetsMeshingDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(description.Vertices);
        ArgumentNullException.ThrowIfNull(description.Indices);

        if (description.Content.IsEmpty)
        {
            throw new ArgumentException("The content box is empty.", nameof(description.Content));
        }

        var cells = new Vector3i(description.Content.SizeX - 1, description.Content.SizeY - 1, description.Content.SizeZ - 1);

        // A null range means the largest one the grid allows:
        var cellMin = description.CellMin ?? new Vector3i(Halo + 1);
        var cellCount = description.CellCount ?? (cells - Halo - cellMin);

        // The quad neighbors of the first meshed cell sit one cell lower, and their gradients reach one sample below that:
        ArgumentOutOfRangeException.ThrowIfLessThan(cellMin.X, Halo + 1, nameof(description.CellMin));
        ArgumentOutOfRangeException.ThrowIfLessThan(cellMin.Y, Halo + 1, nameof(description.CellMin));
        ArgumentOutOfRangeException.ThrowIfLessThan(cellMin.Z, Halo + 1, nameof(description.CellMin));

        ArgumentOutOfRangeException.ThrowIfLessThan(cellCount.X, 1, nameof(description.CellCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(cellCount.Y, 1, nameof(description.CellCount));
        ArgumentOutOfRangeException.ThrowIfLessThan(cellCount.Z, 1, nameof(description.CellCount));

        // The gradient stencil reads one sample past the last corner, so the range stops one cell short of the end:
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cellCount.X, cells.X - Halo - cellMin.X, nameof(description.CellCount));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cellCount.Y, cells.Y - Halo - cellMin.Y, nameof(description.CellCount));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cellCount.Z, cells.Z - Halo - cellMin.Z, nameof(description.CellCount));

        var content = description.Content.Span;
        var iso = description.IsoLevel;
        var vertices = description.Vertices;
        var indices = description.Indices;

        // The traversal runs one halo cell wider than the meshed range, so the halo cell gets a vertex as well:
        var origin = cellMin - Halo;
        var range = cellCount + Halo;

        // Maps a local cell to the index of its vertex, or -1 when the cell does not cross the iso level:
        var cellIndices = new int[range.X * range.Y * range.Z];
        Array.Fill(cellIndices, -1);

        // Holds the 8 corner samples of the cell being meshed, reused for every cell:
        Span<float> cornerValues = stackalloc float[8];

        // Local indices below Halo are halo cells, so the meshed range starts at Halo on every axis:
        for (var lz = 0; lz < range.Z; lz++)
        {
            for (var ly = 0; ly < range.Y; ly++)
            {
                for (var lx = 0; lx < range.X; lx++)
                {
                    // Sample coordinate of this cell's base corner:
                    var sx = origin.X + lx;
                    var sy = origin.Y + ly;
                    var sz = origin.Z + lz;

                    // Each corner is read at its own bit offset and its sign becomes one bit of the mask:
                    var mask = 0;

                    for (var corner = 0; corner < 8; corner++)
                    {
                        var value = content[sx + (corner & 1), sy + ((corner >> 1) & 1), sz + ((corner >> 2) & 1)];
                        cornerValues[corner] = value;

                        if (value > iso)
                        {
                            mask |= 1 << corner;
                        }
                    }

                    // A cell entirely on one side of the iso level holds no surface:
                    if (mask is 0 or 255)
                    {
                        continue;
                    }

                    var edgeMask = EdgeTable[mask];
                    var sumX = 0.0f;
                    var sumY = 0.0f;
                    var sumZ = 0.0f;
                    var crossings = 0;
                    var remaining = edgeMask;

                    // Walk only the edges that cross, lowest set bit first:
                    while (remaining != 0)
                    {
                        var edge = BitOperations.TrailingZeroCount(remaining);
                        remaining &= remaining - 1;

                        var cornerA = CubeEdges[edge * 2];
                        var cornerB = CubeEdges[edge * 2 + 1];
                        var valueA = cornerValues[cornerA];
                        var valueB = cornerValues[cornerB];

                        // Fraction along the edge where the content reaches the iso level:
                        var t = (iso - valueA) / (valueB - valueA);

                        // The two corners differ in exactly one axis, so the other two are the base corner's own bits:
                        var axis = BitOperations.TrailingZeroCount(cornerA ^ cornerB);

                        var bitX = (cornerA & 1) != 0;
                        var bitY = (cornerA & 2) != 0;
                        var bitZ = (cornerA & 4) != 0;

                        sumX += axis == 0 
                            ? bitX ? 1.0f - t : t 
                            : bitX ? 1.0f : 0.0f;

                        sumY += axis == 1 
                            ? bitY ? 1.0f - t : t 
                            : bitY ? 1.0f : 0.0f;
                     
                        sumZ += axis == 2
                            ? bitZ ? 1.0f - t : t 
                            : bitZ ? 1.0f : 0.0f;
                        
                        crossings++;
                    }

                    // Average of the crossing points, kept as an offset inside the cell:
                    var local = new Vector3(sumX / crossings, sumY / crossings, sumZ / crossings);
                    var vertexIndex = vertices.Count;

                    vertices.Add(new SurfaceNetsVertex
                    {
                        Position = new Vector3(sx + local.X, sy + local.Y, sz + local.Z),
                        Normal = VertexNormal(sx, sy, sz, local, content)
                    });

                    // Store the vertex before emitting quads, because an edge quad includes this cell:
                    cellIndices[lz * range.X * range.Y + ly * range.X + lx] = vertexIndex;

                    // Indices below Halo are halo cells, which contribute a vertex but no quads:
                    if (lx < Halo || ly < Halo || lz < Halo)
                    {
                        continue;
                    }

                    // Which side of the surface the base corner sits on decides the triangle order:
                    var solidBase = cornerValues[0] > iso;

                    // Quad around the +x edge, joining the four cells that share it:
                    if ((edgeMask & 1) != 0)
                    {
                        EmitQuad(
                            vertexIndex,
                            cellIndices[lz * range.X * range.Y + (ly - 1) * range.X + lx],
                            cellIndices[(lz - 1) * range.X * range.Y + (ly - 1) * range.X + lx],
                            cellIndices[(lz - 1) * range.X * range.Y + ly * range.X + lx],
                            solidBase,
                            indices
                        );
                    }

                    // Quad around the +y edge, joining the four cells that share it:
                    if ((edgeMask & 2) != 0)
                    {
                        EmitQuad(
                            vertexIndex,
                            cellIndices[(lz - 1) * range.X * range.Y + ly * range.X + lx],
                            cellIndices[(lz - 1) * range.X * range.Y + ly * range.X + (lx - 1)],
                            cellIndices[lz * range.X * range.Y + ly * range.X + (lx - 1)],
                            solidBase,
                            indices
                        );
                    }

                    // Quad around the +z edge, joining the four cells that share it:
                    if ((edgeMask & 4) != 0)
                    {
                        EmitQuad(
                            vertexIndex,
                            cellIndices[lz * range.X * range.Y + ly * range.X + (lx - 1)],
                            cellIndices[lz * range.X * range.Y + (ly - 1) * range.X + (lx - 1)],
                            cellIndices[lz * range.X * range.Y + (ly - 1) * range.X + lx],
                            solidBase,
                            indices
                        );
                    }
                }
            }
        }
    }

    /// <summary>
    ///     Central difference of the content field at one sample point.
    ///     The validation bounds the stencil, so these reads always land inside the grid.
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

        // Trilinear interpolation of the 8 corner gradients at the vertex offset inside the cell.
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