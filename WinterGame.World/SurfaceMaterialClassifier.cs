using Graphics2.Mathematics;

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

public sealed class SurfaceMaterialClassifier
{
    private readonly float _snowLine;
    private readonly float _snowLineFade;
    private readonly float _snowSlopeMin;
    private readonly float _snowSlopeMax;
    private readonly float _rockSlopeMin;
    private readonly float _rockSlopeMax;
    
    public SurfaceMaterialClassifier(
        float snowLine,
        float snowLineFade = 3.0f,
        float snowSlopeMin = 0.80f,
        float snowSlopeMax = 0.95f,
        float rockSlopeMin = 0.45f,
        float rockSlopeMax = 0.70f)
    {
        _snowLine = snowLine;
        _snowLineFade = MathF.Max(snowLineFade, 0.0f);
        _snowSlopeMin = MathF.Min(snowSlopeMin, snowSlopeMax);
        _snowSlopeMax = MathF.Max(snowSlopeMin, snowSlopeMax);
        _rockSlopeMin = MathF.Min(rockSlopeMin, rockSlopeMax);
        _rockSlopeMax = MathF.Max(rockSlopeMin, rockSlopeMax);
    }

    public SurfaceMaterialScores Score(float slope, float height)
    {
        var flat = MathX.CheckedMapSmoothStepSingular(_snowSlopeMin, _snowSlopeMax, slope);
        var above = MathX.CheckedMapSmoothStepSingular(_snowLine - _snowLineFade, _snowLine + _snowLineFade, height);
        var steep = MathX.CheckedMapSmoothStepSingular(_rockSlopeMin, _rockSlopeMax, 1.0f - slope);

        var snow = flat * above;
        var ice = (1.0f - steep) * (1.0f - above) * flat;

        return new SurfaceMaterialScores(snow, ice, 1.0f - MathF.Max(snow, ice));
    }

    public byte Classify(float slope, float height)
    {
        var scores = Score(slope, height);

        if (scores.Snow >= scores.Ice && scores.Snow >= scores.Rock)
        {
            return SurfaceMaterial.Snow;
        }

        return scores.Ice >= scores.Rock ? SurfaceMaterial.Ice : SurfaceMaterial.Rock;
    }
}
