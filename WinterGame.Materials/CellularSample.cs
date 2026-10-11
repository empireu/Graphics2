namespace WinterGame.Materials;

/// <summary>
///     What <see cref="TileNoise.Cellular"/> finds at a point, with distances in cells.
///     A feature point's region is the set of points nearer to it than to any other.
/// </summary>
public readonly struct CellularSample(float distance, float edgeDistance, float regionValue)
{
    /// <summary>
    ///     The distance to the nearest feature point.
    /// </summary>
    public readonly float Distance = distance;

    /// <summary>
    ///     The distance to the border of the nearest feature point's region, which is 0 on the border.
    /// </summary>
    public readonly float EdgeDistance = edgeDistance;

    /// <summary>
    ///     A random value in [0, 1) that belongs to the nearest feature point.
    /// </summary>
    public readonly float RegionValue = regionValue;
}
