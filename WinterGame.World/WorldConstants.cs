namespace WinterGame.World;

/// <summary>
///     The world's fixed metric scale, the one place a sample index becomes a world position.
/// </summary>
public static class WorldConstants
{
    /// <summary>
    ///     The sample spacing. A sample index maps to a world position as index * VoxelSize, and every generator constant is in meters.
    /// </summary>
    public const float VoxelSize = 0.25f;

    /// <summary>
    ///     The content byte of the iso surface.
    /// </summary>
    public const byte ContentIsoLevel = 127;

    /// <summary>
    ///     Content units one sample of distance resolves to, which puts saturation about one sample either side of the surface.
    /// </summary>
    public const float ContentBytesPerSample = 127.0f;
}
