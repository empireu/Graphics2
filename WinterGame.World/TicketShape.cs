namespace WinterGame.World;

/// <summary>
///     A vertical cylinder in section units, stored as the half width in X of every row of its disc, so membership is an interval test and the difference between two placements is interval arithmetic.
///     A section at an offset from the center is inside when <c>dx * dx + dz * dz &lt;= HorizontalRadius * HorizontalRadius</c> and <c>|dy| &lt;= VerticalRadius</c>.
/// </summary>
internal sealed class TicketShape
{
    public readonly int HorizontalRadius;
    public readonly int VerticalRadius;

    /// <summary>
    ///     The largest |dx| inside the disc for each |dz| in [0, <see cref="HorizontalRadius"/>].
    /// </summary>
    private readonly int[] _halfWidths;

    public TicketShape(int horizontalRadius, int verticalRadius)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(horizontalRadius);
        ArgumentOutOfRangeException.ThrowIfNegative(verticalRadius);

        HorizontalRadius = horizontalRadius;
        VerticalRadius = verticalRadius;
        _halfWidths = new int[horizontalRadius + 1];

        var radiusSquared = (long)horizontalRadius * horizontalRadius;

        for (var deltaZ = 0; deltaZ <= horizontalRadius; deltaZ++)
        {
            var remaining = radiusSquared - (long)deltaZ * deltaZ;
            var halfWidth = (long)Math.Sqrt(remaining);

            // The square root is rounded, so it is corrected to the exact integer bound of the membership test:
            while (halfWidth * halfWidth > remaining)
            {
                halfWidth--;
            }

            while ((halfWidth + 1) * (halfWidth + 1) <= remaining)
            {
                halfWidth++;
            }

            _halfWidths[deltaZ] = (int)halfWidth;
        }
    }

    /// <summary>
    ///     The inclusive X interval of the row at <paramref name="y"/>, <paramref name="z"/> for the shape placed at <paramref name="center"/>.
    ///     False when the shape holds no section in that row.
    /// </summary>
    public bool TryGetRow(SectionPos center, int y, int z, out int minX, out int maxX)
    {
        var deltaZ = Math.Abs(z - center.Z);

        if (deltaZ > HorizontalRadius || Math.Abs(y - center.Y) > VerticalRadius)
        {
            minX = 0;
            maxX = -1;

            return false;
        }

        var halfWidth = _halfWidths[deltaZ];

        minX = center.X - halfWidth;
        maxX = center.X + halfWidth;

        return true;
    }

    /// <summary>
    ///     False when the shape placed at the two centers cannot share a section, because their bounding boxes are apart on some axis.
    /// </summary>
    public bool CanOverlap(SectionPos first, SectionPos second)
    {
        var horizontalReach = 2L * HorizontalRadius;

        return Math.Abs((long)first.X - second.X) <= horizontalReach &&
               Math.Abs((long)first.Z - second.Z) <= horizontalReach &&
               Math.Abs((long)first.Y - second.Y) <= 2L * VerticalRadius;
    }
}
