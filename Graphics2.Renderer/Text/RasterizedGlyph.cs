namespace Graphics2.Renderer.Text;

public readonly struct RasterizedGlyph(byte[] pixels, GlyphMetrics metrics)
{
    public readonly byte[] Pixels = pixels;
    public readonly GlyphMetrics Metrics = metrics;
}
