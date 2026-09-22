using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.World.Generation;

namespace WinterGame.World;

/// <summary>
///     The <see cref="TerrainGraph"/> as a voxel source.
///     The content channel is the graph's signed distance, and the material channel is the placeholder classifier on that field's own slope.
///     Owns a density scratch one sample wider than the requested box on x and z, for the slope's finite differences.
/// </summary>
public sealed class TerrainGraphSource : IVoxelSource
{
    private readonly DensitySampler _elevation;
    private readonly DensitySampler _density;
    private readonly SurfaceMaterialClassifier _classifier;
    private Memory3D<float> _scratch;

    public TerrainGraphSource(int seed, TerrainConfig config, SurfaceMaterialClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);

        var graph = TerrainGraph.Build(seed, config);

        _elevation = new DensitySampler(graph.Elevation);
        _density = new DensitySampler(graph.Density);
        _classifier = classifier;
    }

    /// <summary>
    ///     The surface height in meters at one column, for placement.
    /// </summary>
    public float Elevation(int x, int z) => _elevation.Sample(new Vector3i(x, 0, z), 0);

    public byte SampleContent(Vector3i position)
    {
        return DensitySampler.ContentFromDensity(_density.Sample(position, 0), 0);
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

        var scratchCountX = countX + 2;
        var scratchCountZ = countZ + 2;

        if (_scratch.SizeX != scratchCountX || _scratch.SizeY != countY || _scratch.SizeZ != scratchCountZ)
        {
            _scratch = Memory3D<float>.Allocate(scratchCountX, countY, scratchCountZ);
        }

        var scratchMin = new Vector3i(min.X - 1, min.Y, min.Z - 1);
        var scratchMax = new Vector3i(maxInclusive.X + 1, maxInclusive.Y, maxInclusive.Z + 1);

        _density.SampleRange(0, scratchMin, scratchMax, _scratch.Span);
        var density = _scratch.Span;

        var spacing = DensitySampler.SampleSpacing(0);
        var step = 2.0f * spacing;

        for (var z = 0; z < countZ; z++)
        {
            for (var x = 0; x < countX; x++)
            {
                for (var y = 0; y < countY; y++)
                {
                    var value = density[x + 1, y, z + 1];
                    var gradientX = (density[x + 2, y, z + 1] - density[x, y, z + 1]) / step;
                    var gradientZ = (density[x + 1, y, z + 2] - density[x + 1, y, z]) / step;
                    var slope = 1.0f / MathF.Sqrt(gradientX * gradientX + 1.0f + gradientZ * gradientZ);

                    content[x, y, z] = DensitySampler.ContentFromDensity(value, 0);
                    material[x, y, z] = content[x, y, z] > WorldConstants.ContentIsoLevel
                        ? _classifier.Classify(slope, (min.Y + y) * spacing)
                        : SurfaceMaterial.Null;
                }
            }
        }
    }
}
