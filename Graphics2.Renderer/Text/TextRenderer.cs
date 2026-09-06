using System.Numerics;
using System.Runtime.InteropServices;
using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;

namespace Graphics2.Renderer.Text;

[StructLayout(LayoutKind.Sequential)]
public struct TextQuadSsbo
{
    public Vector4 Position;
    public Vector4 Right;
    public Vector4 Up;
    public Vector2 Size;
    public Vector2 UvMin;
    public Vector2 UvMax;
    private Vector2 _pad;
    public Vector4 Color;
}

public sealed class TextRendererDescription
{
    public required TextureFormat ColorFormat { get; init; }
    public TextureFormat? DepthFormat { get; init; }
    public float SdfWeight { get; init; } = 0.5f;
    public float SdfSmoothing { get; init; } = 0.1f;
    public bool DepthTest { get; init; }
    public bool DepthWrite { get; init; }
}

public sealed unsafe class TextRenderer : IDisposable
{
    private static readonly string ShaderSource = Helpers.GetResourceText(typeof(TextRenderer).Assembly, "Graphics2.Renderer.Shaders.Text.slang");

    private readonly GlyphAtlas _atlas;
    private readonly VkSampler _sampler;
    private readonly Pipeline? _normalPipeline;
    private readonly Pipeline? _sdfPipeline;
    private readonly List<TextQuadSsbo> _quads = [];
    private readonly float _sdfWeight;
    private readonly float _sdfSmoothing;
    private Matrix4x4 _viewProj;
    private int _disposed;

    private TextQuad[] _buffer = new TextQuad[1024];

    public TextRenderer(
        VulkanResourceFactory factory,
        GlyphAtlas atlas,
        VkSampler sampler,
        TextRendererDescription description)
    {
        _atlas = atlas;
        _sampler = sampler;
        _sdfWeight = description.SdfWeight;
        _sdfSmoothing = description.SdfSmoothing;

        var depthAttachment = description.DepthFormat ?? TextureFormat.D32Sfloat;

        if (_atlas.Font.Mode == RasterizationMode.Sdf)
        {
            _sdfPipeline = factory.CreatePipeline(new PipelineDescription
            {
                Source = ShaderSource,
                VertexEntryPoint = "vsSdf",
                FragmentEntryPoint = "fsSdf",
                ColorAttachments = [description.ColorFormat],
                DepthAttachment = depthAttachment,
                DepthTest = description.DepthTest,
                DepthWrite = description.DepthWrite,
                CullMode = CullMode.None,
                Blends = [BlendState.AlphaBlend],
                SsboLayouts = new Dictionary<string, SsboLayout>
                {
                    ["quads"] = SsboLayout.FromStruct<TextQuadSsbo>()
                }
            });
        }
        else
        {
            _normalPipeline = factory.CreatePipeline(new PipelineDescription
            {
                Source = ShaderSource,
                VertexEntryPoint = "vsNormal",
                FragmentEntryPoint = "fsNormal",
                ColorAttachments = [description.ColorFormat],
                DepthAttachment = depthAttachment,
                DepthTest = description.DepthTest,
                DepthWrite = description.DepthWrite,
                CullMode = CullMode.None,
                Blends = [BlendState.AlphaBlend],
                SsboLayouts = new Dictionary<string, SsboLayout>
                {
                    ["quads"] = SsboLayout.FromStruct<TextQuadSsbo>()
                }
            });
        }
    }

    public void SetCamera(Matrix4x4 viewProj)
    {
        _viewProj = viewProj;
    }
    
    private void EnsureCapacity(int chars)
    {
        if (chars > _buffer.Length)
        {
            Array.Resize(ref _buffer, (int)BitOperations.RoundUpToPowerOf2((uint)chars));
        }
    }

    public void DrawString2D(
        Frame frame,
        ReadOnlySpan<char> text,
        Vector2 screenPos,
        Vector4 color,
        float maxWidth = 0.0f,
        TextAnchor anchor = TextAnchor.TopLeft)
    {
        EnsureCapacity(text.Length);

        var measure = TextLayout.Measure(_atlas, text, maxWidth);
        var anchorOffset = anchor.Offset(measure, _atlas.Font.Metrics.Ascent);

        TextLayout.Layout(
            out var quadCount,
            _buffer,
            frame,
            _atlas,
            text,
            screenPos + anchorOffset,
            maxWidth
        );

        for (var i = 0; i < quadCount; i++)
        {
            var q = _buffer[i];
            _quads.Add(new TextQuadSsbo
            {
                Position = new Vector4(q.Offset, 0.0f, _sdfWeight),
                Right = new Vector4(1.0f, 0.0f, 0.0f, _sdfSmoothing),
                Up = new Vector4(0.0f, 1.0f, 0.0f, 0.0f),
                Size = q.Size,
                UvMin = new Vector2(q.UvCoords.X, q.UvCoords.Y),
                UvMax = new Vector2(q.UvCoords.Z, q.UvCoords.W),
                Color = color
            });
        }
    }

    public void DrawString3DOriented(
        Frame frame,
        ReadOnlySpan<char> text,
        Vector3 worldPos,
        Vector3 right,
        Vector3 up,
        float worldHeight,
        Vector4 color,
        float maxWidth = 0.0f,
        TextAnchor anchor = TextAnchor.TopLeft)
    {
        EnsureCapacity(text.Length);

        var lineHeight = _atlas.Font.Metrics.LineHeight;
        var pixelScale = worldHeight / lineHeight;

        var measure = TextLayout.Measure(_atlas, text, maxWidth);
        var anchorOffset = anchor.Offset(measure, _atlas.Font.Metrics.Ascent);

        TextLayout.Layout(
            out var quadCount,
            _buffer,
            frame,
            _atlas,
            text,
            anchorOffset,
            maxWidth
        );

        for (var i = 0; i < quadCount; i++)
        {
            var q = _buffer[i];
            var pos = worldPos + (right * (q.Offset.X * pixelScale)) - (up * (q.Offset.Y * pixelScale));
            _quads.Add(new TextQuadSsbo
            {
                Position = new Vector4(pos, _sdfWeight),
                Right = new Vector4(right * pixelScale, _sdfSmoothing),
                Up = new Vector4(-up * pixelScale, 0.0f),
                Size = q.Size,
                UvMin = new Vector2(q.UvCoords.X, q.UvCoords.Y),
                UvMax = new Vector2(q.UvCoords.Z, q.UvCoords.W),
                Color = color
            });
        }
    }

    public void DrawString3DBillboard(
        Frame frame,
        ReadOnlySpan<char> text,
        Vector3 worldPos,
        Vector3 cameraPos,
        Vector3 cameraForward,
        float fov,
        float viewportHeight,
        float screenHeight,
        Vector4 color,
        float maxWidth = 0.0f,
        Vector3? worldUp = null,
        TextAnchor anchor = TextAnchor.TopLeft)
    {
        var distance = Vector3.Distance(worldPos, cameraPos);
        var worldHeight = screenHeight * 2.0f * MathF.Tan(fov * 0.5f) * distance / viewportHeight;

        var upRef = worldUp ?? Vector3.UnitY;
        var forward = Vector3.Normalize(cameraForward);
        var right = Vector3.Normalize(Vector3.Cross(forward, upRef));
        var up = Vector3.Normalize(Vector3.Cross(right, forward));

        DrawString3DOriented(frame, text, worldPos, right, up, worldHeight, color, maxWidth, anchor);
    }

    public void Flush(Frame frame, RenderPassEncoder pass)
    {
        if (_quads.Count == 0)
        {
            return;
        }

        var pipeline = _atlas.Font.Mode == RasterizationMode.Sdf
            ? _sdfPipeline!
            : _normalPipeline!;

        pass.SetPipeline(pipeline);
        pass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);
        pass.SetScissor(0, 0, (uint)frame.Extent.X, (uint)frame.Extent.Y);

        var uboSize = (uint)sizeof(Matrix4x4);
        var uboAlloc = frame.AllocateUniform(uboSize);
        MemoryMarshal.Write(uboAlloc.Data, in _viewProj);
        pass.BindUniformBuffer("Camera", uboAlloc);

        var ssboSize = (uint)(_quads.Count * sizeof(TextQuadSsbo));
        var ssboAlloc = frame.Allocate(ssboSize, 4);

        var quadsSpan = CollectionsMarshal.AsSpan(_quads);
        MemoryMarshal.AsBytes(quadsSpan).CopyTo(ssboAlloc.Data);

        pass.BindStorageBuffer("quads", ssboAlloc);
        pass.BindTexture("atlas", _atlas.Texture);
        pass.BindSampler("atlasSampler", _sampler);

        pass.Draw((uint)(_quads.Count * 6));

        _quads.Clear();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _normalPipeline?.Dispose();
        _sdfPipeline?.Dispose();
    }
}