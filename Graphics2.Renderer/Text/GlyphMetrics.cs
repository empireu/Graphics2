namespace Graphics2.Renderer.Text;

/// <summary>
///     Per-glyph metrics for horizontal layout and bitmap placement.
///     Horizontal values are in pixels at the font's configured pixel size.
///     Bitmap dimensions and offsets are in integer pixels.
/// </summary>
public readonly struct GlyphMetrics(
    float advanceWidth,
    float leftSideBearing,
    int bitmapWidth,
    int bitmapHeight,
    int xOffset,
    int yOffset)
{
    /// <summary>
    ///     How far the pen advances horizontally after this glyph, in pixels.
    ///     This is the per-glyph width that makes "iii" narrower than "WWW".
    ///     Each glyph carries its own advance, not a fixed cell width.
    /// </summary>
    public readonly float AdvanceWidth = advanceWidth;

    /// <summary>
    ///     Horizontal offset from the pen position to the left edge of the glyph bitmap, in pixels.
    ///     Usually zero or small positive, but can be negative for glyphs that extend left of the pen (italic slopes, some accents).
    /// </summary>
    public readonly float LeftSideBearing = leftSideBearing;

    /// <summary>
    ///     Width of the rasterized bitmap in pixels.
    ///     May be zero for whitespace or combining marks (no bitmap to draw).
    /// </summary>
    public readonly int BitmapWidth = bitmapWidth;

    /// <summary>
    ///     Height of the rasterized bitmap in pixels.
    ///     May be zero for whitespace or combining marks (no bitmap to draw).
    /// </summary>
    public readonly int BitmapHeight = bitmapHeight;

    /// <summary>
    ///     Horizontal offset from the pen position to the left edge of the glyph bitmap, in integer pixels.
    ///     Combined with <see cref="LeftSideBearing"/> (which is the same value in float), this is the x coordinate of the bitmap's top-left corner relative to the pen.
    /// </summary>
    public readonly int XOffset = xOffset;

    /// <summary>
    ///     Vertical offset from the pen position (baseline) to the top edge of the glyph bitmap, in integer pixels.
    ///     stb_truetype uses a y-down convention. Negative means above the baseline (most glyphs), positive means below (descenders like g, p, y).
    ///     The bitmap's top row sits at <c>baseline + YOffset</c>.
    /// </summary>
    public readonly int YOffset = yOffset;
}
