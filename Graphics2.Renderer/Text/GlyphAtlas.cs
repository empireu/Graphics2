using Graphics2.Core.Render;
using Graphics2.Core.Resources;

namespace Graphics2.Renderer.Text;

public readonly struct GlyphAtlasEntry(
    float u0,
    float v0,
    float u1,
    float v1,
    GlyphMetrics metrics)
{
    public readonly float U0 = u0;
    public readonly float V0 = v0;
    public readonly float U1 = u1;
    public readonly float V1 = v1;
    public readonly GlyphMetrics Metrics = metrics;
}

public sealed class GlyphAtlas : IDisposable
{
    private const int Padding = 1;

    private readonly Texture _texture;
    private readonly byte[] _cpuBuffer;
    private readonly Dictionary<int, GlyphAtlasEntry> _map = [];
    private int _cursorX;
    private int _cursorY;
    private int _rowHeight;
    private int _disposed;

    public readonly Font Font;
    public readonly int Width;
    public readonly int Height;

    public VkTexture Texture => _texture;

    public GlyphAtlas(Font font, VulkanResourceFactory factory, int width = 2048, int height = 2048)
    {
        Font = font;
        Width = width;
        Height = height;

        _cpuBuffer = new byte[width * height];

        _texture = factory.CreateTexture(new TextureDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = TextureFormat.R8Unorm,
            Usage = TextureUsage.Sampled | TextureUsage.TransferDestination
        });
    }

    public GlyphAtlasEntry GetGlyph(Frame frame, int glyphIndex)
    {
        if (_map.TryGetValue(glyphIndex, out var entry))
        {
            return entry;
        }

        var rasterized = Font.RasterizeGlyph(glyphIndex);
        var metrics = rasterized.Metrics;

        if (metrics.BitmapWidth == 0 || metrics.BitmapHeight == 0)
        {
            entry = new GlyphAtlasEntry(0, 0, 0, 0, metrics);
            _map[glyphIndex] = entry;
            return entry;
        }

        var w = metrics.BitmapWidth;
        var h = metrics.BitmapHeight;

        if (_cursorX + w + Padding > Width)
        {
            _cursorX = 0;
            _cursorY += _rowHeight + Padding;
            _rowHeight = 0;
        }

        if (_cursorY + h + Padding > Height)
        {
            throw new InvalidOperationException(
                $"Glyph atlas ({Width}x{Height}) is full. Cannot fit glyph {glyphIndex} ({w}x{h}).");
        }

        var x = _cursorX;
        var y = _cursorY;

        BlitGlyph(rasterized.Pixels, x, y, w, h);

        var alloc = frame.AllocateStorage((uint)(w * h));
        rasterized.Pixels.AsSpan().CopyTo(alloc.Data);
        frame.CopyBufferToImage(alloc, _texture, (uint)x, (uint)y, (uint)w, (uint)h);

        if (h > _rowHeight)
        {
            _rowHeight = h;
        }

        _cursorX += w + Padding;

        var u0 = (float)x / Width;
        var v0 = (float)y / Height;
        var u1 = (float)(x + w) / Width;
        var v1 = (float)(y + h) / Height;

        entry = new GlyphAtlasEntry(u0, v0, u1, v1, metrics);
        _map[glyphIndex] = entry;

        return entry;
    }

    public bool TryGetGlyph(int glyphIndex, out GlyphAtlasEntry entry)
    {
        return _map.TryGetValue(glyphIndex, out entry);
    }

    public void Prepopulate(Frame frame, ReadOnlySpan<int> codepoints)
    {
        foreach (var codepoint in codepoints)
        {
            var glyphIndex = Font.GetGlyphIndex(codepoint);

            GetGlyph(frame, glyphIndex);
        }
    }

    public void PrepopulateCommons(Frame frame)
    {
        // ReSharper disable once StringLiteralTypo
        const string charset = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,;:?!-()";
        var codepoints = new int[charset.Length];
        for (var i = 0; i < charset.Length; i++)
        {
            codepoints[i] = charset[i];
        }

        Prepopulate(frame, codepoints);
    }

    private void BlitGlyph(byte[] src, int dstX, int dstY, int w, int h)
    {
        for (var row = 0; row < h; row++)
        {
            var srcOffset = row * w;
            var dstOffset = (dstY + row) * Width + dstX;
            Array.Copy(src, srcOffset, _cpuBuffer, dstOffset, w);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _texture.Dispose();
    }
}