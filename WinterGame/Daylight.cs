using System.Numerics;
using Graphics2.Mathematics;

namespace WinterGame;

/// <summary>
///     The sun and the sky colours at a time of day.
///     The sun fades out as it reaches the horizon, and the night is lit by the sky alone.
/// </summary>
internal readonly struct Daylight
{
    /// <summary>
    ///     A high latitude in late winter, so the sun stays low and noon is about 35 degrees up.
    /// </summary>
    private const float Latitude = 50.0f * MathF.PI / 180.0f;

    private const float Declination = -5.0f * MathF.PI / 180.0f;

    private static readonly Vector3 SunColor = new(0.75f, 0.72f, 0.67f);
    private static readonly Vector3 LowSunColor = new(0.75f, 0.42f, 0.22f);

    private static readonly Vector3 DayZenith = new(0.22f, 0.30f, 0.46f);
    private static readonly Vector3 DayHorizon = new(0.38f, 0.44f, 0.54f);
    private static readonly Vector3 DuskZenith = new(0.09f, 0.11f, 0.21f);
    private static readonly Vector3 DuskHorizon = new(0.42f, 0.28f, 0.22f);
    private static readonly Vector3 NightZenith = new(0.010f, 0.012f, 0.025f);
    private static readonly Vector3 NightHorizon = new(0.020f, 0.025f, 0.040f);

    /// <summary>
    ///     Unit vector from the ground towards the sun, below the horizon at night.
    /// </summary>
    public readonly Vector3 SunDirection;

    /// <summary>
    ///     Exactly zero while the sun is at or below the horizon.
    /// </summary>
    public readonly Vector3 SunRadiance;

    public readonly Vector3 SkyZenith;
    public readonly Vector3 SkyHorizon;

    private Daylight(Vector3 sunDirection, Vector3 sunRadiance, Vector3 skyZenith, Vector3 skyHorizon)
    {
        SunDirection = sunDirection;
        SunRadiance = sunRadiance;
        SkyZenith = skyZenith;
        SkyHorizon = skyHorizon;
    }

    public bool SunIsUp => SunRadiance != Vector3.Zero;

    /// <summary>
    ///     The light at <paramref name="timeOfDay"/>, the fraction of a day since midnight.
    /// </summary>
    public static Daylight At(double timeOfDay)
    {
        // The sun turns about the celestial pole, which stands at the latitude above the north horizon, and crosses the meridian in the south at noon:
        var hourAngle = (float)((timeOfDay - 0.5) * 2.0 * Math.PI);
        var pole = new Vector3(0.0f, MathF.Sin(Latitude), MathF.Cos(Latitude));
        var meridian = new Vector3(0.0f, MathF.Cos(Latitude), -MathF.Sin(Latitude));
        var west = Vector3.Cross(pole, meridian);

        var sun = MathF.Cos(Declination) * (MathF.Cos(hourAngle) * meridian + MathF.Sin(hourAngle) * west) + MathF.Sin(Declination) * pole;
        var sunElevation = sun.Y;
        var horizonFade = MathX.CheckedMapSmoothStepNonsingular(0.0f, 0.08f, sunElevation);
        var sunColor = Vector3.Lerp(LowSunColor, SunColor, MathX.CheckedMapSmoothStepNonsingular(0.0f, 0.35f, sunElevation));

        var nightToDusk = MathX.CheckedMapSmoothStepNonsingular(-0.15f, 0.0f, sunElevation);
        var duskToDay = MathX.CheckedMapSmoothStepNonsingular(0.0f, 0.25f, sunElevation);

        return new Daylight(
            sun,
            sunColor * horizonFade,
            Vector3.Lerp(Vector3.Lerp(NightZenith, DuskZenith, nightToDusk), DayZenith, duskToDay),
            Vector3.Lerp(Vector3.Lerp(NightHorizon, DuskHorizon, nightToDusk), DayHorizon, duskToDay)
        );
    }
}
