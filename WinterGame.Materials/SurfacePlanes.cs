namespace WinterGame.Materials;

/// <summary>
///     The planes a recipe works on: the height in metres and the albedo in linear light.
/// </summary>
public sealed class SurfacePlanes
{
    public readonly TileImage Height;
    public readonly TileImage Red;
    public readonly TileImage Green;
    public readonly TileImage Blue;

    public SurfacePlanes(int size)
    {
        Height = new TileImage(size);
        Red = new TileImage(size);
        Green = new TileImage(size);
        Blue = new TileImage(size);
    }

    /// <summary>
    ///     Fills each pixel from <paramref name="surface"/> at the pixel's centre, in tile coordinates.
    ///     The planes tile only if the surface repeats with a period of 1 in both coordinates.
    ///     The rows are filled on the thread pool, so <paramref name="surface"/> is called from several threads at once and must not change any state.
    /// </summary>
    public static SurfacePlanes Evaluate(int size, Func<float, float, SurfacePoint> surface)
    {
        var planes = new SurfacePlanes(size);

        Parallel.For(0, size, y =>
        {
            var v = (y + 0.5f) / size;

            for (var x = 0; x < size; x++)
            {
                var point = surface((x + 0.5f) / size, v);
                var index = y * size + x;

                planes.Height.Pixels[index] = point.Height;
                planes.Red.Pixels[index] = point.Red;
                planes.Green.Pixels[index] = point.Green;
                planes.Blue.Pixels[index] = point.Blue;
            }
        });

        return planes;
    }

    /// <summary>
    ///     Darkens the albedo where the height lies below its blurred self, as crevices hold dirt and moisture, and lightens it where it lies above, as raised edges weather.
    /// </summary>
    /// <param name="radiusMetres">The blur's radius, which sets the width of what counts as a crevice.</param>
    /// <param name="darkening">The fraction lost per metre below the blurred height.</param>
    /// <param name="lightening">The fraction gained per metre above it.</param>
    public void ShadeCavities(float radiusMetres, float darkening, float lightening)
    {
        var radius = Math.Max(1, (int)MathF.Round(radiusMetres * Height.Size / MaterialMaps.TileMetres));
        var blurred = TileOps.Blur(Height, radius);

        for (var index = 0; index < Height.Pixels.Length; index++)
        {
            var cavity = Height.Pixels[index] - blurred.Pixels[index];
            var factor = Math.Clamp(1.0f + (cavity < 0.0f ? darkening : lightening) * cavity, 0.4f, 1.2f);

            Red.Pixels[index] *= factor;
            Green.Pixels[index] *= factor;
            Blue.Pixels[index] *= factor;
        }
    }

    public MaterialMaps Pack()
    {
        return MaterialMaps.Pack(Red, Green, Blue, Height);
    }
}
