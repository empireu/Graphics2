using Graphics2.Data;
using Graphics2.Mathematics;

namespace WinterGame.World;

/// <summary>
///     Reader for a voxel world. Reads in a box-shaped region, containing content and material.
///     A sample at lod L covers 2^L voxels, so the extent readable at lod L is <see cref="SizeInVoxels"/> <c>>> L</c>.
///     A sample index maps to a world voxel as <c>sampleIndex &lt;&lt; lod</c>, measured from one origin for the whole world, so a coarse sample sits exactly on a fine sample position.
///     What value that sample carries is up to the implementation: the dense grid point-samples the fine voxel there, while an octree stores a filtered representative of the voxels the sample spans.
///     Either way the value must be a pure function of lod and position, so that two readers of the same sample agree on the bytes.
///     Content is a normalized signed distance where larger values are closer to solid. Material is an index into the material table, with 255 meaning no material.
/// </summary>
public interface IVoxelStorage
{
    /// <summary>
    ///     World extent in voxels.
    /// </summary>
    Vector3i SizeInVoxels { get; }

    /// <summary>
    ///     Fills both channels for the inclusive box [min, maxInclusive] at the given lod.
    ///     Both destinations are sized maxInclusive - min + 1 on every axis and are validated against it.
    ///     Does not allocate, and is safe for concurrent readers.
    /// </summary>
    void ReadRange(int lod, Vector3i min, Vector3i maxInclusive, Span3D<byte> content, Span3D<byte> material);
}
