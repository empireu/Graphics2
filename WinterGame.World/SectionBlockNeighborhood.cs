using System.Runtime.CompilerServices;

namespace WinterGame.World;

/// <summary>
///     Block mask arithmetic over a block and its 26 neighbors, one bit per section in the layout of <see cref="SectionBlockPos.GetLocalIndex(int, int, int)"/>.
///     A neighborhood is 27 masks indexed by <see cref="GetIndex"/>, and a block that is not there is a zero mask.
/// </summary>
internal static class SectionBlockNeighborhood
{
    public const int Count = 27;

    private const int XStride = 1;
    private const int YStride = SectionBlockPos.BlockSize;
    private const int ZStride = SectionBlockPos.BlockSize * SectionBlockPos.BlockSize;

    private const ulong LowXFace = 0x1111_1111_1111_1111;
    private const ulong HighXFace = 0x8888_8888_8888_8888;
    private const ulong LowYFace = 0x000F_000F_000F_000F;
    private const ulong HighYFace = 0xF000_F000_F000_F000;
    private const ulong LowZFace = 0x0000_0000_0000_FFFF;
    private const ulong HighZFace = 0xFFFF_0000_0000_0000;

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
        return GetAxisBorder(offsetX, LowXFace, HighXFace) & GetAxisBorder(offsetY, LowYFace, HighYFace) & GetAxisBorder(offsetZ, LowZFace, HighZFace);
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
            alongX[row] = CombineAlongAxis(masks[3 * row], masks[3 * row + 1], masks[3 * row + 2], XStride, LowXFace, HighXFace, grow);
        }

        var lowZ = CombineAlongAxis(alongX[0], alongX[1], alongX[2], YStride, LowYFace, HighYFace, grow);
        var centerZ = CombineAlongAxis(alongX[3], alongX[4], alongX[5], YStride, LowYFace, HighYFace, grow);
        var highZ = CombineAlongAxis(alongX[6], alongX[7], alongX[8], YStride, LowYFace, HighYFace, grow);

        return CombineAlongAxis(lowZ, centerZ, highZ, ZStride, LowZFace, HighZFace, grow);
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
