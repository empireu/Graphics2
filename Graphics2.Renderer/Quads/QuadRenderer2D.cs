using System.Numerics;
using System.Runtime.InteropServices;
using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;

namespace Graphics2.Renderer.Quads;

[StructLayout(LayoutKind.Sequential)]
public struct SolidQuad2DSsbo
{
    public Vector4 Position;
    public Vector4 Color;
    public Vector2 Size;
    private Vector2 _pad;
}

[StructLayout(LayoutKind.Sequential)]
public struct TexturedQuad2DSsbo
{
    public Vector4 Position;
    public Vector4 Color;
    public Vector2 Size;
    public Vector2 UvMin;
    public Vector2 UvMax;
    private Vector2 _pad;
}

[StructLayout(LayoutKind.Sequential)]
public struct CircleQuad2DSsbo
{
    public Vector4 Position;
    public Vector4 Color;
}

[StructLayout(LayoutKind.Sequential)]
public struct GradientQuad2DSsbo
{
    public Vector4 Position;
    public Vector4 Color0;
    public Vector4 Color1;
    public Vector4 Color2;
    public Vector4 Color3;
    public Vector2 Size;
    private Vector2 _pad;
}

public sealed unsafe class QuadRenderer2D : IDisposable
{
    private static readonly string ShaderSource = Helpers.GetResourceText(typeof(QuadRenderer2D).Assembly, "Graphics2.Renderer.Shaders.Quad2D.slang");

    private readonly TextureArray? _textures;
    private readonly VkSampler? _sampler;
    private readonly Pipeline _solidPipeline;
    private readonly Pipeline? _texturedPipeline;
    private readonly Pipeline _circlePipeline;
    private readonly Pipeline _gradientPipeline;

    private readonly List<SolidQuad2DSsbo> _solidQuads = [];
    private readonly List<TexturedQuad2DSsbo> _texturedQuads = [];
    private readonly List<CircleQuad2DSsbo> _circleQuads = [];
    private readonly List<GradientQuad2DSsbo> _gradientQuads = [];

    private Matrix4x4 _viewProj;
    private int _disposed;

    public QuadRenderer2D(
        VulkanResourceFactory factory,
        TextureArray? textures,
        VkSampler? sampler,
        PrimitiveRendererDescription description)
    {
        _textures = textures;
        _sampler = sampler;

        var depthAttachment = description.DepthFormat ?? TextureFormat.D32Sfloat;

        _solidPipeline = factory.CreatePipeline(new PipelineDescription
        {
            Source = ShaderSource,
            VertexEntryPoint = "vsSolid2D",
            FragmentEntryPoint = "fsSolid2D",
            ColorAttachments = [description.ColorFormat],
            DepthAttachment = depthAttachment,
            DepthTest = description.DepthTest,
            DepthWrite = description.DepthWrite,
            CullMode = CullMode.None,
            Blends = description.Blends,
            SsboLayouts = new Dictionary<string, SsboLayout>
            {
                ["solidQuads"] = SsboLayout.FromStruct<SolidQuad2DSsbo>()
            }
        });

        if (textures != null && sampler != null)
        {
            _texturedPipeline = factory.CreatePipeline(new PipelineDescription
            {
                Source = ShaderSource,
                VertexEntryPoint = "vsTextured2D",
                FragmentEntryPoint = "fsTextured2D",
                ColorAttachments = [description.ColorFormat],
                DepthAttachment = depthAttachment,
                DepthTest = description.DepthTest,
                DepthWrite = description.DepthWrite,
                CullMode = CullMode.None,
                Blends = description.Blends,
                SsboLayouts = new Dictionary<string, SsboLayout>
                {
                    ["texturedQuads"] = SsboLayout.FromStruct<TexturedQuad2DSsbo>()
                }
            });
        }

        _circlePipeline = factory.CreatePipeline(new PipelineDescription
        {
            Source = ShaderSource,
            VertexEntryPoint = "vsCircle2D",
            FragmentEntryPoint = "fsCircle2D",
            ColorAttachments = [description.ColorFormat],
            DepthAttachment = depthAttachment,
            DepthTest = description.DepthTest,
            DepthWrite = description.DepthWrite,
            CullMode = CullMode.None,
            Blends = description.Blends,
            SsboLayouts = new Dictionary<string, SsboLayout>
            {
                ["circleQuads"] = SsboLayout.FromStruct<CircleQuad2DSsbo>()
            }
        });

        _gradientPipeline = factory.CreatePipeline(new PipelineDescription
        {
            Source = ShaderSource,
            VertexEntryPoint = "vsGradient2D",
            FragmentEntryPoint = "fsGradient2D",
            ColorAttachments = [description.ColorFormat],
            DepthAttachment = depthAttachment,
            DepthTest = description.DepthTest,
            DepthWrite = description.DepthWrite,
            CullMode = CullMode.None,
            Blends = description.Blends,
            SsboLayouts = new Dictionary<string, SsboLayout>
            {
                ["gradientQuads"] = SsboLayout.FromStruct<GradientQuad2DSsbo>()
            }
        });
    }

    public void SetCamera(Matrix4x4 viewProj)
    {
        _viewProj = viewProj;
    }

    public void DrawSolid2D(Vector2 position, Vector2 size, float rotation, Vector4 color, QuadAnchor anchor = QuadAnchor.TopLeft)
    {
        var topLeft = position + anchor.Offset2D(size);
        _solidQuads.Add(new SolidQuad2DSsbo
        {
            Position = new Vector4(topLeft, rotation, 0.0f),
            Color = color,
            Size = size
        });
    }

    public void DrawTextured2D(Vector2 position, Vector2 size, float rotation, uint textureIndex, Vector2 uvMin, Vector2 uvMax, Vector4 color, QuadAnchor anchor = QuadAnchor.TopLeft)
    {
        if (_texturedPipeline == null)
        {
            throw new InvalidOperationException("QuadRenderer was created without a TextureArray and sampler.");
        }

        var topLeft = position + anchor.Offset2D(size);
        _texturedQuads.Add(new TexturedQuad2DSsbo
        {
            Position = new Vector4(topLeft, rotation, textureIndex),
            Color = color,
            Size = size,
            UvMin = uvMin,
            UvMax = uvMax
        });
    }

    public void DrawCircle2D(Vector2 position, float radius, Vector4 color, QuadAnchor anchor = QuadAnchor.Center)
    {
        var diameter = new Vector2(radius * 2.0f, radius * 2.0f);
        var center = position + new Vector2(radius, radius) + anchor.Offset2D(diameter);
        _circleQuads.Add(new CircleQuad2DSsbo
        {
            Position = new Vector4(center, radius, 0.0f),
            Color = color
        });
    }

    public void DrawGradient2D(Vector2 position, Vector2 size, float rotation, Vector4 color0, Vector4 color1, Vector4 color2, Vector4 color3, QuadAnchor anchor = QuadAnchor.TopLeft)
    {
        var topLeft = position + anchor.Offset2D(size);
        _gradientQuads.Add(new GradientQuad2DSsbo
        {
            Position = new Vector4(topLeft, rotation, 0.0f),
            Color0 = color0,
            Color1 = color1,
            Color2 = color2,
            Color3 = color3,
            Size = size
        });
    }

    public void Submit(Frame frame, RenderPassEncoder pass)
    {
        if (_solidQuads.Count > 0)
        {
            FlushEffect(frame, pass, _solidPipeline, "solidQuads", _solidQuads);
            _solidQuads.Clear();
        }

        if (_texturedQuads.Count > 0 && _texturedPipeline != null)
        {
            FlushTextured(frame, pass);
            _texturedQuads.Clear();
        }

        if (_circleQuads.Count > 0)
        {
            FlushEffect(frame, pass, _circlePipeline, "circleQuads", _circleQuads);
            _circleQuads.Clear();
        }

        if (_gradientQuads.Count > 0)
        {
            FlushEffect(frame, pass, _gradientPipeline, "gradientQuads", _gradientQuads);
            _gradientQuads.Clear();
        }
    }

    private void FlushEffect<T>(Frame frame, RenderPassEncoder pass, Pipeline pipeline, string ssboName, List<T> quads) where T : unmanaged
    {
        pass.SetPipeline(pipeline);
        pass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);
        pass.SetScissor(0, 0, (uint)frame.Extent.X, (uint)frame.Extent.Y);
        BindCamera(frame, pass);

        var ssboSize = (uint)(quads.Count * sizeof(T));
        var ssboAlloc = frame.AllocateStorage(ssboSize);
        var quadsSpan = CollectionsMarshal.AsSpan(quads);
        MemoryMarshal.AsBytes(quadsSpan).CopyTo(ssboAlloc.Data);
        pass.BindStorageBuffer(ssboName, ssboAlloc);

        pass.Draw((uint)(quads.Count * 6));
    }

    private void FlushTextured(Frame frame, RenderPassEncoder pass)
    {
        pass.SetPipeline(_texturedPipeline!);
        pass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);
        pass.SetScissor(0, 0, (uint)frame.Extent.X, (uint)frame.Extent.Y);
        BindCamera(frame, pass);

        var ssboSize = (uint)(_texturedQuads.Count * sizeof(TexturedQuad2DSsbo));
        var ssboAlloc = frame.AllocateStorage(ssboSize);
        var quadsSpan = CollectionsMarshal.AsSpan(_texturedQuads);
        MemoryMarshal.AsBytes(quadsSpan).CopyTo(ssboAlloc.Data);
        pass.BindStorageBuffer("texturedQuads", ssboAlloc);

        pass.BindSampler("quadSampler", _sampler!);
        pass.BindTextureArray("textures", _textures!);

        pass.Draw((uint)(_texturedQuads.Count * 6));
    }

    private void BindCamera(Frame frame, RenderPassEncoder pass)
    {
        var uboSize = (uint)sizeof(Matrix4x4);
        var uboAlloc = frame.AllocateUniform(uboSize);
        MemoryMarshal.Write(uboAlloc.Data, in _viewProj);
        pass.BindUniformBuffer("Camera", uboAlloc);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _solidPipeline.Dispose();
        _texturedPipeline?.Dispose();
        _circlePipeline.Dispose();
        _gradientPipeline.Dispose();
    }
}
