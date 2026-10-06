using WinterGame.World.Storage;

namespace WinterGame.World.Level.Residency;

/// <summary>
///     One X interval of one row of sections, and whether a walk adds its sections to the placement or removes them.
/// </summary>
internal readonly struct TicketShapeRun(int minX, int maxX, int y, int z, bool entering)
{
    public readonly int MinX = minX;
    public readonly int MaxX = maxX;
    public readonly int Y = y;
    public readonly int Z = z;
    public readonly bool Entering = entering;
}

/// <summary>
///     Receives a walk over a shape one block of sections at a time.
/// </summary>
internal interface ITicketShapeVisitor
{
    /// <summary>
    ///     Applies the runs of one row of blocks to the block at <paramref name="blockX"/>, which at least one of them overlaps. A run can reach past the block on either side, so it is clipped to the block.
    /// </summary>
    void VisitBlock(int blockX, int blockY, int blockZ, ReadOnlySpan<TicketShapeRun> runs);
}

/// <summary>
///     A vertical cylinder in section units, stored as the half width in X of every row of its disc, so membership is an interval test and the difference between two placements is interval arithmetic.
///     A section at an offset from the center is inside when <c>dx * dx + dz * dz &lt;= HorizontalRadius * HorizontalRadius</c> and <c>|dy| &lt;= VerticalRadius</c>.
/// </summary>
internal sealed class TicketShape
{
    /// <summary>
    ///     The most runs one row of blocks can hold: each of its rows has at most two intervals entering a shape and two leaving it.
    /// </summary>
    private const int MaxRunsPerBlockRow = SectionBlockPos.BlockSize * SectionBlockPos.BlockSize * 4;

    /// <summary>
    ///     The widest row of blocks whose scratch is taken from the stack.
    /// </summary>
    private const int MaxStackBlocksPerRow = 256;

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

    /// <summary>
    ///     Visits every section of the shape placed at <paramref name="center"/> block by block, with every run marked <paramref name="entering"/>.
    /// </summary>
    public void Walk<TVisitor>(SectionPos center, bool entering, TVisitor visitor) where TVisitor : struct, ITicketShapeVisitor
    {
        Span<TicketShapeRun> runs = stackalloc TicketShapeRun[MaxRunsPerBlockRow];

        var minY = center.Y - VerticalRadius;
        var maxY = center.Y + VerticalRadius;
        var minZ = center.Z - HorizontalRadius;
        var maxZ = center.Z + HorizontalRadius;

        for (var blockZ = minZ >> SectionBlockPos.Log; blockZ <= maxZ >> SectionBlockPos.Log; blockZ++)
        {
            for (var blockY = minY >> SectionBlockPos.Log; blockY <= maxY >> SectionBlockPos.Log; blockY++)
            {
                var count = 0;

                for (var z = Math.Max(minZ, blockZ << SectionBlockPos.Log); z <= Math.Min(maxZ, (blockZ << SectionBlockPos.Log) + SectionBlockPos.BlockMask); z++)
                {
                    for (var y = Math.Max(minY, blockY << SectionBlockPos.Log); y <= Math.Min(maxY, (blockY << SectionBlockPos.Log) + SectionBlockPos.BlockMask); y++)
                    {
                        if (TryGetRow(center, y, z, out var minX, out var maxX))
                        {
                            runs[count++] = new TicketShapeRun(minX, maxX, y, z, entering);
                        }
                    }
                }

                VisitBlocks(runs[..count], blockY, blockZ, visitor);
            }
        }
    }

    /// <summary>
    ///     Visits the sections inside the shape at <paramref name="center"/> and outside it at <paramref name="previous"/> as entering, and the reverse as leaving.
    ///     Each row of the two placements is one X interval, so a row's difference is at most two intervals per direction and no section is tested on its own.
    /// </summary>
    public void WalkDifference<TVisitor>(SectionPos previous, SectionPos center, TVisitor visitor) where TVisitor : struct, ITicketShapeVisitor
    {
        Span<TicketShapeRun> runs = stackalloc TicketShapeRun[MaxRunsPerBlockRow];

        var minY = Math.Min(previous.Y, center.Y) - VerticalRadius;
        var maxY = Math.Max(previous.Y, center.Y) + VerticalRadius;
        var minZ = Math.Min(previous.Z, center.Z) - HorizontalRadius;
        var maxZ = Math.Max(previous.Z, center.Z) + HorizontalRadius;

        for (var blockZ = minZ >> SectionBlockPos.Log; blockZ <= maxZ >> SectionBlockPos.Log; blockZ++)
        {
            for (var blockY = minY >> SectionBlockPos.Log; blockY <= maxY >> SectionBlockPos.Log; blockY++)
            {
                var count = 0;

                for (var z = Math.Max(minZ, blockZ << SectionBlockPos.Log); z <= Math.Min(maxZ, (blockZ << SectionBlockPos.Log) + SectionBlockPos.BlockMask); z++)
                {
                    for (var y = Math.Max(minY, blockY << SectionBlockPos.Log); y <= Math.Min(maxY, (blockY << SectionBlockPos.Log) + SectionBlockPos.BlockMask); y++)
                    {
                        var wasInside = TryGetRow(previous, y, z, out var previousMinX, out var previousMaxX);
                        var isInside = TryGetRow(center, y, z, out var minX, out var maxX);

                        if (isInside)
                        {
                            count = AddRunsOutside(runs, count, true, minX, maxX, wasInside, previousMinX, previousMaxX, y, z);
                        }

                        if (wasInside)
                        {
                            count = AddRunsOutside(runs, count, false, previousMinX, previousMaxX, isInside, minX, maxX, y, z);
                        }
                    }
                }

                VisitBlocks(runs[..count], blockY, blockZ, visitor);
            }
        }
    }

    /// <summary>
    ///     Adds the part of the row from <paramref name="minX"/> to <paramref name="maxX"/> outside the excluded interval, when there is one, and returns the new run count.
    /// </summary>
    private static int AddRunsOutside(Span<TicketShapeRun> runs, int count, bool entering, int minX, int maxX, bool hasExcluded, int excludedMinX, int excludedMaxX, int y, int z)
    {
        if (!hasExcluded || excludedMaxX < minX || excludedMinX > maxX)
        {
            runs[count++] = new TicketShapeRun(minX, maxX, y, z, entering);
            return count;
        }

        if (minX < excludedMinX)
        {
            runs[count++] = new TicketShapeRun(minX, excludedMinX - 1, y, z, entering);
        }

        if (maxX > excludedMaxX)
        {
            runs[count++] = new TicketShapeRun(excludedMaxX + 1, maxX, y, z, entering);
        }

        return count;
    }

    /// <summary>
    ///     Hands the runs of one row of blocks to the visitor one block at a time, so each block the runs touch is looked up once.
    /// </summary>
    private static void VisitBlocks<TVisitor>(ReadOnlySpan<TicketShapeRun> runs, int blockY, int blockZ, TVisitor visitor) where TVisitor : struct, ITicketShapeVisitor
    {
        if (runs.IsEmpty)
        {
            return;
        }

        var minX = int.MaxValue;
        var maxX = int.MinValue;

        foreach (var run in runs)
        {
            minX = Math.Min(minX, run.MinX);
            maxX = Math.Max(maxX, run.MaxX);
        }

        var firstBlockX = minX >> SectionBlockPos.Log;
        var blockCount = (maxX >> SectionBlockPos.Log) - firstBlockX + 1;

        // A move's runs sit at the two ends of the row, so only the blocks some run touches are visited:
        Span<bool> touched = blockCount <= MaxStackBlocksPerRow ? stackalloc bool[blockCount] : new bool[blockCount];

        foreach (var run in runs)
        {
            touched[((run.MinX >> SectionBlockPos.Log) - firstBlockX)..((run.MaxX >> SectionBlockPos.Log) - firstBlockX + 1)].Fill(true);
        }

        for (var offset = 0; offset < blockCount; offset++)
        {
            if (touched[offset])
            {
                visitor.VisitBlock(firstBlockX + offset, blockY, blockZ, runs);
            }
        }
    }
}
