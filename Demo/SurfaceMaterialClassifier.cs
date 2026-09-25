using Graphics2.Mathematics;
using WinterGame.World;

namespace Demo;

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
