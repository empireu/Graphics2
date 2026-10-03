using System.Diagnostics;
using System.Runtime.Intrinsics;
using Graphics2.Data;
using WinterGame.World.Storage;

namespace WinterGame.World;

/// <summary>
///     Which sides of <see cref="WorldConstants.ContentIsoLevel"/> some samples are on. Content above the level is solid, which is how the mesher counts it.
/// </summary>
[Flags]
public enum ContentSide : byte
{
    None = 0,
    Air = 1 << 0,
    Solid = 1 << 1,
    Mixed = Air | Solid
}

/// <summary>
///     Which sides of the iso level a section's samples are on, and those of its three low faces, two bits each.
///     A cell owns only the three edges leaving its low corner, so the edges of a section's cells end inside it or in the low face of its positive neighbour on that axis, which is why only the low faces are kept.
/// </summary>
public readonly struct SectionContentSides(byte packed) : IEquatable<SectionContentSides>
{
    private const int FieldBits = 2;
    private const int FieldMask = (1 << FieldBits) - 1;
    private const int InteriorShift = 0;
    private const int LowXShift = FieldBits;
    private const int LowYShift = 2 * FieldBits;
    private const int LowZShift = 3 * FieldBits;

    /// <summary>
    ///     The voxels of a brick at its lowest X, in the brick's x fastest order with one bit per voxel.
    /// </summary>
    private const ulong BrickLowXVoxels = 0x1111_1111_1111_1111;

    private const ulong BrickLowYVoxels = 0x000F_000F_000F_000F;
    private const ulong BrickLowZVoxels = 0xFFFF;

    public readonly byte Packed = packed;

    public SectionContentSides(ContentSide interior, ContentSide lowX, ContentSide lowY, ContentSide lowZ)
        : this((byte)(((int)interior << InteriorShift) | ((int)lowX << LowXShift) | ((int)lowY << LowYShift) | ((int)lowZ << LowZShift)))
    {
    }

    /// <summary>
    ///     Every sample of the section.
    /// </summary>
    public ContentSide Interior => (ContentSide)((Packed >> InteriorShift) & FieldMask);

    /// <summary>
    ///     The samples at the section's lowest X.
    /// </summary>
    public ContentSide LowX => (ContentSide)((Packed >> LowXShift) & FieldMask);

    /// <summary>
    ///     The samples at the section's lowest Y.
    /// </summary>
    public ContentSide LowY => (ContentSide)((Packed >> LowYShift) & FieldMask);

    /// <summary>
    ///     The samples at the section's lowest Z.
    /// </summary>
    public ContentSide LowZ => (ContentSide)((Packed >> LowZShift) & FieldMask);

    /// <summary>
    ///     The sides of a section whose samples are all on <paramref name="side"/>, which its faces then are too.
    /// </summary>
    public static SectionContentSides FromOneSide(ContentSide side)
    {
        return new SectionContentSides(side, side, side, side);
    }

    /// <summary>
    ///     Scans a section's content box, x fastest. The faces are only scanned when the section is on both sides.
    /// </summary>
    public static SectionContentSides FromContent(Span3D<byte> content)
    {
        var data = content.Data;
        var interior = GetSide(data);

        if (interior != ContentSide.Mixed)
        {
            return FromOneSide(interior);
        }

        var planeSize = content.SizeX * content.SizeY;
        var lowX = ContentSide.None;
        var lowY = ContentSide.None;

        for (var z = 0; z < content.SizeZ; z++)
        {
            lowY |= GetSide(data.Slice(z * planeSize, content.SizeX));

            for (var y = 0; y < content.SizeY; y++)
            {
                lowX |= GetSide(data[z * planeSize + y * content.SizeX]);
            }
        }

        return new SectionContentSides(interior, lowX, lowY, GetSide(data[..planeSize]));
    }

    /// <summary>
    ///     Scans a stored section's content bricks, which is what a write uses instead of expanding the section first.
    ///     A sample is solid exactly when its high bit is set, so a dense brick's 64 samples become one solid mask in two Vector256 loads, and each face is a test of that mask.
    /// </summary>
    public static SectionContentSides FromSection(Section section)
    {
        Debug.Assert(WorldConstants.ContentIsoLevel == sbyte.MaxValue, "The solid mask is the samples' high bit, which only matches an iso level of 127.");

        var interior = ContentSide.None;
        var lowX = ContentSide.None;
        var lowY = ContentSide.None;
        var lowZ = ContentSide.None;

        for (var brickIndex = 0; brickIndex < Section.Bricks; brickIndex++)
        {
            var solid = GetSolidMask(section, brickIndex);

            interior |= GetSide(solid, ulong.MaxValue);

            if (brickIndex % Section.BricksPerAxis == 0)
            {
                lowX |= GetSide(solid, BrickLowXVoxels);
            }

            if (brickIndex / Section.BricksPerAxis % Section.BricksPerAxis == 0)
            {
                lowY |= GetSide(solid, BrickLowYVoxels);
            }

            if (brickIndex < Section.BricksPerAxis * Section.BricksPerAxis)
            {
                lowZ |= GetSide(solid, BrickLowZVoxels);
            }
        }

        return new SectionContentSides(interior, lowX, lowY, lowZ);
    }

    /// <summary>
    ///     Whether the mesher emits any quad for the cells of a section, given the section and its positive neighbours on each axis.
    ///     It does exactly when some edge those cells own crosses the iso level, which is when the section and the three faces are not all on one side.
    /// </summary>
    public static bool HasSurface(SectionContentSides section, SectionContentSides positiveX, SectionContentSides positiveY, SectionContentSides positiveZ)
    {
        return (section.Interior | positiveX.LowX | positiveY.LowY | positiveZ.LowZ) == ContentSide.Mixed;
    }

    private static ContentSide GetSide(ReadOnlySpan<byte> samples)
    {
        var side = ContentSide.None;

        if (samples.ContainsAnyInRange(byte.MinValue, WorldConstants.ContentIsoLevel))
        {
            side |= ContentSide.Air;
        }

        if (samples.ContainsAnyInRange((byte)(WorldConstants.ContentIsoLevel + 1), byte.MaxValue))
        {
            side |= ContentSide.Solid;
        }

        return side;
    }

    private static ContentSide GetSide(byte sample)
    {
        return sample > WorldConstants.ContentIsoLevel ? ContentSide.Solid : ContentSide.Air;
    }

    /// <summary>
    ///     The sides of the voxels in <paramref name="voxels"/>, given which voxels of the brick are solid.
    /// </summary>
    private static ContentSide GetSide(ulong solid, ulong voxels)
    {
        var side = ContentSide.None;

        if ((~solid & voxels) != 0)
        {
            side |= ContentSide.Air;
        }

        if ((solid & voxels) != 0)
        {
            side |= ContentSide.Solid;
        }

        return side;
    }

    /// <summary>
    ///     One bit per voxel of a content brick, set where the voxel is solid, in the brick's x fastest order.
    /// </summary>
    private static ulong GetSolidMask(Section section, int brickIndex)
    {
        if (section.GetBrickType(VoxelChannel.Content, brickIndex) == BrickType.Uniform)
        {
            return section.GetUniformValue(VoxelChannel.Content, brickIndex) > WorldConstants.ContentIsoLevel ? ulong.MaxValue : 0;
        }

        var voxels = section.GetDense(VoxelChannel.Content, brickIndex).Span;
        var low = Vector256.Create<byte>(voxels);
        var high = Vector256.Create<byte>(voxels[Vector256<byte>.Count..]);

        return low.ExtractMostSignificantBits() | ((ulong)high.ExtractMostSignificantBits() << Vector256<byte>.Count);
    }

    public bool Equals(SectionContentSides other)
    {
        return Packed == other.Packed;
    }

    public override bool Equals(object? obj)
    {
        return obj is SectionContentSides other && Equals(other);
    }

    public override int GetHashCode()
    {
        return Packed;
    }

    public override string ToString()
    {
        return $"({Interior}, {LowX}, {LowY}, {LowZ})";
    }

    public static bool operator ==(SectionContentSides left, SectionContentSides right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(SectionContentSides left, SectionContentSides right)
    {
        return !left.Equals(right);
    }
}
