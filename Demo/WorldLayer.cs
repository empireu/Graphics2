using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.ImGui;
using Graphics2.Renderer.Quads;
using Graphics2.Renderer.Lines;
using Graphics2.Renderer.Text;
using Graphics2.Windowing.Input;
using ImGuiNET;
using Silk.NET.Maths;

namespace Demo;

internal sealed unsafe class WorldLayer : IDisposable
{
    private readonly App _app;
    
    private readonly ImGuiRenderer _imGui;
    private readonly VkSampler _textSampler;
    private readonly GlyphAtlas _glyphAtlas;
    private readonly TextRenderer _textRenderer;
    private bool _textInitialized;

    private Texture? _depthTexture;

    private readonly QuadRenderer2D _quadRenderer2D;
    private readonly QuadRenderer3D _overlayQuadRenderer3D;
    private readonly LineRenderer3D _lineRenderer3D;
    
    private readonly Camera3D _camera = new();

    private readonly Pipeline _pipeline;
    
    public WorldLayer(App app)
    {
        _app = app;
        
        #region Assets and ImGui

        var factory = app.Factory;

        _imGui = new ImGuiRenderer(factory);
        _imGui.Initialize(
            app.G2Wnd.Window.FramebufferSize,
            () => app.G2Wnd.ClipboardText,
            text => app.G2Wnd.ClipboardText = text
        );

        var font = Font.CreateDefault(48.0f, RasterizationMode.Sdf);
       
        _textSampler = factory.CreateSampler(new SamplerDescription
        {
            AnisotropyEnabled = false
        });
        
        _glyphAtlas = new GlyphAtlas(font, factory);
        
        _textRenderer = new TextRenderer(
            factory,
            _glyphAtlas,
            _textSampler,
            new TextRendererDescription
            {
                ColorFormat = TextureFormat.B8G8R8A8Srgb,
                DepthFormat = TextureFormat.D32Sfloat,
                SdfSmoothing = 0.015f,
                DepthTest = false,
                DepthWrite = false
            });

        #endregion

        _quadRenderer2D = new QuadRenderer2D(factory, null, null, new PrimitiveRendererDescription
        {
            ColorFormat = TextureFormat.B8G8R8A8Srgb,
            DepthFormat = TextureFormat.D32Sfloat
        });

        _overlayQuadRenderer3D = new QuadRenderer3D(factory, null, null, new PrimitiveRendererDescription
        {
            ColorFormat = TextureFormat.B8G8R8A8Srgb,
            DepthFormat = TextureFormat.D32Sfloat,
            DepthTest = false,
            DepthWrite = false
        });

        _lineRenderer3D = new LineRenderer3D(factory, new PrimitiveRendererDescription
        {
            ColorFormat = TextureFormat.B8G8R8A8Srgb,
            DepthFormat = TextureFormat.D32Sfloat,
            DepthTest = true,
            DepthWrite = true,
            Blends = [new BlendState(true, BlendFactor.One, BlendFactor.One)]
        });

        _pipeline = app.Factory.CreatePipeline(new PipelineDescription
        {
            Source = Helpers.GetResourceText(typeof(App).Assembly, "Demo.Shaders.Shader.slang"),
            VertexEntryPoint = "vsMain",
            FragmentEntryPoint = "fsMain",
            ColorAttachments = [TextureFormat.B8G8R8A8Srgb],
            DepthAttachment = TextureFormat.D32Sfloat,
            DepthTest = true,
            DepthWrite = true,
            CullMode = CullMode.None,
            Wireframe = false,
            Blends = [BlendState.None],
            VertexLayout = VertexLayout.FromStruct<Vertex>(),
            PushConstantLayout = PushConstantLayout.FromStruct<PushConstants>()
        });
    }

    private struct Vertex
    {
        public Vector4 Position;
        public Vector4 Normal;
    }

    private struct PushConstants
    {
        public Matrix4x4 ViewProj;
    }

    public void OnResize(Vector2D<int> framebufferSize)
    {
        Console.WriteLine($"On resize: {framebufferSize}");

        var factory = _app.Factory;

        _depthTexture?.Dispose();
        _depthTexture = factory.CreateTexture(new TextureDescription
        {
            Width = (uint)framebufferSize.X,
            Height = (uint)framebufferSize.Y,
            Format = TextureFormat.D32Sfloat,
            Usage = TextureUsage.DepthAttachment
        });

        _camera.SetAspect(framebufferSize);
    }

    private void ImGuiPass(Frame frame)
    {
        using var imGuiPass = frame.BeginRenderPass(new RenderPassDescription
        {
            ColorAttachments =
            [
                new ColorAttachment
                {
                    Target = frame.SwapchainColorTarget,
                    LoadOp = LoadOp.Load,
                    StoreOp = StoreOp.Store,
                    FinalLayout = TextureLayout.Present
                }
            ]
        });

        _imGui.Submit(frame, imGuiPass);
    }
    
    public void OnFrame(double deltaTime, Frame frame, InputSnapshot input)
    {
        if (input.WasKeyPressed(G2Key.Escape))
        {
            _camera.ToggleFpsMode(_app.G2Wnd);
        }

        _camera.Update((float)deltaTime, input, _app.G2Wnd.Window.FramebufferSize);
        _imGui.Update((float)deltaTime, input, _app.G2Wnd.Window.FramebufferSize);

        if (!_textInitialized)
        {
            _glyphAtlas.PrepopulateCommons(frame);
            _textInitialized = true;
        }
        
        _overlayQuadRenderer3D.SetCamera(_camera.ViewProj);
        _textRenderer.SetCamera(_camera.ViewProj);
        _lineRenderer3D.SetCamera(_camera.ViewProj, _camera.Position);

        Voxels(out var vertices, out var indices);

        var worldPassDescription = new RenderPassDescription
        {
            ColorAttachments =
            [
                new ColorAttachment
                {
                    Target = frame.SwapchainColorTarget,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearValue = new Vector4(0f, 0f, 0f, 1f),
                    FinalLayout = TextureLayout.ColorAttachment
                }
            ],
            DepthAttachment = new DepthAttachment
            {
                Target = _depthTexture!,
                LoadOp = LoadOp.Clear,
                StoreOp = StoreOp.Store,
                ClearDepth = 1.0f,
                FinalLayout = TextureLayout.DepthAttachment
            }
        };

        using (var worldPass = frame.BeginRenderPass(worldPassDescription))
        {
            _lineRenderer3D.Submit(frame, worldPass);
            _overlayQuadRenderer3D.Submit(frame, worldPass);
            _textRenderer.Submit(frame, worldPass);

            var vertexBuffer = frame.AllocateVertex(sizeof(Vertex) * vertices.Count);
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(vertices)).CopyTo(vertexBuffer.Data);

            var indexBuffer = frame.AllocateVertex(sizeof(uint) * indices.Count);
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(indices)).CopyTo(indexBuffer.Data);
            
            worldPass.SetPipeline(_pipeline);
            worldPass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);
            worldPass.SetScissor(0, 0, (uint)frame.Extent.X, (uint)frame.Extent.Y);

            worldPass.PushConstants(new PushConstants
            {
                ViewProj = _camera.ViewProj
            });
            
            worldPass.SetVertexBuffer(vertexBuffer.Buffer, vertexBuffer.Offset);
            worldPass.SetIndexBuffer(indexBuffer.Buffer, IndexType.UInt32, indexBuffer.Offset);
            worldPass.DrawIndexed((uint)indices.Count);
        }
        
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0));
        ImGui.DockSpaceOverViewport(0U, (ImGuiViewport*) null, ImGuiDockNodeFlags.PassthruCentralNode, (ImGuiWindowClass*) null);
        ImGui.PopStyleColor();
        
        if (ImGui.Begin("Info"))
        {
            ImGui.Text($"Vtx: {vertices.Count}");
        }

        ImGui.End();
        
        ImGuiPass(frame);
    }

    private void Voxels(out List<Vertex> finalVertices, out List<int> finalIndices)
    {
        const int radius = 5;

        var grid = new Grid3D<byte>(radius * 2 + 3);
        var center = new Vector3(grid.SizeX / 2.0f, grid.SizeY / 2.0f, grid.SizeZ / 2.0f);

        for (var z = 0; z < grid.SizeZ; z++)
        {
            for (var y = 0; y < grid.SizeY; y++)
            {
                for (var x = 0; x < grid.SizeX; x++)
                {
                    var p = new Vector3(x, y, z);
                    var sdf = Vector3.Distance(p, center) - radius;

                    grid[x, y, z] = (byte)Math.Clamp(127 - sdf * 127, 0, 255);
                }
            }
        }
        
        var cellsX = grid.SizeX - 1;
        var cellsY = grid.SizeY - 1;
        var cellsZ = grid.SizeZ - 1;

        var vertices = new List<Vector3>();
        var cellIndices = new int[cellsX * cellsY * cellsZ];
        Array.Fill(cellIndices, -1);
        
        for (var z = 0; z < grid.SizeZ - 1; z++)
        {
            for (var y = 0; y < grid.SizeY - 1; y++)
            {
                for (var x = 0; x < grid.SizeX - 1; x++)
                {
                    var c000 = grid[x, y, z];
                    var c100 = grid[x + 1, y, z];
                    var c010 = grid[x, y + 1, z];
                    var c001 = grid[x, y, z + 1];
                    var c110 = grid[x + 1, y + 1, z];
                    var c011 = grid[x, y + 1, z + 1];
                    var c101 = grid[x + 1, y, z + 1];
                    var c111 = grid[x + 1, y + 1, z + 1];

                    var mask =
                        (Unsafe.BitCast<bool, byte>(c000 > 127) << 0) |
                        (Unsafe.BitCast<bool, byte>(c100 > 127) << 1) |
                        (Unsafe.BitCast<bool, byte>(c010 > 127) << 2) |
                        (Unsafe.BitCast<bool, byte>(c110 > 127) << 3) |
                        (Unsafe.BitCast<bool, byte>(c001 > 127) << 4) |
                        (Unsafe.BitCast<bool, byte>(c101 > 127) << 5) |
                        (Unsafe.BitCast<bool, byte>(c011 > 127) << 6) |
                        (Unsafe.BitCast<bool, byte>(c111 > 127) << 7);
                    
                    if (mask == 0 || mask == 255)
                    {
                        continue;
                    }

                    var sum = Vector3.Zero;
                    var count = 0;
                    
                    if (c000 > 127 != c100 > 127)
                    {
                        sum += Vector3.Lerp(
                            new Vector3(x, y, z),
                            new Vector3(x + 1, y, z),
                            (127.0f - c000) / (c100 - c000)
                        );

                        ++count;
                    }

                    if (c000 > 127 != c010 > 127)
                    {
                        sum += Vector3.Lerp(
                            new Vector3(x, y, z),
                            new Vector3(x, y + 1, z),
                            (127.0f - c000) / (c010 - c000)
                        );

                        ++count;
                    }
                    
                    if (c000 > 127 != c001 > 127)
                    {
                        sum += Vector3.Lerp(
                            new Vector3(x, y, z),
                            new Vector3(x, y, z + 1),
                            (127.0f - c000) / (c001 - c000)
                        );

                        ++count;
                    }
                    
                    if (c111 > 127 != c110 > 127)
                    {
                        sum += Vector3.Lerp(
                            new Vector3(x + 1, y + 1, z + 1),
                            new Vector3(x + 1, y + 1, z),
                            (127.0f - c111) / (c110 - c111)
                        );

                        ++count;
                    }
                    
                    if (c111 > 127 != c011 > 127)
                    {
                        sum += Vector3.Lerp(
                            new Vector3(x + 1, y + 1, z + 1),
                            new Vector3(x, y + 1, z + 1),
                            (127.0f - c111) / (c011 - c111)
                        );

                        ++count;
                    }
                    
                    if (c111 > 127 != c101 > 127)
                    {
                        sum += Vector3.Lerp(
                            new Vector3(x + 1, y + 1, z + 1),
                            new Vector3(x + 1, y, z + 1),
                            (127.0f - c111) / (c101 - c111)
                        );

                        ++count;
                    }
                    
                    if (c101 > 127 != c100 > 127)
                    {
                        sum += Vector3.Lerp(
                            new Vector3(x + 1, y, z + 1),
                            new Vector3(x + 1, y, z),
                            (127.0f - c101) / (c100 - c101)
                        );

                        ++count;
                    }
                    
                    if (c101 > 127 != c001 > 127)
                    {
                        sum += Vector3.Lerp(
                            new Vector3(x + 1, y, z + 1),
                            new Vector3(x, y, z + 1),
                            (127.0f - c101) / (c001 - c101)
                        );

                        ++count;
                    }
                    
                    if (c011 > 127 != c010 > 127)
                    {
                        sum += Vector3.Lerp(
                            new Vector3(x, y + 1, z + 1),
                            new Vector3(x, y + 1, z),
                            (127.0f - c011) / (c010 - c011)
                        );

                        ++count;
                    }

                    if (c010 > 127 != c110 > 127)
                    {
                        sum += Vector3.Lerp(
                            new Vector3(x, y + 1, z),
                            new Vector3(x + 1, y + 1, z),
                            (127.0f - c010) / (c110 - c010)
                        );

                        ++count;
                    }

                    if (c100 > 127 != c110 > 127)
                    {
                        sum += Vector3.Lerp(
                            new Vector3(x + 1, y, z),
                            new Vector3(x + 1, y + 1, z),
                            (127.0f - c100) / (c110 - c100)
                        );

                        ++count;
                    }

                    if (c001 > 127 != c011 > 127)
                    {
                        sum += Vector3.Lerp(
                            new Vector3(x, y, z + 1),
                            new Vector3(x, y + 1, z + 1),
                            (127.0f - c001) / (c011 - c001)
                        );

                        ++count;
                    }
                    
                    var vertex = sum / count;

                    cellIndices[z * cellsX * cellsY + y * cellsX + x] = vertices.Count;
                    vertices.Add(vertex);
                }
            }
        }
        
        finalVertices = new List<Vertex>();

        foreach (var vector3 in vertices)
        {
            finalVertices.Add(new Vertex
            {
                Position = new Vector4(vector3, 0.0f),
                Normal = new Vector4(vector3, 0.0f)
            });
        }
        
        finalIndices = new List<int>();

        for (var z = 0; z < grid.SizeZ - 1; z++)
        {
            for (var y = 0; y < grid.SizeY - 1; y++)
            {
                for (var x = 0; x < grid.SizeX - 1; x++)
                {
                    var c000 = grid[x, y, z];
                    var c100 = grid[x + 1, y, z];
                    var c010 = grid[x, y + 1, z];
                    var c001 = grid[x, y, z + 1];

                    var crossX = c000 > 127 != c100 > 127;
                    var crossY = c000 > 127 != c010 > 127;
                    var crossZ = c000 > 127 != c001 > 127;

                    if (!crossX && !crossY && !crossZ)
                    {
                        continue;
                    }

                    var i0 = cellIndices[z * cellsX * cellsY + y * cellsX + x];
                    var v0 = vertices[i0];

                    const float thickness = 0.01f;
                    
                    if (crossX && y > 0 && z > 0)
                    { 
                        var i1 = cellIndices[z * cellsX * cellsY + (y - 1) * cellsX + x];
                        var i2 = cellIndices[(z - 1) * cellsX * cellsY + (y - 1) * cellsX + x];
                        var i3 = cellIndices[(z - 1) * cellsX * cellsY + y * cellsX + x];
                        
                        var v1 = vertices[i1];
                        var v2 = vertices[i2];
                        var v3 = vertices[i3];

                        if (c000 > 127)
                        {
                            finalIndices.Add(i0);
                            finalIndices.Add(i1);
                            finalIndices.Add(i2);
                            finalIndices.Add(i0);
                            finalIndices.Add(i2);
                            finalIndices.Add(i3);
                        }
                        else
                        {
                            finalIndices.Add(i0);
                            finalIndices.Add(i2);
                            finalIndices.Add(i1);
                            finalIndices.Add(i0);
                            finalIndices.Add(i3);
                            finalIndices.Add(i2);
                        }
                        
                        _lineRenderer3D.DrawLine3D(v0, v1, thickness, new Vector4(1, 0, 0, 1));
                        _lineRenderer3D.DrawLine3D(v1, v2, thickness, new Vector4(0, 1, 0, 1));
                        _lineRenderer3D.DrawLine3D(v2, v3, thickness, new Vector4(0, 0, 1, 1));
                        _lineRenderer3D.DrawLine3D(v3, v0, thickness, new Vector4(0, 1, 1, 1));
                    }
                    
                    if (crossY && x > 0 && z > 0)
                    {
                        var i1 = cellIndices[(z - 1) * cellsX * cellsY + y * cellsX + x];
                        var i2 = cellIndices[(z - 1) * cellsX * cellsY + y * cellsX + (x - 1)];
                        var i3 = cellIndices[z * cellsX * cellsY + y * cellsX + (x - 1)];
                        
                        var v1 = vertices[i1];
                        var v2 = vertices[i2];
                        var v3 = vertices[i3];

                        if (c000 > 127)
                        {
                            finalIndices.Add(i0);
                            finalIndices.Add(i1);
                            finalIndices.Add(i2);
                            finalIndices.Add(i0);
                            finalIndices.Add(i2);
                            finalIndices.Add(i3);
                        }
                        else
                        {
                            finalIndices.Add(i0);
                            finalIndices.Add(i2);
                            finalIndices.Add(i1);
                            finalIndices.Add(i0);
                            finalIndices.Add(i3);
                            finalIndices.Add(i2);
                        }
                        
                        _lineRenderer3D.DrawLine3D(v0, v1, thickness, new Vector4(1, 0, 0, 1));
                        _lineRenderer3D.DrawLine3D(v1, v2, thickness, new Vector4(0, 1, 0, 1));
                        _lineRenderer3D.DrawLine3D(v2, v3, thickness, new Vector4(0, 0, 1, 1));
                        _lineRenderer3D.DrawLine3D(v3, v0, thickness, new Vector4(0, 1, 1, 1));
                    }
                    
                    if (crossZ && x > 0 && y > 0)
                    {
                        var i1 = cellIndices[z * cellsX * cellsY + y * cellsX + (x - 1)];
                        var i2 = cellIndices[z * cellsX * cellsY + (y - 1) * cellsX + (x - 1)];
                        var i3 = cellIndices[z * cellsX * cellsY + (y - 1) * cellsX + x];
                        
                        var v1 = vertices[i1];
                        var v2 = vertices[i2];
                        var v3 = vertices[i3];

                        if (c000 > 127)
                        {
                            finalIndices.Add(i0);
                            finalIndices.Add(i1);
                            finalIndices.Add(i2);
                            finalIndices.Add(i0);
                            finalIndices.Add(i2);
                            finalIndices.Add(i3);
                        }
                        else
                        {
                            finalIndices.Add(i0);
                            finalIndices.Add(i2);
                            finalIndices.Add(i1);
                            finalIndices.Add(i0);
                            finalIndices.Add(i3);
                            finalIndices.Add(i2);
                        }
                        
                        _lineRenderer3D.DrawLine3D(v0, v1, thickness, new Vector4(1, 0, 0, 1));
                        _lineRenderer3D.DrawLine3D(v1, v2, thickness, new Vector4(0, 1, 0, 1));
                        _lineRenderer3D.DrawLine3D(v2, v3, thickness, new Vector4(0, 0, 1, 1));
                        _lineRenderer3D.DrawLine3D(v3, v0, thickness, new Vector4(0, 1, 1, 1));
                    }
                }
            }
        }

    }
    
    public void Dispose()
    {
        _imGui.Dispose();
        _textRenderer.Dispose();
        _glyphAtlas.Dispose();
        _textSampler.Dispose();
        _depthTexture?.Dispose();
        _quadRenderer2D.Dispose();
        _overlayQuadRenderer3D.Dispose();
        _lineRenderer3D.Dispose();
        _pipeline.Dispose();
    }
}