using System.Numerics;
using System.Runtime.InteropServices;
using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.Renderer.Quads;

namespace Graphics2.Renderer.Lines;

[StructLayout(LayoutKind.Sequential)]
public struct Line3DSsbo
{
    public Vector4 Start;
    public Vector4 End;
    public Vector4 StartColor;
    public Vector4 EndColor;
}

[StructLayout(LayoutKind.Sequential)]
internal struct LineCameraData
{
    public Matrix4x4 ViewProj;
    public Vector3 CameraPos;
    private float _pad;
}

public sealed unsafe class LineRenderer3D : IDisposable
{
    private static readonly string ShaderSource = Helpers.GetResourceText(typeof(LineRenderer3D).Assembly, "Graphics2.Renderer.Shaders.Line3D.slang");

    private readonly Pipeline _pipeline;
    private readonly List<Line3DSsbo> _lines = [];
    private LineCameraData _camera;
    private int _disposed;

    public LineRenderer3D(VulkanResourceFactory factory, PrimitiveRendererDescription description)
    {
        var depthAttachment = description.DepthFormat ?? TextureFormat.D32Sfloat;

        _pipeline = factory.CreatePipeline(new PipelineDescription
        {
            Source = ShaderSource,
            VertexEntryPoint = "vsLine3D",
            FragmentEntryPoint = "fsLine3D",
            ColorAttachments = [description.ColorFormat],
            DepthAttachment = depthAttachment,
            DepthTest = description.DepthTest,
            DepthWrite = description.DepthWrite,
            CullMode = CullMode.None,
            Blends = description.Blends,
            SsboLayouts = new Dictionary<string, SsboLayout>
            {
                ["lines"] = SsboLayout.FromStruct<Line3DSsbo>()
            }
        });
    }

    public void SetCamera(Matrix4x4 viewProj, Vector3 cameraPos)
    {
        _camera = new LineCameraData
        {
            ViewProj = viewProj,
            CameraPos = cameraPos
        };
    }

    public void DrawLine3D(Vector3 start, Vector3 end, float radius, Vector4 color)
    {
        DrawLine3D(start, end, radius, color, color);
    }

    public void DrawLine3D(Vector3 start, Vector3 end, float radius, Vector4 startColor, Vector4 endColor)
    {
        _lines.Add(new Line3DSsbo
        {
            Start = new Vector4(start, radius),
            End = new Vector4(end, 0.0f),
            StartColor = startColor,
            EndColor = endColor
        });
    }

    public void Submit(Frame frame, RenderPassEncoder pass)
    {
        if (_lines.Count == 0)
        {
            return;
        }

        pass.SetPipeline(_pipeline);
        pass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);
        pass.SetScissor(0, 0, (uint)frame.Extent.X, (uint)frame.Extent.Y);

        var uboSize = (uint)sizeof(LineCameraData);
        var uboAlloc = frame.AllocateUniform(uboSize);
        MemoryMarshal.Write(uboAlloc.Data, in _camera);
        pass.BindUniformBuffer("Camera", uboAlloc);

        var ssboSize = (uint)(_lines.Count * sizeof(Line3DSsbo));
        var ssboAlloc = frame.Allocate(ssboSize, 4);
        var linesSpan = CollectionsMarshal.AsSpan(_lines);
        MemoryMarshal.AsBytes(linesSpan).CopyTo(ssboAlloc.Data);
        pass.BindStorageBuffer("lines", ssboAlloc);

        pass.Draw((uint)(_lines.Count * 6));

        _lines.Clear();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _pipeline.Dispose();
    }
}
