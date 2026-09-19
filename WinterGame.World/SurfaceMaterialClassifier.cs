using Graphics2.Mathematics;

namespace WinterGame.World;

/// <summary>
///     The ids the material volume stores, which are also the indices of the shader's palette.
/// </summary>
public static class SurfaceMaterial
{
    public const byte Rock = 0;
    public const byte Ice = 1;
    public const byte Snow = 2;

    /// <summary>
    ///     Air carries no material. The reconstruction skips it, so it normalizes over the solid corners of a cell only.
    /// </summary>
    public const byte Null = 255;

    public const int Count = 3;
}

public readonly struct SurfaceMaterialScores(float snow, float ice, float rock)
{
    public readonly float Snow = snow;

    public readonly float Ice = ice;

    public readonly float Rock = rock;
}

/// <summary>
///     Turns a surface sample's slope and height into a material id.
///     Every material gets a score and the id written is the highest one, so the boundary between two materials sits at a smooth locus of the slope and height fields rather than on a triangle edge.
///     <para />
///     Below the snow line the frozen ground reads as ice, above it the flats hold snow, and slopes too steep to hold snow stay rock.
/// </summary>
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
        var flat = MathX.SmoothStep(_snowSlopeMin, _snowSlopeMax, slope);
        var above = MathX.SmoothStep(_snowLine - _snowLineFade, _snowLine + _snowLineFade, height);
        var steep = MathX.SmoothStep(_rockSlopeMin, _rockSlopeMax, 1.0f - slope);

        var snow = flat * above;
        var ice = (1.0f - steep) * (1.0f - above) * flat;

        return new SurfaceMaterialScores(snow, ice, 1.0f - MathF.Max(snow, ice));
    }

    /// <summary>
    ///     The id for one sample.
    ///     <paramref name="slope" /> is the surface normal's y component and <paramref name="height" /> is the sample's height in voxels.
    /// </summary>
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
