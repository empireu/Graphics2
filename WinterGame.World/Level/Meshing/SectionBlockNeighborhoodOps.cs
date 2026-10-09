using System.Runtime.CompilerServices;
using WinterGame.World.Storage;

namespace WinterGame.World.Level.Meshing;

/// <summary>
///     Block mask arithmetic over a block and its 26 neighbors.
///     A block mask is a <see cref="CubeMask"/> with one bit per section, in the layout of <see cref="SectionBlockPos.GetLocalIndex(int,int,int)"/>.
///     A neighborhood is 27 masks indexed by <see cref="GetIndex"/>, and a block that is not there is a zero mask.
/// </summary>
internal static class SectionBlockNeighborhoodOps
{
    public const int Count = 27;

    /// <summary>
    ///     The index of the block at the offset from the center block, each offset in [-1, 1].
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetIndex(int offsetX, int offsetY, int offsetZ)
    {
        return offsetX + 1 + 3 * (offsetY + 1) + 9 * (offsetZ + 1);
    }

    /// <summary>
    ///     The sections of a block within one section of the neighbor block at the offset, so the 3x3x3 sections around each of them reach into it.
    /// </summary>
    public static ulong GetBorder(int offsetX, int offsetY, int offsetZ)
    {
        return GetAxisBorder(offsetX, CubeMask.LowX, CubeMask.HighX) & GetAxisBorder(offsetY, CubeMask.LowY, CubeMask.HighY) & GetAxisBorder(offsetZ, CubeMask.LowZ, CubeMask.HighZ);
    }

    /// <summary>
    ///     The sections of the center block whose 3x3x3 sections are all set.
    /// </summary>
    public static ulong Erode(ReadOnlySpan<ulong> masks)
    {
        return Combine(masks, grow: false);
    }

    /// <summary>
    ///     The sections of the center block with any of their 3x3x3 sections set.
    /// </summary>
    public static ulong Dilate(ReadOnlySpan<ulong> masks)
    {
        return Combine(masks, grow: true);
    }

    private static ulong GetAxisBorder(int offset, ulong lowFace, ulong highFace)
    {
        return offset switch
        {
            < 0 => lowFace,
            > 0 => highFace,
            _ => ulong.MaxValue
        };
    }

    /// <summary>
    ///     Combines the three sections around each section along X, then Y, then Z, which is the whole 3x3x3 because a box is separable.
    /// </summary>
    private static ulong Combine(ReadOnlySpan<ulong> masks, bool grow)
    {
        if (masks.Length != Count)
        {
            throw new ArgumentException($"A neighborhood is {Count} masks, not {masks.Length}.", nameof(masks));
        }

        // Each row along X of the 3x3 blocks in Y and Z, combined along X:
        Span<ulong> alongX = stackalloc ulong[9];

        for (var row = 0; row < alongX.Length; row++)
        {
            alongX[row] = CombineAlongAxis(masks[3 * row], masks[3 * row + 1], masks[3 * row + 2], CubeMask.StrideX, CubeMask.LowX, CubeMask.HighX, grow);
        }

        var lowZ = CombineAlongAxis(alongX[0], alongX[1], alongX[2], CubeMask.StrideY, CubeMask.LowY, CubeMask.HighY, grow);
        var centerZ = CombineAlongAxis(alongX[3], alongX[4], alongX[5], CubeMask.StrideY, CubeMask.LowY, CubeMask.HighY, grow);
        var highZ = CombineAlongAxis(alongX[6], alongX[7], alongX[8], CubeMask.StrideY, CubeMask.LowY, CubeMask.HighY, grow);

        return CombineAlongAxis(lowZ, centerZ, highZ, CubeMask.StrideZ, CubeMask.LowZ, CubeMask.HighZ, grow);
    }

    /// <summary>
    ///     Combines each section of <paramref name="center"/> with its two neighbors along one axis, where the neighbor past a face is in the block on that side.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong CombineAlongAxis(ulong low, ulong center, ulong high, int stride, ulong lowFace, ulong highFace, bool grow)
    {
        var faceDistance = (SectionBlockPos.BlockSize - 1) * stride;
        var next = ((center >> stride) & ~highFace) | ((high & lowFace) << faceDistance);
        var previous = ((center << stride) & ~lowFace) | ((low & highFace) >> faceDistance);

        return grow ? center | next | previous : center & next & previous;
    }
}
