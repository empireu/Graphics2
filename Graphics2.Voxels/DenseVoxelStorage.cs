using Graphics2.Data;
using Graphics2.Mathematics;

namespace Graphics2.Voxels;

/// <summary>
///     A dense uniform grid, one byte per voxel per channel.
/// </summary>
public sealed class DenseVoxelStorage : IVoxelStorage
{
    private const int MaxLod = 30;

    private readonly Memory3D<byte> _content;
    private readonly Memory3D<byte> _material;

    public DenseVoxelStorage(Memory3D<byte> content, Memory3D<byte> material)
    {
        if (content.SizeX != material.SizeX || content.SizeY != material.SizeY || content.SizeZ != material.SizeZ)
        {
            throw new ArgumentException("The material box must be the same size as the content box.", nameof(material));
        }

        _content = content;
        _material = material;
    }

    public Vector3i SizeInVoxels => new(_content.SizeX, _content.SizeY, _content.SizeZ);

    public void ReadRange(int lod, Vector3i min, Vector3i maxInclusive, Span3D<byte> content, Span3D<byte> material)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lod);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lod, MaxLod, nameof(lod));

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

        if (min.X < 0 || min.Y < 0 || min.Z < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(min), "The box starts outside the storage.");
        }

        var endsPastX = (long)(maxInclusive.X + 1) << lod > _content.SizeX;
        var endsPastY = (long)(maxInclusive.Y + 1) << lod > _content.SizeY;
        var endsPastZ = (long)(maxInclusive.Z + 1) << lod > _content.SizeZ;

        if (endsPastX || endsPastY || endsPastZ)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInclusive), "The box reaches past the storage.");
        }

        var sourceContent = _content.Span.Data;
        var sourceMaterial = _material.Span.Data;
        var sourceSizeX = _content.SizeX;
        var sourcePlane = sourceSizeX * _content.SizeY;

        for (var z = 0; z < countZ; z++)
        {
            var sourceZ = (min.Z + z) << lod;

            for (var y = 0; y < countY; y++)
            {
                var sourceRow = sourceZ * sourcePlane + ((min.Y + y) << lod) * sourceSizeX;

                for (var x = 0; x < countX; x++)
                {
                    var sourceIndex = sourceRow + ((min.X + x) << lod);

                    content[x, y, z] = sourceContent[sourceIndex];
                    material[x, y, z] = sourceMaterial[sourceIndex];
                }
            }
        }
    }
}
