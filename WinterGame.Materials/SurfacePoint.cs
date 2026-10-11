namespace WinterGame.Materials;

/// <summary>
///     A material's surface at one point: the height in metres and the albedo in linear light.
/// </summary>
public readonly struct SurfacePoint(float height, float red, float green, float blue)
{
    public readonly float Height = height;
    public readonly float Red = red;
    public readonly float Green = green;
    public readonly float Blue = blue;
}
