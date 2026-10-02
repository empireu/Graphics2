using Graphics2.Data;

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
