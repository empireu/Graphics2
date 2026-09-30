namespace WinterGame.World;

/// <summary>
///     A loader's claim on the sections around it, handed out by <see cref="Level.AddTicket"/>.
///     The shape is a vertical cylinder in section units: a horizontal disc of <see cref="HorizontalRadius"/> around <see cref="Center"/>, and <see cref="VerticalRadius"/> sections above and below it.
///     Sections inside the shape are loaded nearest first, and the shape grown by <see cref="Level.KeepMargin"/> is what keeps them loaded.
/// </summary>
public sealed class LevelTicket
{
    internal LevelTicket(Level owner, SectionPos center, int horizontalRadius, int verticalRadius)
    {
        Owner = owner;
        Center = center;
        HorizontalRadius = horizontalRadius;
        VerticalRadius = verticalRadius;
    }

    /// <summary>
    ///     The level the ticket is active in, cleared when it is removed.
    /// </summary>
    internal Level? Owner { get; set; }

    public SectionPos Center { get; internal set; }

    public int HorizontalRadius { get; }

    public int VerticalRadius { get; }
}
