using System.Diagnostics;
using WinterGame.World.Storage;

namespace WinterGame.World.Lighting;

/// <summary>
///     Finds the bricks of a section that block light falling straight down.
///     A brick blocks when none of its 16 voxel columns along Y is air from top to bottom.
/// </summary>
internal static class SkyBlockers
{
    /// <summary>
    ///     The brick columns of a section. A column's index is <c>x + z * <see cref="Section.BricksPerAxis"/></c> in brick coordinates inside the section.
    /// </summary>
    public const int ColumnsPerSection = Section.BricksPerAxis * Section.BricksPerAxis;

    /// <summary>
    ///     Writes one byte per brick column of the section into <paramref name="blockers"/>, with bit <c>y</c> set when the column's brick at height <c>y</c> blocks.
    ///     Returns false when no brick blocks.
    /// </summary>
    public static bool Scan(Section section, Span<byte> blockers)
    {
        Debug.Assert(Section.BricksPerAxis == 8, "A brick column's blockers are one byte, which only holds a section 8 bricks tall.");
        Debug.Assert(blockers.Length == ColumnsPerSection, $"The blockers hold {blockers.Length} columns, not {ColumnsPerSection}.");

        // A shared section holds one content value in every brick:
        if (section.Type == SectionType.Shared)
        {
            var solid = section.GetUniformValue(VoxelChannel.Content, 0) > WorldConstants.ContentIsoLevel;

            blockers.Fill(solid ? byte.MaxValue : (byte)0);
            return solid;
        }

        var any = false;

        blockers.Clear();

        for (var z = 0; z < Section.BricksPerAxis; z++)
        {
            for (var y = 0; y < Section.BricksPerAxis; y++)
            {
                for (var x = 0; x < Section.BricksPerAxis; x++)
                {
                    if (!Blocks(section.GetSolidMask(Section.GetBrickIndex(x, y, z))))
                    {
                        continue;
                    }

                    blockers[x + z * Section.BricksPerAxis] |= (byte)(1 << y);
                    any = true;
                }
            }
        }

        return any;
    }

    /// <summary>
    ///     Whether a brick blocks, given its solid mask (<see cref="Section.GetSolidMask"/>).
    /// </summary>
    public static bool Blocks(ulong solid)
    {
        return CubeMask.GetFullColumnsY(~solid) == 0;
    }
}
