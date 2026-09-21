using Graphics2.Data;
using Graphics2.Mathematics;

namespace WinterGame.World;

public interface IVoxelSource
{
    /// <summary>
    ///     Point query for the content at one voxel.
    /// </summary>
    byte SampleContent(Vector3i position);

    /// <summary>
    ///     Fills both channels for the inclusive box [min, maxInclusive].
    ///     Both destinations are sized maxInclusive - min + 1 on every axis.
    /// </summary>
    void Generate(Vector3i min, Vector3i maxInclusive, Span3D<byte> content, Span3D<byte> material);
}
