namespace Graphics2.Renderer.Text;

/// <summary>
///     Font-wide vertical metrics, all in pixels at the font's configured pixel size.
///     These describe the vertical layout envelope that all glyphs share.
/// </summary>
public readonly struct FontMetrics(float ascent, float descent, float lineGap)
{
    /// <summary>
    ///     Distance from the baseline to the top of the tallest glyph, in pixels.
    ///     Most glyphs fit below this line. Only accents and tall characters reach it.
    ///     Positive.
    /// </summary>
    public readonly float Ascent = ascent;

    /// <summary>
    ///     Distance from the baseline to the bottom of the lowest glyph, in pixels.
    ///     Descenders (g, p, q, y) extend below the baseline by this amount.
    ///     Negative.
    /// </summary>
    public readonly float Descent = descent;

    /// <summary>
    ///     Recommended extra spacing between consecutive lines, in pixels.
    ///     The font designer's suggested gap between the descent of one line and the ascent of the next.
    ///     Positive.
    /// </summary>
    public readonly float LineGap = lineGap;

    /// <summary>
    ///     Total height of one line: <c>Ascent - Descent + LineGap</c>.
    ///     This is the distance the pen moves vertically when wrapping to the next line.
    /// </summary>
    public float LineHeight => Ascent - Descent + LineGap;
}
