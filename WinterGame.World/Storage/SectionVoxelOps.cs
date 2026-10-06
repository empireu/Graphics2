using Graphics2.Data;
using Graphics2.Mathematics;

namespace WinterGame.World.Storage;

/// <summary>
///     Copies boxes of voxels between a section's bricks and flat buffers, both channels at once.
/// </summary>
internal static class SectionVoxelOps
{
    /// <summary>
    ///     Copies a box of one section, given in its local voxel coordinates, into destinations that hold the section's local origin at <paramref name="destinationOffset"/>.
    /// </summary>
    public static void ReadSection(Section section, Vector3i localMin, Vector3i localMax, Vector3i destinationOffset, Span3D<byte> content, Span3D<byte> material)
    {
        var brickMin = localMin >> Section.BrickLog;
        var brickMax = localMax >> Section.BrickLog;

        for (var brickZ = brickMin.Z; brickZ <= brickMax.Z; brickZ++)
        {
            for (var brickY = brickMin.Y; brickY <= brickMax.Y; brickY++)
            {
                for (var brickX = brickMin.X; brickX <= brickMax.X; brickX++)
                {
                    var brickOrigin = new Vector3i(brickX, brickY, brickZ) << Section.BrickLog;
                    var clipMin = Vector3i.Max(localMin, brickOrigin);
                    var clipMax = Vector3i.Min(localMax, brickOrigin + (Section.BrickSize - 1));
                    var brickIndex = Section.GetBrickIndex(brickX, brickY, brickZ);

                    ReadBrick(section, VoxelChannel.Content, brickIndex, brickOrigin, clipMin, clipMax, destinationOffset, content);
                    ReadBrick(section, VoxelChannel.Material, brickIndex, brickOrigin, clipMin, clipMax, destinationOffset, material);
                }
            }
        }
    }

    /// <summary>
    ///     Copies the clipped part of one brick one row at a time.
    ///     A uniform brick fills each row with its value and a dense one copies each row out of its array.
    /// </summary>
    private static void ReadBrick(
        Section section,
        VoxelChannel channel,
        int brickIndex,
        Vector3i brickOrigin,
        Vector3i clipMin,
        Vector3i clipMax,
        Vector3i destinationOffset,
        Span3D<byte> destination)
    {
        var rowLength = clipMax.X - clipMin.X + 1;
        var data = destination.Data;

        if (section.GetBrickType(channel, brickIndex) == BrickType.Uniform)
        {
            var value = section.GetUniformValue(channel, brickIndex);

            for (var z = clipMin.Z; z <= clipMax.Z; z++)
            {
                for (var y = clipMin.Y; y <= clipMax.Y; y++)
                {
                    var row = FlatIndex(destination, clipMin.X + destinationOffset.X, y + destinationOffset.Y, z + destinationOffset.Z);

                    data.Slice(row, rowLength).Fill(value);
                }
            }

            return;
        }

        var voxels = section.GetDense(channel, brickIndex).Span;

        for (var z = clipMin.Z; z <= clipMax.Z; z++)
        {
            for (var y = clipMin.Y; y <= clipMax.Y; y++)
            {
                var source = Section.GetBrickVoxelIndex(clipMin.X - brickOrigin.X, y - brickOrigin.Y, z - brickOrigin.Z);
                var row = FlatIndex(destination, clipMin.X + destinationOffset.X, y + destinationOffset.Y, z + destinationOffset.Z);

                voxels.Slice(source, rowLength).CopyTo(data.Slice(row, rowLength));
            }
        }
    }

    public static byte ReadVoxel(Section section, VoxelChannel channel, int brickIndex, int voxelIndex)
    {
        return section.GetBrickType(channel, brickIndex) == BrickType.Uniform
            ? section.GetUniformValue(channel, brickIndex)
            : section.GetDense(channel, brickIndex).Span[voxelIndex];
    }

    /// <summary>
    ///     Writes a box of one section, given in its local voxel coordinates, from sources that hold the section's local origin at <paramref name="sourceOffset"/>.
    ///     Grows the changed box by the written part of every brick whose values changed, in world samples, and returns whether any did.
    /// </summary>
    public static bool WriteSection(
        Section section,
        Vector3i localMin,
        Vector3i localMax,
        Vector3i sourceOffset,
        Span3D<byte> content,
        Span3D<byte> material,
        ref Vector3i changedMin,
        ref Vector3i changedMax)
    {
        var origin = section.Position.GetOrigin();
        var brickMin = localMin >> Section.BrickLog;
        var brickMax = localMax >> Section.BrickLog;
        var changed = false;

        for (var brickZ = brickMin.Z; brickZ <= brickMax.Z; brickZ++)
        {
            for (var brickY = brickMin.Y; brickY <= brickMax.Y; brickY++)
            {
                for (var brickX = brickMin.X; brickX <= brickMax.X; brickX++)
                {
                    var brickOrigin = new Vector3i(brickX, brickY, brickZ) << Section.BrickLog;
                    var clipMin = Vector3i.Max(localMin, brickOrigin);
                    var clipMax = Vector3i.Min(localMax, brickOrigin + (Section.BrickSize - 1));
                    var brickIndex = Section.GetBrickIndex(brickX, brickY, brickZ);

                    // Both channels are written whatever the first one did:
                    var contentChanged = WriteBrick(section, VoxelChannel.Content, brickIndex, brickOrigin, clipMin, clipMax, sourceOffset, content);
                    var materialChanged = WriteBrick(section, VoxelChannel.Material, brickIndex, brickOrigin, clipMin, clipMax, sourceOffset, material);

                    if (!contentChanged && !materialChanged)
                    {
                        continue;
                    }

                    changed = true;
                    changedMin = Vector3i.Min(changedMin, origin + clipMin);
                    changedMax = Vector3i.Max(changedMax, origin + clipMax);
                }
            }
        }

        return changed;
    }

    /// <summary>
    ///     Writes the clipped part of one brick and returns whether any value changed.
    ///     A uniform brick written with its own value is left alone, a brick the box covers with one value becomes uniform, and a dense brick left holding one value becomes uniform, which frees its array.
    /// </summary>
    private static bool WriteBrick(
        Section section,
        VoxelChannel channel,
        int brickIndex,
        Vector3i brickOrigin,
        Vector3i clipMin,
        Vector3i clipMax,
        Vector3i sourceOffset,
        Span3D<byte> source)
    {
        var rowLength = clipMax.X - clipMin.X + 1;
        var data = source.Data;
        var first = data[FlatIndex(source, clipMin.X + sourceOffset.X, clipMin.Y + sourceOffset.Y, clipMin.Z + sourceOffset.Z)];
        var oneValue = IsOneValue(source, clipMin + sourceOffset, clipMax + sourceOffset, first);
        var wholeBrick = clipMin == brickOrigin && clipMax == brickOrigin + (Section.BrickSize - 1);

        if (section.GetBrickType(channel, brickIndex) == BrickType.Uniform)
        {
            var value = section.GetUniformValue(channel, brickIndex);

            if (oneValue && first == value)
            {
                return false;
            }

            if (oneValue && wholeBrick)
            {
                section.SetUniform(channel, brickIndex, first);
                return true;
            }

            section.SetDense(channel, brickIndex);
            section.GetDense(channel, brickIndex).Span.Fill(value);
        }
        else if (oneValue && wholeBrick)
        {
            var replaced = section.GetDense(channel, brickIndex).Span.ContainsAnyExcept(first);

            section.SetUniform(channel, brickIndex, first);
            return replaced;
        }

        var voxels = section.GetDense(channel, brickIndex).Span;
        var changed = false;

        for (var z = clipMin.Z; z <= clipMax.Z; z++)
        {
            for (var y = clipMin.Y; y <= clipMax.Y; y++)
            {
                var target = voxels.Slice(Section.GetBrickVoxelIndex(clipMin.X - brickOrigin.X, y - brickOrigin.Y, z - brickOrigin.Z), rowLength);
                var row = data.Slice(FlatIndex(source, clipMin.X + sourceOffset.X, y + sourceOffset.Y, z + sourceOffset.Z), rowLength);

                if (row.SequenceEqual(target))
                {
                    continue;
                }

                row.CopyTo(target);
                changed = true;
            }
        }

        var firstVoxel = voxels[0];

        if (!voxels.ContainsAnyExcept(firstVoxel))
        {
            section.SetUniform(channel, brickIndex, firstVoxel);
        }

        return changed;
    }

    /// <summary>
    ///     Whether every sample of the inclusive box of <paramref name="source"/> is <paramref name="value"/>.
    /// </summary>
    private static bool IsOneValue(Span3D<byte> source, Vector3i min, Vector3i maxInclusive, byte value)
    {
        var rowLength = maxInclusive.X - min.X + 1;
        var data = source.Data;

        for (var z = min.Z; z <= maxInclusive.Z; z++)
        {
            for (var y = min.Y; y <= maxInclusive.Y; y++)
            {
                if (data.Slice(FlatIndex(source, min.X, y, z), rowLength).ContainsAnyExcept(value))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static void FillBox(Span3D<byte> destination, Vector3i min, Vector3i maxInclusive, byte value)
    {
        var rowLength = maxInclusive.X - min.X + 1;
        var data = destination.Data;

        for (var z = min.Z; z <= maxInclusive.Z; z++)
        {
            for (var y = min.Y; y <= maxInclusive.Y; y++)
            {
                data.Slice(FlatIndex(destination, min.X, y, z), rowLength).Fill(value);
            }
        }
    }

    // TODO unify all these
    private static int FlatIndex(Span3D<byte> span, int x, int y, int z)
    {
        return x + (y + z * span.SizeY) * span.SizeX;
    }
}
