using System.Numerics;
using System.Runtime.CompilerServices;
using WinterGame.World.Storage;

namespace WinterGame.World.Lighting;

/// <summary>
///     A cell's light packed in a ushort. Bits 0 to 3 hold the level.
///     A cell whose air is split into several components also sets <see cref="HasSeed"/> and keeps a seed voxel in bits 4 to 9.
///     The cell's lit air is then the component that contains the seed voxel.
/// </summary>
internal static class LightWord
{
    public const int LevelMask = 0xF;
    public const int SeedShift = 4;
    public const int SeedMask = 63;

    /// <summary>
    ///     Set when the seed voxel picks the lit component. When clear, all of the cell's air is lit.
    /// </summary>
    public const int HasSeed = 1 << 10;

    /// <summary>
    ///     The lit air of a cell, given its light word and its air mask.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong GetLitAir(int word, ulong air)
    {
        return (word & HasSeed) != 0 ? CubeMask.Flood(air, 1UL << ((word >> SeedShift) & SeedMask)) : air;
    }

    /// <summary>
    ///     The light word of a sky cell, given the lit air of the cell above it.
    ///     When the air is split, the lit component is the one that <paramref name="litAbove"/> touches across the top face.
    ///     When it touches none, the lit component is the one holding the first open column (<see cref="BrickAir.GetOpenColumnVoxel"/>).
    ///     When there is no open column either, all of the air is lit.
    /// </summary>
    public static ushort Sky(ulong air, bool isSplit, ulong litAbove)
    {
        if (!isSplit)
        {
            return LightEngine.MaxLevel;
        }

        var lit = CubeMask.Flood(air, CubeMask.CrossNegativeY(litAbove));
        var voxel = lit != 0 ? BitOperations.TrailingZeroCount(lit) : BrickAir.GetOpenColumnVoxel(air);

        return voxel < 0 ? (ushort)LightEngine.MaxLevel : (ushort)(LightEngine.MaxLevel | HasSeed | (voxel << SeedShift));
    }
}
