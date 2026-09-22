using Graphics2.Data;
using Graphics2.Mathematics;

namespace WinterGame.World;

// Baseline to test the garph
public sealed class TerrainGenerator : IVoxelSource
{
    public const byte IsoLevel = 127;

    /// <summary>
    ///     The distance one sample stands for. The density conversion divides by it, which is the only place a level enters.
    /// </summary>
    private const float SampleSpacing = WorldConstants.VoxelSize;

    private readonly TerrainConfig _config;
    private readonly SurfaceMaterialClassifier _classifier;
    private readonly FastNoiseLite _continental;
    private readonly FastNoiseLite _hills;
    private readonly FastNoiseLite _mountains;

    public TerrainGenerator(int seed, SurfaceMaterialClassifier classifier, TerrainConfig config)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(config);

        _classifier = classifier;
        _config = config;

        _continental = CreateNoise(
            MathX.MixSeed(seed, 0x1F123BB5),
            FastNoiseLite.NoiseType.OpenSimplex2,
            FastNoiseLite.FractalType.FBm,
            4,
            config.ContinentalWavelength
        );
        
        _hills = CreateNoise(
            MathX.MixSeed(seed, 0x2A9F4B21),
            FastNoiseLite.NoiseType.OpenSimplex2,
            FastNoiseLite.FractalType.FBm,
            3,
            config.HillWavelength
        );
        
        _mountains = CreateNoise(
            MathX.MixSeed(seed, 0x6C8E9CF5),
            FastNoiseLite.NoiseType.OpenSimplex2,
            FastNoiseLite.FractalType.Ridged,
            4,
            config.MountainWavelength
        );
    }

    /// <summary>
    ///     The terrain height at one column, in meters.
    /// </summary>
    public float Elevation(int x, int z)
    {
        var worldX = x * SampleSpacing;
        var worldZ = z * SampleSpacing;

        var continent = _continental.GetNoise(worldX, worldZ);
        var hills = _hills.GetNoise(worldX, worldZ);
        var mountains = Math.Clamp(_mountains.GetNoise(worldX, worldZ), 0.0f, 1.0f);

        var mask = MathX.CheckedMapSmoothStepNonsingular(
            _config.MountainMaskMin, 
            _config.MountainMaskMax,
            continent
        );

        return _config.BaseHeight
            + continent * _config.ContinentalAmplitude
            + hills * _config.HillAmplitude
            + mountains * _config.MountainAmplitude * mask;
    }

    public byte SampleContent(Vector3i position)
    {
        return ContentAt(position.Y, Elevation(position.X, position.Z));
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
            throw new ArgumentException("The content box must be maxInclusive - min + 1 on every axis.", nameof(content));
        }

        if (material.SizeX != countX || material.SizeY != countY || material.SizeZ != countZ)
        {
            throw new ArgumentException("The material box must be maxInclusive - min + 1 on every axis.", nameof(material));
        }

        for (var z = 0; z < countZ; z++)
        {
            for (var x = 0; x < countX; x++)
            {
                var worldX = min.X + x;
                var worldZ = min.Z + z;
                var elevation = Elevation(worldX, worldZ);
                var slope = Slope(worldX, worldZ);

                for (var y = 0; y < countY; y++)
                {
                    var worldY = min.Y + y;
                    var value = ContentAt(worldY, elevation);

                    content[x, y, z] = value;
                    material[x, y, z] = value > IsoLevel ? _classifier.Classify(slope, worldY * SampleSpacing) : SurfaceMaterial.Null;
                }
            }
        }
    }

    /// <summary>
    ///     The y component of the surface normal, from the height field's central difference.
    /// </summary>
    private float Slope(int x, int z)
    {
        var step = 2.0f * SampleSpacing;
        var gradientX = (Elevation(x + 1, z) - Elevation(x - 1, z)) / step;
        var gradientZ = (Elevation(x, z + 1) - Elevation(x, z - 1)) / step;

        return 1.0f / MathF.Sqrt(gradientX * gradientX + 1.0f + gradientZ * gradientZ);
    }

    private byte ContentAt(int y, float elevation)
    {
        var distance = elevation - y * SampleSpacing;

        return (byte)Math.Clamp(IsoLevel + distance / SampleSpacing * _config.ContentScale, 0.0f, 255.0f);
    }

    private static FastNoiseLite CreateNoise(int seed, FastNoiseLite.NoiseType noiseType, FastNoiseLite.FractalType fractalType, int octaves, float wavelength)
    {
        var noise = new FastNoiseLite(seed);

        noise.SetNoiseType(noiseType);
        noise.SetFractalType(fractalType);
        noise.SetFractalOctaves(octaves);
        noise.SetFrequency(1.0f / wavelength);

        return noise;
    }
}
