using System.Numerics;
using WinterGame.World.Storage;

namespace WinterGame.World.Lighting;

/// <summary>
///     Operations on a brick's air mask.
///     An air mask is a <see cref="CubeMask"/> of the brick's voxels with a bit set where the voxel is air.
/// </summary>
internal static class BrickAir
{
    /// <summary>
    ///     The air mask of a brick with no solid voxel.
    /// </summary>
    public const ulong Full = ulong.MaxValue;

    /// <summary>
    ///     Whether the air is a single component of voxels that share faces. No air counts as one component.
    /// </summary>
    public static bool IsOneComponent(ulong air)
    {
        return air == 0 || CubeMask.Flood(air, 1UL << BitOperations.TrailingZeroCount(air)) == air;
    }

    /// <summary>
    ///     The bottom voxel of the brick's first open column, or -1 when it has none.
    ///     An open column is a column of four voxels along Y that are all air.
    /// </summary>
    public static int GetOpenColumnVoxel(ulong air)
    {
        var open = CubeMask.GetFullColumnsY(air);

        return open == 0 ? -1 : BitOperations.TrailingZeroCount(open);
    }
}
