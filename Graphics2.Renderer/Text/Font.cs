using System.Runtime.InteropServices;
using Graphics2.Core;
using StbTrueTypeSharp;

namespace Graphics2.Renderer.Text;

public sealed unsafe class Font : IDisposable
{
    private readonly StbTrueType.stbtt_fontinfo _info;
    private readonly float _scale;
    private readonly float _sdfPixelDistScale;
    private int _disposed;

    public FontMetrics Metrics { get; }
    public float PixelSize { get; }
    public RasterizationMode Mode { get; }
    public int SdfSpread { get; }
    
    public Font(byte[] data, float pixelSize, RasterizationMode mode, int sdfSpread = 8)
    {
        if (data == null || data.Length == 0)
        {
            throw new ArgumentException("Font data is null or empty.", nameof(data));
        }

        if (pixelSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelSize), "Pixel size must be positive.");
        }

        if (mode == RasterizationMode.Sdf && sdfSpread <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sdfSpread), "SDF spread must be positive.");
        }

        _info = StbTrueType.CreateFont(data, 0) ?? throw new ArgumentException("Failed to parse font data.", nameof(data));

        _scale = StbTrueType.stbtt_ScaleForPixelHeight(_info, pixelSize);
        Mode = mode;
        SdfSpread = sdfSpread;
        _sdfPixelDistScale = 128.0f / sdfSpread;
        PixelSize = pixelSize;

        int ascent, descent, lineGap;
        StbTrueType.stbtt_GetFontVMetrics(_info, &ascent, &descent, &lineGap);

        Metrics = new FontMetrics(
            ascent * _scale,
            descent * _scale,
            lineGap * _scale
        );
    }

    /// <summary>
    ///     Maps a Unicode codepoint to the font's internal glyph index.
    ///     All metric and rasterization methods take glyph indices, not codepoints.
    ///     Call this once per character, then use the returned index for <see cref="RasterizeGlyph"/> and <see cref="GetKerning"/>.
    /// </summary>
    public int GetGlyphIndex(int codepoint)
    {
        return StbTrueType.stbtt_FindGlyphIndex(_info, codepoint);
    }

    /// <summary>
    ///     Returns the horizontal kerning adjustment between two glyphs, in pixels.
    ///     Kerning tightens specific glyph pairs (AV, To, etc.) by shifting the second glyph left (positive) or right (negative).
    ///     Add this to the pen advance before placing the second glyph.
    ///     Returns 0 for pairs with no kerning data.
    /// </summary>
    public float GetKerning(int leftGlyphIndex, int rightGlyphIndex)
    {
        return StbTrueType.stbtt_GetGlyphKernAdvance(_info, leftGlyphIndex, rightGlyphIndex) * _scale;
    }

    public RasterizedGlyph RasterizeGlyph(int glyphIndex)
    {
        int width, height, xoff, yoff;
        byte* bitmap;

        if (Mode == RasterizationMode.Sdf)
        {
            bitmap = StbTrueType.stbtt_GetGlyphSDF(
                _info,
                _scale,
                glyphIndex,
                SdfSpread,
                128,
                _sdfPixelDistScale,
                &width,
                &height,
                &xoff, 
                &yoff
             );
        }
        else
        {
            bitmap = StbTrueType.stbtt_GetGlyphBitmap(
                _info,
                _scale,
                _scale,
                glyphIndex,
                &width,
                &height,
                &xoff,
                &yoff
            );
        }

        if (bitmap == null)
        {
            int advance, lsb;
            StbTrueType.stbtt_GetGlyphHMetrics(
                _info,
                glyphIndex,
                &advance,
                &lsb
            );

            return new RasterizedGlyph(
                [],
                new GlyphMetrics(
                    advance * _scale,
                    lsb * _scale,
                    0, 
                    0,
                    0,
                    0
                )
            );
        }

        var length = width * height;
        var pixels = new byte[length];
        Marshal.Copy((IntPtr)bitmap, pixels, 0, length);

        if (Mode == RasterizationMode.Sdf)
        {
            StbTrueType.stbtt_FreeSDF(bitmap, null);
        }
        else
        {
            StbTrueType.stbtt_FreeBitmap(bitmap, null);
        }

        int adv, lsb2;
        StbTrueType.stbtt_GetGlyphHMetrics(_info, glyphIndex, &adv, &lsb2);

        var metrics = new GlyphMetrics(
            adv * _scale,
            lsb2 * _scale,
            width,
            height,
            xoff,
            yoff
        );

        return new RasterizedGlyph(pixels, metrics);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _info.Dispose();
    }

    public static Font CreateDefault(float pixelSize, RasterizationMode mode, int sdfSpread = 8)
    {
        return new Font(Helpers.GetResourceBytes(typeof(Font).Assembly, "Graphics2.Renderer.Fonts.Inter.ttf"), pixelSize, mode, sdfSpread);
    }
}
