using WinterGame.World.Storage;

namespace WinterGame.World.Level.Residency;

/// <summary>
///     A loader's claim on the surrounding sections, handed out by <see cref="WorldLevel.AddTicket"/>.
///     The shape is a vertical cylinder in section units: a horizontal disc of <see cref="HorizontalRadius"/> around <see cref="Center"/>, and <see cref="VerticalRadius"/> sections above and below it.
///     Sections inside the shape are loaded nearest first, and the shape grown by <see cref="WorldLevel.KeepMargin"/> is what keeps them loaded.
/// </summary>
public sealed class LevelTicket
{
    /// <summary>
    ///     The shape whose sections are loaded.
    /// </summary>
    internal readonly TicketShape LoadShape;

    /// <summary>
    ///     The load shape grown by <see cref="WorldLevel.KeepMargin"/>, whose sections stay loaded once they are.
    /// </summary>
    internal readonly TicketShape KeepShape;

    internal LevelTicket(WorldLevel owner, SectionPos center, int horizontalRadius, int verticalRadius)
    {
        Owner = owner;
        Center = center;
        HorizontalRadius = horizontalRadius;
        VerticalRadius = verticalRadius;
        LoadShape = new TicketShape(horizontalRadius, verticalRadius);
        KeepShape = new TicketShape(horizontalRadius + WorldLevel.KeepMargin, verticalRadius + WorldLevel.KeepMargin);
    }

    /// <summary>
    ///     The level the ticket is active in, cleared when it is removed.
    /// </summary>
    internal WorldLevel? Owner { get; set; }

    /// <summary>
    ///     Set when a load queue rebuild found every section of the load shape loaded or generating, so later rebuilds skip the ticket until it moves.
    ///     It stays true while the ticket stands still, since a section the ticket holds is never unloaded and its generation always loads it.
    /// </summary>
    internal bool IsSatisfied { get; set; }

    public SectionPos Center { get; internal set; }

    public int HorizontalRadius { get; }

    public int VerticalRadius { get; }
}
