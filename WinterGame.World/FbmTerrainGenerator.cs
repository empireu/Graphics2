using Graphics2.Data;
using Graphics2.Mathematics;
using Graphics2.Mathematics.Noise;

namespace WinterGame.World;

public sealed class FbmTerrainGenerator
{
    public const byte Stone = 1;

    private readonly Fbm3D<ValueNoise3D> _noise;
    private readonly float _inverseVoxelsPerNoiseUnit;
    private readonly float _surfaceHeight;
    private readonly float _reliefVoxels;

    public FbmTerrainGenerator(int seed, float voxelsPerNoiseUnit, float surfaceHeight, float reliefVoxels, int octaves = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(voxelsPerNoiseUnit);

        _noise = new Fbm3D<ValueNoise3D>(new ValueNoise3D(seed), octaves);
        _inverseVoxelsPerNoiseUnit = 1.0f / voxelsPerNoiseUnit;
        _surfaceHeight = surfaceHeight;
        _reliefVoxels = reliefVoxels;
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
            var noiseZ = (min.Z + z) * _inverseVoxelsPerNoiseUnit;

            for (var y = 0; y < countY; y++)
            {
                var noiseY = (min.Y + y) * _inverseVoxelsPerNoiseUnit;

                for (var x = 0; x < countX; x++)
                {
                    var noiseX = (min.X + x) * _inverseVoxelsPerNoiseUnit;
                    var surface = _surfaceHeight + _noise.Sample(noiseX, noiseY, noiseZ) * _reliefVoxels;
                    var distance = min.Y + y - surface;

                    content[x, y, z] = (byte)Math.Clamp(127.0f - (distance * 127.0f), 0.0f, 255.0f);
                    material[x, y, z] = Stone;
                }
            }
        }
    }
}
