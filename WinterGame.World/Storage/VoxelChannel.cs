using System.Runtime.CompilerServices;

namespace WinterGame.World.Storage;

/// <summary>
///     Which of a brick's two stored fields a type or a value describes.
/// </summary>
public enum VoxelChannel : byte
{
    /// <summary>
    ///     The normalized signed distance, where larger values are closer to solid.
    /// </summary>
    Content = 0,

    /// <summary>
    ///     An index into the material table, with 255 meaning no material.
    /// </summary>
    Material = 1
}

public static class VoxelChannelExtensions
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte GetClearValue(this VoxelChannel channel) => channel switch
    {
        VoxelChannel.Content => 0,
        VoxelChannel.Material => 255,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, $"Invalid voxel channel {channel}")
    };
}