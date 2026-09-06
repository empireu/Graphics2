using System.Numerics;
using Graphics2.Core.Render;
// ReSharper disable ForCanBeConvertedToForeach

namespace Graphics2.Renderer.Text;

public struct TextQuad
{
    public Vector2 Offset;
    public Vector2 Size;
    public Vector4 UvCoords;
}

public static class TextLayout
{
    public static Vector2 Layout(
        out int quadCount,
        Span<TextQuad> quads,
        Frame? frame,
        GlyphAtlas atlas,
        ReadOnlySpan<char> text,
        Vector2 origin,
        float maxWidth = 0.0f)
    {
        return LayoutInternal(out quadCount, quads, frame, atlas, text, origin, maxWidth);
    }

    public static Vector2 Measure(
        GlyphAtlas atlas,
        ReadOnlySpan<char> text,
        float maxWidth = 0.0f)
    {
        return LayoutInternal(out _, default, null, atlas, text, Vector2.Zero, maxWidth);
    }

    private static Vector2 LayoutInternal(
        out int quadCount,
        Span<TextQuad> quads,
        Frame? frame,
        GlyphAtlas atlas,
        ReadOnlySpan<char> text,
        Vector2 origin,
        float maxWidth)
    {
        var font = atlas.Font;
        var metrics = font.Metrics;
        var count = 0;
        var hasFrame = frame != null;

        var penX = origin.X;
        var penY = origin.Y;
        var maxPenX = origin.X;

        var prevGlyphIndex = -1;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                maxPenX = Math.Max(maxPenX, penX);
                penX = origin.X;
                penY += metrics.LineHeight;
                prevGlyphIndex = -1;
                continue;
            }

            if (c == '\n')
            {
                maxPenX = Math.Max(maxPenX, penX);
                penX = origin.X;
                penY += metrics.LineHeight;
                prevGlyphIndex = -1;
                continue;
            }

            var glyphIndex = font.GetGlyphIndex(c);

            if (prevGlyphIndex >= 0)
            {
                penX += font.GetKerning(prevGlyphIndex, glyphIndex);
            }

            GlyphAtlasEntry entry;
            GlyphMetrics gm;

            if (hasFrame)
            {
                entry = atlas.GetGlyph(frame!, glyphIndex);
                gm = entry.Metrics;
            }
            else
            {
                if (atlas.TryGetGlyph(glyphIndex, out entry))
                {
                    gm = entry.Metrics;
                }
                else
                {
                    gm = font.RasterizeGlyph(glyphIndex).Metrics;
                }
            }

            if (maxWidth > 0.0f && penX + gm.AdvanceWidth - origin.X > maxWidth)
            {
                maxPenX = Math.Max(maxPenX, penX);
                penX = origin.X;
                penY += metrics.LineHeight;

                if (hasFrame)
                {
                    entry = atlas.GetGlyph(frame!, glyphIndex);
                    gm = entry.Metrics;
                }
            }

            if (gm.BitmapWidth > 0 && gm.BitmapHeight > 0 && hasFrame)
            {
                quads[count] = new TextQuad
                {
                    Offset = new Vector2(penX + gm.XOffset, penY + gm.YOffset),
                    Size = new Vector2(gm.BitmapWidth, gm.BitmapHeight),
                    UvCoords = new Vector4(entry.U0, entry.V0, entry.U1, entry.V1)
                };
                count++;
            }

            penX += gm.AdvanceWidth;
            prevGlyphIndex = glyphIndex;
        }

        maxPenX = Math.Max(maxPenX, penX);

        quadCount = count;
        return new Vector2(maxPenX - origin.X, penY + metrics.LineHeight - origin.Y);
    }
}