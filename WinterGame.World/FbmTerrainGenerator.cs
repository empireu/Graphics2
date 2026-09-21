using Graphics2.Data;
using Graphics2.Mathematics;
using Graphics2.Mathematics.Noise;

namespace WinterGame.World;

public sealed class FbmTerrainGenerator : IVoxelSource
{
    public const byte IsoLevel = 127;

    private readonly Fbm3D<ValueNoise3D> _noise;
    private readonly SurfaceMaterialClassifier _classifier;
    private readonly float _inverseVoxelsPerNoiseUnit;
    private readonly float _surfaceHeight;
    private readonly float _reliefVoxels;

    public FbmTerrainGenerator(int seed, float voxelsPerNoiseUnit, float surfaceHeight, float reliefVoxels, SurfaceMaterialClassifier classifier, int octaves = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(voxelsPerNoiseUnit);
        ArgumentNullException.ThrowIfNull(classifier);

        _noise = new Fbm3D<ValueNoise3D>(new ValueNoise3D(seed), octaves);
        _classifier = classifier;
        _inverseVoxelsPerNoiseUnit = 1.0f / voxelsPerNoiseUnit;
        _surfaceHeight = surfaceHeight;
        _reliefVoxels = reliefVoxels;
    }

    public byte SampleContent(Vector3i position) => ContentAt(position.X, position.Y, position.Z);

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
            throw new ArgumentException("The content box must be maxInclusive - min + 1 on every axis.", nameof(content));
        }

        if (material.SizeX != countX || material.SizeY != countY || material.SizeZ != countZ)
        {
            throw new ArgumentException("The material box must be maxInclusive - min + 1 on every axis.", nameof(material));
        }

        for (var z = 0; z < countZ; z++)
        {
            for (var y = 0; y < countY; y++)
            {
                for (var x = 0; x < countX; x++)
                {
                    content[x, y, z] = ContentAt(min.X + x, min.Y + y, min.Z + z);
                }
            }
        }

        ClassifyMaterial(min, content, material);
    }

    /// <summary>
    ///     The density at one voxel, shared by the interface's point read and the box fill's inner loop, so the two can never disagree about a position.
    /// </summary>
    private byte ContentAt(int x, int y, int z)
    {
        var surface = _surfaceHeight + _noise.Sample(
            x * _inverseVoxelsPerNoiseUnit,
            y * _inverseVoxelsPerNoiseUnit,
            z * _inverseVoxelsPerNoiseUnit
        ) * _reliefVoxels;

        return (byte)Math.Clamp(127.0f - ((y - surface) * 127.0f), 0.0f, 255.0f);
    }

    /// <summary>
    ///     Air carries <see cref="SurfaceMaterial.Null" />.
    ///     A solid sample takes the id the classifier scores highest at that sample's own slope and height, where the slope comes from the content field's gradient, which is also what the mesher's normals are made of, so the material follows the drawn surface.
    /// </summary>
    private void ClassifyMaterial(Vector3i min, Span3D<byte> content, Span3D<byte> material)
    {
        var countX = content.SizeX;
        var countY = content.SizeY;
        var countZ = content.SizeZ;

        for (var z = 0; z < countZ; z++)
        {
            var zLow = Math.Max(z - 1, 0);
            var zHigh = Math.Min(z + 1, countZ - 1);

            for (var y = 0; y < countY; y++)
            {
                var yLow = Math.Max(y - 1, 0);
                var yHigh = Math.Min(y + 1, countY - 1);

                for (var x = 0; x < countX; x++)
                {
                    if (content[x, y, z] <= IsoLevel)
                    {
                        material[x, y, z] = SurfaceMaterial.Null;
                        continue;
                    }

                    var xLow = Math.Max(x - 1, 0);
                    var xHigh = Math.Min(x + 1, countX - 1);

                    var gradientX = content[xHigh, y, z] - content[xLow, y, z];
                    var gradientY = content[x, yHigh, z] - content[x, yLow, z];
                    var gradientZ = content[x, y, zHigh] - content[x, y, zLow];

                    var lengthSquared = (gradientX * gradientX) + (gradientY * gradientY) + (gradientZ * gradientZ);

                    var slope = lengthSquared > 0 ? -gradientY / MathF.Sqrt(lengthSquared) : 1.0f;

                    material[x, y, z] = _classifier.Classify(slope, min.Y + y);
                }
            }
        }
    }
}
