using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.World.Generation.GraphProgram;

namespace WinterGame.World.Generation;

/// <summary>
///     The <see cref="TerrainGraph" /> as a voxel source.
///     The content channel is the graph's signed distance and the material channel is its material root, both filled by one range read of the shared graph.
///     Owns its sampler and scratch, so one source serves one thread.
/// </summary>
public sealed class TerrainGraphSource
{
    private readonly TerrainGraph _graph;
    private readonly DensitySampler _sampler;
    private readonly DensityScratch _scratch;

    public TerrainGraphSource(int seed, TerrainConfig config) : this(TerrainGraph.Build(seed, config))
    {
    }

    /// <summary>
    ///     Creates a source over an already compiled graph.
    /// </summary>
    public TerrainGraphSource(TerrainGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        _graph = graph;
        _scratch = new DensityScratch(_graph.Graph);
        _sampler = new DensitySampler(_graph.Graph, _scratch);
    }

    public TerrainGraph Graph => _graph;

    /// <summary>
    ///     The surface height in meters at one column, for placement.
    /// </summary>
    public float Elevation(int x, int z) => _sampler.Sample(new Vector3i(x, 0, z), 0, _graph.ElevationRoot);

    public byte SampleContent(Vector3i position)
    {
        return DensitySampler.ContentFromDensity(_sampler.Sample(position, 0, _graph.DensityRoot), 0);
    }

    public void Generate(Vector3i min, Vector3i maxInclusive, Span3D<byte> content, Span3D<byte> material)
    {
        var countX = maxInclusive.X - min.X + 1;
        var countY = maxInclusive.Y - min.Y + 1;
        var countZ = maxInclusive.Z - min.Z + 1;

        if (countX < 1 || countY < 1 || countZ < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInclusive), "The box is empty.");
        }

        if (content.SizeX != countX || content.SizeY != countY || content.SizeZ != countZ)
        {
            throw new ArgumentException("The content box has to be maxInclusive - min + 1 on every axis.", nameof(content));
        }

        if (material.SizeX != countX || material.SizeY != countY || material.SizeZ != countZ)
        {
            throw new ArgumentException("The material box has to be maxInclusive - min + 1 on every axis.", nameof(material));
        }

        _sampler.SampleRange(0, min, maxInclusive);

        var density = _scratch.RangeOutput(_graph.DensityRoot);
        var materialIds = _scratch.RangeOutput(_graph.MaterialRoot);

        for (var z = 0; z < countZ; z++)
        {
            for (var y = 0; y < countY; y++)
            {
                for (var x = 0; x < countX; x++)
                {
                    var index = x + y * countX + z * countX * countY;
                    var contentByte = DensitySampler.ContentFromDensity(density[index], 0);

                    content[x, y, z] = contentByte;

                    // Air carries no material, and the graph's root is already one of the table's ids, so the clamp only guards a bad table:
                    material[x, y, z] = contentByte > WorldConstants.ContentIsoLevel
                        ? (byte)Math.Clamp(materialIds[index], 0.0f, 254.0f)
                        : SurfaceMaterial.Null;
                }
            }
        }
    }
}
