using System.Numerics;
using System.Runtime.InteropServices;
using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;

namespace Graphics2.Renderer.Quads;

[StructLayout(LayoutKind.Sequential)]
public struct SolidQuad3DSsbo
{
    public Vector4 Position;
    public Vector4 Right;
    public Vector4 Up;
    public Vector2 Size;
    private Vector2 _pad;
    public Vector4 Color;
}

[StructLayout(LayoutKind.Sequential)]
public struct TexturedQuad3DSsbo
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

public sealed unsafe class QuadRenderer3D : IDisposable
{
    private static readonly string ShaderSource = Helpers.GetResourceText(typeof(QuadRenderer3D).Assembly, "Graphics2.Renderer.Shaders.Quad3D.slang");

    private readonly TextureArray? _textures;
    private readonly VkSampler? _sampler;
    private readonly Pipeline _solidPipeline;
    private readonly Pipeline? _texturedPipeline;

    private readonly List<SolidQuad3DSsbo> _solidQuads = [];
    private readonly List<TexturedQuad3DSsbo> _texturedQuads = [];

    private Matrix4x4 _viewProj;
    private int _disposed;

    public QuadRenderer3D(VulkanResourceFactory factory, TextureArray? textures, VkSampler? sampler, PrimitiveRendererDescription description)
    {
        _textures = textures;
        _sampler = sampler;

        var depthAttachment = description.DepthFormat ?? TextureFormat.D32Sfloat;

        _solidPipeline = factory.CreatePipeline(new PipelineDescription
        {
            Source = ShaderSource,
            VertexEntryPoint = "vsSolid3D",
            FragmentEntryPoint = "fsSolid3D",
            ColorAttachments = [description.ColorFormat],
            DepthAttachment = depthAttachment,
            DepthTest = description.DepthTest,
            DepthWrite = description.DepthWrite,
            CullMode = CullMode.None,
            Blends = description.Blends,
            SsboLayouts = new Dictionary<string, SsboLayout>
            {
                ["solidQuads"] = SsboLayout.FromStruct<SolidQuad3DSsbo>()
            }
        });

        if (textures != null && sampler != null)
        {
            _texturedPipeline = factory.CreatePipeline(new PipelineDescription
            {
                Source = ShaderSource,
                VertexEntryPoint = "vsTextured3D",
                FragmentEntryPoint = "fsTextured3D",
                ColorAttachments = [description.ColorFormat],
                DepthAttachment = depthAttachment,
                DepthTest = description.DepthTest,
                DepthWrite = description.DepthWrite,
                CullMode = CullMode.None,
                Blends = description.Blends,
                SsboLayouts = new Dictionary<string, SsboLayout>
                {
                    ["texturedQuads"] = SsboLayout.FromStruct<TexturedQuad3DSsbo>()
                }
            });
        }
    }

    public void SetCamera(Matrix4x4 viewProj)
    {
        _viewProj = viewProj;
    }

    public void DrawSolid3D(Vector3 position, Vector3 right, Vector3 up, Vector2 size, Vector4 color)
    {
        _solidQuads.Add(new SolidQuad3DSsbo
        {
            Position = new Vector4(position, 0.0f),
            Right = new Vector4(right, 0.0f),
            Up = new Vector4(up, 0.0f),
            Size = size,
            Color = color
        });
    }

    public void DrawTextured3D(Vector3 position, Vector3 right, Vector3 up, Vector2 size, uint textureIndex, Vector2 uvMin, Vector2 uvMax, Vector4 color)
    {
        if (_texturedPipeline == null)
        {
            throw new InvalidOperationException("QuadRenderer3D was created without a TextureArray and sampler.");
        }

        _texturedQuads.Add(new TexturedQuad3DSsbo
        {
            Position = new Vector4(position, textureIndex),
            Right = new Vector4(right, 0.0f),
            Up = new Vector4(up, 0.0f),
            Size = size,
            UvMin = uvMin,
            UvMax = uvMax,
            Color = color
        });
    }

    public void DrawBillboard3D(Vector3 position, Vector3 cameraForward, Vector2 size, Vector4 color, Vector3? worldUp = null)
    {
        var upRef = worldUp ?? Vector3.UnitY;
        var forward = Vector3.Normalize(cameraForward);
        var right = Vector3.Normalize(Vector3.Cross(forward, upRef));
        var up = Vector3.Normalize(Vector3.Cross(right, forward));
        DrawSolid3D(position, right, up, size, color);
    }

    public void DrawTexturedBillboard3D(Vector3 position, Vector3 cameraForward, Vector2 size, uint textureIndex, Vector2 uvMin, Vector2 uvMax, Vector4 color, Vector3? worldUp = null)
    {
        if (_texturedPipeline == null)
        {
            throw new InvalidOperationException("QuadRenderer3D was created without a TextureArray and sampler.");
        }

        var upRef = worldUp ?? Vector3.UnitY;
        var forward = Vector3.Normalize(cameraForward);
        var right = Vector3.Normalize(Vector3.Cross(forward, upRef));
        var up = Vector3.Normalize(Vector3.Cross(right, forward));
        DrawTextured3D(position, right, up, size, textureIndex, uvMin, uvMax, color);
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

        var ssboSize = (uint)(_texturedQuads.Count * sizeof(TexturedQuad3DSsbo));
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
    }
}
