using System.Runtime.CompilerServices;

namespace WinterGame.World.Storage;

/// <summary>
///     Bit operations on a 4x4x4 cube of bits held in a ulong, where bit x + 4 * y + 16 * z stands for the cell at (x, y, z).
///     A brick's voxels and a section block's sections both use this layout.
/// </summary>
internal static class CubeMask
{
    /// <summary>
    ///     The distance in bits between two neighboring cells along each axis.
    /// </summary>
    public const int StrideX = 1;

    public const int StrideY = 4;
    public const int StrideZ = 16;

    /// <summary>
    ///     The bits of each face of the cube. LowX holds every bit with x = 0, and HighX every bit with x = 3.
    /// </summary>
    public const ulong LowX = 0x1111_1111_1111_1111;

    public const ulong HighX = LowX << (3 * StrideX);
    public const ulong LowY = 0x000F_000F_000F_000F;
    public const ulong HighY = LowY << (3 * StrideY);
    public const ulong LowZ = 0x0000_0000_0000_FFFF;
    public const ulong HighZ = LowZ << (3 * StrideZ);

    /// <summary>
    ///     Moves every bit one cell toward +X. Bits that leave the cube are dropped.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong StepPositiveX(ulong mask) => (mask << StrideX) & ~LowX;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong StepNegativeX(ulong mask) => (mask >> StrideX) & ~HighX;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong StepPositiveY(ulong mask) => (mask << StrideY) & ~LowY;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong StepNegativeY(ulong mask) => (mask >> StrideY) & ~HighY;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong StepPositiveZ(ulong mask) => mask << StrideZ;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong StepNegativeZ(ulong mask) => mask >> StrideZ;

    /// <summary>
    ///     Moves the bits on the high X face across it, onto the low X face of the next cube along +X.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong CrossPositiveX(ulong mask) => (mask & HighX) >> (3 * StrideX);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong CrossNegativeX(ulong mask) => (mask & LowX) << (3 * StrideX);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong CrossPositiveY(ulong mask) => (mask & HighY) >> (3 * StrideY);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong CrossNegativeY(ulong mask) => (mask & LowY) << (3 * StrideY);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong CrossPositiveZ(ulong mask) => (mask & HighZ) >> (3 * StrideZ);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong CrossNegativeZ(ulong mask) => (mask & LowZ) << (3 * StrideZ);

    /// <summary>
    ///     The columns along Y whose four bits are all set, each marked by its bit at y = 0.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetFullColumnsY(ulong mask) => mask & (mask >> StrideY) & (mask >> (2 * StrideY)) & (mask >> (3 * StrideY)) & LowY;

    /// <summary>
    ///     The set bits of <paramref name="mask"/> connected to <paramref name="seed"/> through cells that share a face.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Flood(ulong mask, ulong seed)
    {
        var filled = seed & mask;

        while (true)
        {
            var grown = (filled |
                         StepPositiveX(filled) | StepNegativeX(filled) |
                         StepPositiveY(filled) | StepNegativeY(filled) |
                         StepPositiveZ(filled) | StepNegativeZ(filled)) & mask;

            if (grown == filled)
            {
                return filled;
            }

            filled = grown;
        }
    }
}
