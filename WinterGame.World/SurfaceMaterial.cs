namespace WinterGame.World;

public static class SurfaceMaterial
{
    /// <summary>
    ///     The generic rock the classifier writes and the shader falls back to when no id is found.
    /// </summary>
    public const byte Rock = 0;

    public const byte Ice = 1;
    public const byte Snow = 2;

    // The strata ids, which the banded table writes and the shader's palette is indexed by:
    public const byte Sandstone = 3;
    public const byte Shale = 4;
    public const byte Limestone = 5;
    public const byte Basalt = 6;
    public const byte Gneiss = 7;

    public const byte Null = 255;

    public const int Count = 8;
}

public readonly struct SurfaceMaterialScores(float snow, float ice, float rock)
{
    public readonly float Snow = snow;

    public readonly float Ice = ice;

    public readonly float Rock = rock;
}