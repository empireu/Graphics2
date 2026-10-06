namespace WinterGame.World.Generation;

/// <summary>
///     The geology provinces as banded tables stacked upward from one shared basement top.
///     The provinces share the deep basement and their own character comes from the thickness of the shared beds, which interpolates smoothly.
///     The only mismatched bed is a thin marker, because a pinch trough is as deep as the bed that pinches and a thick one would read as a crack.
/// </summary>
public static class TerrainStrata
{
    /// <summary>
    ///     The top of the basement, so the cover is measured upward from here. Below it is the basement every province shares.
    /// </summary>
    public const float BasementTopY = -30.0f;

    /// <summary>
    ///     Half the width of a province boundary, in province coordinates. A thin pinch only needs a moderate run.
    /// </summary>
    public const float DefaultTransitionHalfWidth = 0.3f;

    /// <summary>
    ///     A sedimentary cover over a shared basement, where the second province thickens the lower beds and replaces the thin sandstone marker with a basalt flow.
    ///     Both covers reach 26 m, above the highest surface the shape can produce.
    /// </summary>
    public static readonly StrataTable[] Tables =
    [
        new(
            BasementTopY,
            SurfaceMaterial.Gneiss,
            [8.0f, 7.0f, 6.0f, 2.0f, 9.0f, 24.0f],
            [
                SurfaceMaterial.Basalt,
                SurfaceMaterial.Limestone,
                SurfaceMaterial.Shale,
                SurfaceMaterial.Sandstone,
                SurfaceMaterial.Limestone,
                SurfaceMaterial.Sandstone
            ]
        ),
        new(
            BasementTopY,
            SurfaceMaterial.Gneiss,
            [12.0f, 9.0f, 6.0f, 2.0f, 9.0f, 18.0f],
            [
                SurfaceMaterial.Basalt,
                SurfaceMaterial.Limestone,
                SurfaceMaterial.Shale,
                SurfaceMaterial.Basalt,
                SurfaceMaterial.Limestone,
                SurfaceMaterial.Sandstone
            ]
        )
    ];

    public static readonly StrataSet Provinces = new(DefaultTransitionHalfWidth, Tables);
}
