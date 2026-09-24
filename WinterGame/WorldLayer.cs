using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.Data;
using Graphics2.ImGui;
using Graphics2.Mathematics;
using Graphics2.Renderer.Lines;
using Graphics2.Renderer.Quads;
using Graphics2.Renderer.Text;
using Graphics2.Windowing.Input;
using ImGuiNET;
using Silk.NET.Maths;
using WinterGame.Core;
using WinterGame.World;
using WinterGame.World.Generation;

// ReSharper disable ForCanBeConvertedToForeach

namespace WinterGame;

internal sealed unsafe class WorldLayer : IDisposable
{
    private readonly App _app;
    
    private readonly ImGuiRenderer _imGui;
    private readonly VkSampler _textSampler;
    private readonly GlyphAtlas _glyphAtlas;
    private readonly TextRenderer _textRenderer;
    private bool _textInitialized;

    private Texture[]? _depthTextures;
    private VkBuffer[]? _argBuffers;

    private readonly QuadRenderer2D _quadRenderer2D;
    private readonly QuadRenderer3D _overlayQuadRenderer3D;
    private readonly LineRenderer3D _lineRenderer3D;
    
    private readonly Camera3D _camera = new();

    private readonly Pipeline _pipeline;

    private int _seed = 3141;
    private float _baseHeight = 4.0f;
    private float _continentalAmplitude = 8.0f;
    private float _hillAmplitude = 1.5f;
    private float _mountainAmplitude = 12.0f;
    private int _chunkCells = 32;
    private int _sectionsX = 4;
    private int _sectionsY = 10;
    private int _sectionsZ = 4;
    private bool _cameraPlaced;

    // A short province wavelength keeps two provinces inside the prototype world, where a shipping one would leave it inside a single province:
    private float _provinceWavelength = 24.0f;
    private float _provinceHalfWidth = 0.3f;
    private float _foldAmplitude = 4.0f;
    private float _foldWavelength = 48.0f;
    private float _snowThickness = 0.35f;
    private float _iceThickness = 0.65f;
    private float _fairingRange = 0.5f;
    private float _fairingStep = 0.5f;
    private int _fairingIterations = 4;

    // The prototype cuts a notch out of the near corner so the strata are visible from the camera, which is not a worldgen stage:
    private bool _cutEnabled = true;
    private float _cutWidth = 24.0f;
    private float _cutDepth = 12.0f;
    private float _cutFloorY = -34.0f;

    private static readonly string[] MaterialNames =
    [
        "rock", "ice", "snow", "sandstone", "shale", "limestone", "basalt", "gneiss"
    ];

    /// <summary>
    ///     The most sections one build may produce. It caps the indirect argument list and it may not exceed the material volume array size in Shader.slang.
    /// </summary>
    private const int MaxSections = 1024;

    /// <summary>
    ///     The relaxation margin is one cell per iteration, so the read box grows with it.
    /// </summary>
    private const int MaxRelaxIterations = 8;

    private MeshArena? _arena;
    private readonly List<Chunk> _chunks = [];   
    private bool _rebuildRequested = true;
    private long _solidVoxels;
    private long _vertexCount;
    private long _indexCount;

   
    private sealed class Chunk
    {
        public Vector3i Origin;
        public MeshAllocation Allocation;
        public int VertexCount;
        public int IndexCount;
    }
    
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
            Source = Helpers.GetResourceText(typeof(App).Assembly, "WinterGame.Shaders.Shader.slang"),
            VertexEntryPoint = "vsMain",
            FragmentEntryPoint = "fsMain",
            ColorAttachments = [TextureFormat.B8G8R8A8Srgb],
            DepthAttachment = TextureFormat.D32Sfloat,
            DepthTest = true,
            DepthWrite = true,
            CullMode = CullMode.Back,
            FrontFace = FrontFace.CounterClockwise,
            Wireframe = false,
            Blends = [BlendState.None],
            VertexLayout = VertexLayout.FromStruct<VoxelVertex>(),
            PushConstantLayout = PushConstantLayout.FromStruct<PushConstants>(),
            SsboLayouts = new Dictionary<string, SsboLayout>
            {
                ["draws"] = SsboLayout.FromStruct<DrawRecord>()
            }
        });
        
        _arena = new MeshArena(factory, new MeshArenaDescription
        {
            VertexStride = sizeof(VoxelVertex),
            IndexType = IndexType.UInt16,
            VertexClasses =
            [
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 512, SlotsPerPage = 32 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 1024, SlotsPerPage = 32 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 2048, SlotsPerPage = 16 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 4096, SlotsPerPage = 16 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 8192, SlotsPerPage = 4 }
            ],
            IndexClasses =
            [
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 2048, SlotsPerPage = 32 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 4096, SlotsPerPage = 32 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 8192, SlotsPerPage = 16 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 16384, SlotsPerPage = 16 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 32768, SlotsPerPage = 4 }
            ]
        });

        _argBuffers = new VkBuffer[_app.Context!.FramesInFlight];
        for (var index = 0; index < _argBuffers.Length; index++)
        {
            _argBuffers[index] = factory.CreateBuffer(new BufferDescription
            {
                Size = DrawIndexedIndirectCommand.SizeInBytes * MaxSections,
                Usage = BufferUsage.IndirectHostVisible
            });
        }
    }

    private struct PushConstants
    {
        // ReSharper disable once NotAccessedField.Local
        public Matrix4x4 ViewProj;
    }

    public void OnResize(Vector2D<int> framebufferSize)
    {
        Console.WriteLine($"On resize: {framebufferSize}");

        var factory = _app.Factory;

        DisposeDepthTextures();

        var depthTextureCount = (int)_app.Context!.FramesInFlight;
        _depthTextures = new Texture[depthTextureCount];

        for (var slot = 0; slot < depthTextureCount; slot++)
        {
            _depthTextures[slot] = factory.CreateTexture(new TextureDescription
            {
                Width = (uint)framebufferSize.X,
                Height = (uint)framebufferSize.Y,
                Format = TextureFormat.D32Sfloat,
                Usage = TextureUsage.DepthAttachment
            });
        }

        _camera.SetAspect(framebufferSize);
    }

    /// <summary>
    ///     Issues one indirect draw for a run of commands, or nothing when the run is empty.
    /// </summary>
    private static void DrawRun(RenderPassEncoder pass, VkBuffer args, int firstCommand, int commandCount)
    {
        if (firstCommand < 0 || commandCount <= 0)
        {
            return;
        }

        pass.DrawIndexedIndirect(args, (ulong)firstCommand * DrawIndexedIndirectCommand.SizeInBytes, (uint)commandCount);
    }
    
    private static void UploadMesh(Frame frame, in MeshAllocation allocation, ReadOnlySpan<byte> vertices, ReadOnlySpan<byte> indices)
    {
        UploadRange(frame, allocation.Vertices.Buffer, allocation.Vertices.BaseAddress, vertices);
        UploadRange(frame, allocation.Indices.Buffer, allocation.Indices.BaseAddress, indices);
    }

    private static void UploadRange(Frame frame, VkBuffer destination, int destinationOffset, ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        var staging = frame.AllocateVertex(data.Length);
        data.CopyTo(staging.Data);
        frame.CopyBuffer(staging.Buffer, staging.Offset, destination, (ulong)destinationOffset, (ulong)data.Length);
    }

    private readonly FrameTimeTracker _tracker = new(4000 * 5);
    
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
        if (_rebuildRequested)
        {
            _app.Device.WaitIdle();
            BuildWorld(frame);
            _rebuildRequested = false;
        }

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
        
        var worldPassDescription = new RenderPassDescription
        {
            ColorAttachments =
            [
                new ColorAttachment
                {
                    Target = frame.SwapchainColorTarget,
                    LoadOp = LoadOp.Clear,
                    StoreOp = StoreOp.Store,
                    ClearValue = new Vector4(0.1f, 0.1f, 0.1f, 1f),
                    FinalLayout = TextureLayout.ColorAttachment
                }
            ],
            DepthAttachment = new DepthAttachment
            {
                Target = _depthTextures![frame.SlotIndex],
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
            
            if (_chunks.Count > 0)
            {
                worldPass.SetPipeline(_pipeline);
                worldPass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);
                worldPass.SetScissor(0, 0, (uint)frame.Extent.X, (uint)frame.Extent.Y);

                worldPass.PushConstants(new PushConstants
                {
                    ViewProj = _camera.ViewProj
                });

                var records = frame.AllocateStorage(_chunks.Count * Unsafe.SizeOf<DrawRecord>());
                var recordSpan = MemoryMarshal.Cast<byte, DrawRecord>(records.Data);
                for (var index = 0; index < _chunks.Count; index++)
                {
                    var chunk = _chunks[index];
                    recordSpan[index] = new DrawRecord
                    {
                        Origin = new Vector3(chunk.Origin.X, chunk.Origin.Y, chunk.Origin.Z),
                        VoxelSize = WorldConstants.VoxelSize
                    };
                }

                worldPass.BindStorageBuffer("draws", records);

                
                var args = _argBuffers![frame.SlotIndex];
                var argSpan = MemoryMarshal.Cast<byte, DrawIndexedIndirectCommand>(args.MappedData);
                VkBuffer? vertexBuffer = null;
                VkBuffer? indexBuffer = null;
                var runFirstCommand = 0;

                for (var index = 0; index < _chunks.Count; index++)
                {
                    var chunk = _chunks[index];

                    if (!ReferenceEquals(chunk.Allocation.Vertices.Buffer, vertexBuffer) ||
                        !ReferenceEquals(chunk.Allocation.Indices.Buffer, indexBuffer))
                    {
                        DrawRun(worldPass, args, runFirstCommand, index - runFirstCommand);

                        vertexBuffer = chunk.Allocation.Vertices.Buffer;
                        indexBuffer = chunk.Allocation.Indices.Buffer;
                        runFirstCommand = index;
                        worldPass.SetVertexBuffer(vertexBuffer);
                        worldPass.SetIndexBuffer(indexBuffer, IndexType.UInt16);
                    }

                    argSpan[index] = new DrawIndexedIndirectCommand
                    {
                        IndexCount = (uint)chunk.IndexCount,
                        InstanceCount = 1,
                        FirstIndex = (uint)chunk.Allocation.Indices.ElementOffset,
                        VertexOffset = chunk.Allocation.Vertices.ElementOffset,
                        FirstInstance = (uint)index
                    };
                }

                DrawRun(worldPass, args, runFirstCommand, _chunks.Count - runFirstCommand);
            }
        }

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0, 0, 0, 0));
        ImGui.DockSpaceOverViewport(0U, (ImGuiViewport*) null, ImGuiDockNodeFlags.PassthruCentralNode, (ImGuiWindowClass*) null);
        ImGui.PopStyleColor();

        _tracker.Update();

        if (ImGui.Begin("Tracker"))
        {
            ImGui.Text($"Frames: {_tracker.TotalFrames}/{_tracker.WindowFrames}");
            ImGui.Separator();
            ImGui.Text("Time (window):");
            ImGui.Text($"  Avg: {_tracker.AverageMilliseconds * 1000:F2}us");
            ImGui.Text($"  Min: {_tracker.MinimumMilliseconds * 1000:F2}us");
            ImGui.Text($"  Max: {_tracker.MaximumMilliseconds * 1000:F2}us");
            ImGui.Text($"  P50: {_tracker.MedianMilliseconds * 1000:F2}us");
            ImGui.Text($"  P95: {_tracker.P95Milliseconds * 1000:F2}us");
            ImGui.Text($"  P99: {_tracker.P99Milliseconds * 1000:F2}us");
        }
        
        ImGui.End();
        
        if (ImGui.Begin("Generator"))
        {
            ImGui.InputInt("Chunk cells", ref _chunkCells, 8);
            ImGui.InputInt("Sections X", ref _sectionsX, 1);
            ImGui.InputInt("Sections Y", ref _sectionsY, 1);
            ImGui.InputInt("Sections Z", ref _sectionsZ, 1);
            ImGui.InputFloat("Base height (m)", ref _baseHeight, 1.0f);
            ImGui.InputFloat("Continental amp (m)", ref _continentalAmplitude, 1.0f);
            ImGui.InputFloat("Hill amp (m)", ref _hillAmplitude, 0.25f);
            ImGui.InputFloat("Mountain amp (m)", ref _mountainAmplitude, 1.0f);

            _chunkCells = Math.Clamp(_chunkCells, 1, 64);
            _baseHeight = MathF.Max(_baseHeight, 0.0f);
            _continentalAmplitude = MathF.Max(_continentalAmplitude, 0.0f);
            _hillAmplitude = MathF.Max(_hillAmplitude, 0.0f);
            _mountainAmplitude = MathF.Max(_mountainAmplitude, 0.0f);
            ClampSectionCounts();

            ImGui.Separator();
            ImGui.Text("World state:");
            ImGui.Text($"Sections: {_sectionsX} x {_sectionsY} x {_sectionsZ} = {_sectionsX * _sectionsY * _sectionsZ} of {MaxSections}");
            ImGui.Text($"Drawn: {_chunks.Count}");
            ImGui.Text($"Voxel size: {WorldConstants.VoxelSize} m");
            ImGui.Text($"World: {_sectionsX * _chunkCells * WorldConstants.VoxelSize} x {_sectionsY * _chunkCells * WorldConstants.VoxelSize} x {_sectionsZ * _chunkCells * WorldConstants.VoxelSize} m");
            ImGui.Text($"Solid Voxels: {_solidVoxels}");
            ImGui.Text($"Vertices: {_vertexCount}");
            ImGui.Text($"Indices: {_indexCount}");

            ImGui.Separator();
            ImGui.Text("Camera:");

            var forward = _camera.Forward;
            var facing = MathF.Abs(forward.X) >= MathF.Abs(forward.Z)
                ? forward.X >= 0.0f ? "+X" : "-X"
                : forward.Z >= 0.0f ? "+Z" : "-Z";

            ImGui.Text($"Position: {_camera.Position.X:F1}, {_camera.Position.Y:F1}, {_camera.Position.Z:F1} m");
            ImGui.Text($"Forward: {forward.X:F2}, {forward.Y:F2}, {forward.Z:F2}");
            ImGui.Text($"Facing: {facing}");
            ImGui.Text($"Yaw: {_camera.Yaw * 180.0f / MathF.PI:F0} deg, Pitch: {_camera.Pitch * 180.0f / MathF.PI:F0} deg");

            var arenaStats = _arena!.GetStats();

            ImGui.Separator();
            ImGui.Text("Arena:");
            ImGui.Text($"Vertex: {arenaStats.Vertices.Pages} page(s), {arenaStats.Vertices.Bytes / 1024} KiB");
            ImGui.Text($"Vertex slots: {arenaStats.Vertices.UsedSlots} used, {arenaStats.Vertices.FreeSlots} free");
            ImGui.Text($"Index: {arenaStats.Indices.Pages} page(s), {arenaStats.Indices.Bytes / 1024} KiB");
            ImGui.Text($"Index slots: {arenaStats.Indices.UsedSlots} used, {arenaStats.Indices.FreeSlots} free");
            ImGui.Text($"Total: {arenaStats.Bytes / 1024} KiB");

            if (ImGui.Button("Run"))
            {
                _rebuildRequested = true;
            }

            ImGui.Separator();
            ImGui.Text("Material:");
            ImGui.InputFloat("Province wavelength (m)", ref _provinceWavelength, 4.0f);
            ImGui.InputFloat("Province half width", ref _provinceHalfWidth, 0.05f);
            ImGui.InputFloat("Fold amplitude (m)", ref _foldAmplitude, 0.25f);
            ImGui.InputFloat("Fold wavelength (m)", ref _foldWavelength, 8.0f);
            ImGui.InputFloat("Snow thickness (m)", ref _snowThickness, 0.05f);
            ImGui.InputFloat("Ice thickness (m)", ref _iceThickness, 0.05f);

            _provinceWavelength = MathF.Max(_provinceWavelength, 1.0f);
            _provinceHalfWidth = Math.Clamp(_provinceHalfWidth, 0.0f, 0.5f);
            _foldWavelength = MathF.Max(_foldWavelength, 1.0f);
            _snowThickness = MathF.Max(_snowThickness, 0.0f);
            _iceThickness = MathF.Max(_iceThickness, 0.0f);

            ImGui.Separator();
            ImGui.Text("Fairing:");
            if (ImGui.SliderInt("Iterations", ref _fairingIterations, 0, MaxRelaxIterations))
            {
                _rebuildRequested = true;
            }

            if (ImGui.SliderFloat("Step", ref _fairingStep, 0.0f, 1.0f))
            {
                _rebuildRequested = true;
            }

            if (ImGui.SliderFloat("Range (voxels)", ref _fairingRange, 0.0f, 1.5f))
            {
                _rebuildRequested = true;
            }

            _fairingIterations = Math.Clamp(_fairingIterations, 0, MaxRelaxIterations);
            _fairingStep = Math.Clamp(_fairingStep, 0.0f, 1.0f);
            _fairingRange = Math.Max(_fairingRange, 0.0f);

            ImGui.Separator();
            ImGui.Text("Prototype cut:");
            ImGui.Checkbox("Cut enabled", ref _cutEnabled);
            ImGui.InputFloat("Cut width (m)", ref _cutWidth, 1.0f);
            ImGui.InputFloat("Cut depth (m)", ref _cutDepth, 1.0f);
            ImGui.InputFloat("Cut floor (m)", ref _cutFloorY, 1.0f);

            var gpuOnly = _app.Factory.GetAllocatorStats(AllocatorFamily.GpuOnly);
            var cpuToGpu = _app.Factory.GetAllocatorStats(AllocatorFamily.CpuToGpu);

            ImGui.Separator();
            ImGui.Text("Allocators:");
            ImGui.Text($"GPU only: {gpuOnly.Pages} page(s), {gpuOnly.BytesUsed} bytes, {gpuOnly.FreeRanges} free range(s)");
            ImGui.Text($"CPU to GPU: {cpuToGpu.Pages} page(s), {cpuToGpu.BytesUsed} bytes, {cpuToGpu.FreeRanges} free range(s)");
        }

        ImGui.End();
        
        ImGuiPass(frame);
    }

  
    private void BuildWorld(Frame frame)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var arena = _arena!;

        foreach (var chunk in _chunks)
        {
            arena.MarkFree(frame, chunk.Allocation);
        }

        _chunks.Clear();
        _solidVoxels = 0;
        _vertexCount = 0;
        _indexCount = 0;

        var cellCount = new Vector3i(_chunkCells, _chunkCells, _chunkCells);

        // Each relaxation pass reaches one cell further, so the chunk is read with that many cells of margin:
        var relaxHalo = Math.Clamp(_fairingIterations, 0, MaxRelaxIterations);
        var halo = new Vector3i(relaxHalo);

        var config = new TerrainConfig
        {
            BaseHeight = _baseHeight,
            ContinentalAmplitude = _continentalAmplitude,
            HillAmplitude = _hillAmplitude,
            MountainAmplitude = _mountainAmplitude,
            ProvinceWavelength = _provinceWavelength,
            FoldAmplitude = _foldAmplitude,
            FoldWavelength = _foldWavelength,
            SnowThickness = _snowThickness,
            IceThickness = _iceThickness,
            Provinces = new StrataSet(_provinceHalfWidth, TerrainStrata.Tables)
        };

        var generator = new TerrainGraphSource(_seed, config);

        // The column is centred on the surface at the world center, so it reaches well below the strata anchor and above every peak:
        var centreX = _sectionsX * _chunkCells / 2;
        var centreZ = _sectionsZ * _chunkCells / 2;
        var centreHeight = generator.Elevation(centreX, centreZ);
        var originY = (int)MathF.Floor(centreHeight / WorldConstants.VoxelSize) - _sectionsY * _chunkCells / 2;

        var cutMaxX = _cutEnabled ? (int)(_cutWidth / WorldConstants.VoxelSize) : 0;
        var cutMaxZ = _cutEnabled ? (int)(_cutDepth / WorldConstants.VoxelSize) : 0;
        var cutMinY = _cutEnabled ? (int)MathF.Floor(_cutFloorY / WorldConstants.VoxelSize) : 0;

        if (!_cameraPlaced)
        {
            _cameraPlaced = true;
            _camera.Position = new Vector3(
                _sectionsX * _chunkCells * WorldConstants.VoxelSize * 0.5f,
                _cutFloorY + 14.0f,
                -8.0f
            );
            _camera.Pitch = -0.2f;
        }

        Span<long> materialCounts = stackalloc long[SurfaceMaterial.Count];
        Span<long> solidCounts = stackalloc long[SurfaceMaterial.Count];
        long interiorCount = 0;

        for (var sectionY = 0; sectionY < _sectionsY; sectionY++)
        {
            for (var sectionZ = 0; sectionZ < _sectionsZ; sectionZ++)
            {
                for (var sectionX = 0; sectionX < _sectionsX; sectionX++)
                {
                    var cellMin = new Vector3i(sectionX * _chunkCells, originY + sectionY * _chunkCells, sectionZ * _chunkCells);
                    var box = SurfaceNetsMesher.GetSampleRange(cellMin - halo, cellCount + halo + halo);
                    var localCellMin = cellMin - box.Min;

                    var content = Memory3D<byte>.Allocate(
                        box.Max.X - box.Min.X + 1,
                        box.Max.Y - box.Min.Y + 1,
                        box.Max.Z - box.Min.Z + 1
                    );

                    var material = Memory3D<byte>.Allocate(content.SizeX, content.SizeY, content.SizeZ);

                    generator.Generate(box.Min, box.Max, content.Span, material.Span);

                    if (_cutEnabled)
                    {
                        ClearCut(content.Span, material.Span, box.Min, cutMaxX, cutMaxZ, cutMinY);
                    }

                    var sampleCount = cellCount + new Vector3i(1);
                    _solidVoxels += CountSolid(content.Span, localCellMin, sampleCount);
                    CountMaterials(content.Span, material.Span, localCellMin, sampleCount, materialCounts, solidCounts, ref interiorCount);

                    var vertices = new List<VoxelVertex>();
                    var indices = new List<int>();

                    MultiLabelSurfaceNets.Mesh(new MultiLabelSurfaceNetsDescription
                    {
                        Content = content,
                        Material = material,
                        IsoLevel = WorldConstants.ContentIsoLevel,
                        CellMin = localCellMin,
                        CellCount = cellCount,
                        Vertices = vertices,
                        Indices = indices,
                        Fairing = new SurfaceFairingSettings
                        {
                            AllowedRange = _fairingRange,
                            StepSize = _fairingStep,
                            Iterations = relaxHalo
                        }
                    });

                    if (indices.Count == 0)
                    {
                        // A fully solid or fully air section emits no geometry, so it costs no arena slot:
                        continue;
                    }

                    // A chunk holds far fewer than 65536 vertices, so its indices stay 16 bits and slot-relative:
                    var indexScratch = new ushort[indices.Count];
                    for (var index = 0; index < indices.Count; index++)
                    {
                        indexScratch[index] = (ushort)indices[index];
                    }

                    var allocation = arena.Allocate(vertices.Count, indexScratch.Length);

                    UploadMesh(
                        frame,
                        allocation,
                        MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(vertices)),
                        MemoryMarshal.AsBytes(indexScratch.AsSpan())
                    );

                    _chunks.Add(new Chunk
                    {
                        Origin = box.Min,
                        Allocation = allocation,
                        VertexCount = vertices.Count,
                        IndexCount = indexScratch.Length
                    });

                    _vertexCount += vertices.Count;
                    _indexCount += indexScratch.Length;
                }
            }
        }

        // Slot order is not page order, and the submission loop needs the pages contiguous:
        // A run breaks when either buffer changes, so ordering groups the chunks that share a pair.
        _chunks.Sort(CompareChunksByBuffer);

        watch.Stop();

        Console.WriteLine($"Build: {watch.Elapsed.TotalMilliseconds:F0} ms for {_sectionsX * _sectionsY * _sectionsZ} section(s)");
        Console.WriteLine($"World: {_chunks.Count} section(s) drawn of {_sectionsX * _sectionsY * _sectionsZ}, {_sectionsX * _chunkCells * WorldConstants.VoxelSize} x {_sectionsY * _chunkCells * WorldConstants.VoxelSize} x {_sectionsZ * _chunkCells * WorldConstants.VoxelSize} m, {_vertexCount} verts, {_indexCount} indices, {_solidVoxels} solid voxels");
        Console.WriteLine($"Materials: {FormatMaterialCounts(solidCounts)} over every solid sample");
        Console.WriteLine($"Materials: {FormatMaterialCounts(materialCounts)} within a sample of the surface, {interiorCount} saturated interior sample(s)");
        Console.WriteLine($"Draws: {CountRuns()} run(s) over {CountBufferPairs()} buffer pair(s)");
    }

    private void ClampSectionCounts()
    {
        _sectionsX = Math.Clamp(_sectionsX, 1, 16);
        _sectionsY = Math.Clamp(_sectionsY, 1, 32);
        _sectionsZ = Math.Clamp(_sectionsZ, 1, 16);

        while (_sectionsX * _sectionsY * _sectionsZ > MaxSections)
        {
            if (_sectionsY > 1)
            {
                _sectionsY--;
            }
            else if (_sectionsX > 1)
            {
                _sectionsX--;
            }
            else if (_sectionsZ > 1)
            {
                _sectionsZ--;
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>
    ///     Clears a notch out of the near corner of the world so the strata are visible, which is a prototype aid rather than a worldgen stage.
    ///     It runs after generation and before the mesh, and it clears the material too, because the shader's reconstruction skips only the null id.
    /// </summary>
    private static void ClearCut(Span3D<byte> content, Span3D<byte> material, Vector3i boxMin, int cutMaxX, int cutMaxZ, int cutMinY)
    {
        for (var z = 0; z < content.SizeZ; z++)
        {
            var worldZ = boxMin.Z + z;

            if (worldZ >= cutMaxZ)
            {
                continue;
            }

            for (var y = 0; y < content.SizeY; y++)
            {
                var worldY = boxMin.Y + y;

                if (worldY <= cutMinY)
                {
                    continue;
                }

                for (var x = 0; x < content.SizeX; x++)
                {
                    if (boxMin.X + x >= cutMaxX)
                    {
                        continue;
                    }

                    content[x, y, z] = 0;
                    material[x, y, z] = SurfaceMaterial.Null;
                }
            }
        }
    }

    private static string FormatMaterialCounts(ReadOnlySpan<long> counts)
    {
        var builder = new System.Text.StringBuilder();

        for (var id = 0; id < counts.Length; id++)
        {
            if (id > 0)
            {
                builder.Append(", ");
            }

            builder.Append(MaterialNames[id]).Append('=').Append(counts[id]);
        }

        return builder.ToString();
    }

    /// <summary>
    ///     Orders the chunks so that every run of chunks sharing a pair of buffers is contiguous.
    /// </summary>
    private static int CompareChunksByBuffer(Chunk left, Chunk right)
    {
        var byVertexAllocator = left.Allocation.Vertices.Allocator.CompareTo(right.Allocation.Vertices.Allocator);

        if (byVertexAllocator != 0)
        {
            return byVertexAllocator;
        }

        var byVertexPage = left.Allocation.Vertices.Page.CompareTo(right.Allocation.Vertices.Page);

        if (byVertexPage != 0)
        {
            return byVertexPage;
        }

        var byIndexAllocator = left.Allocation.Indices.Allocator.CompareTo(right.Allocation.Indices.Allocator);

        if (byIndexAllocator != 0)
        {
            return byIndexAllocator;
        }

        var byIndexPage = left.Allocation.Indices.Page.CompareTo(right.Allocation.Indices.Page);

        return byIndexPage != 0 ? byIndexPage : left.Allocation.Vertices.Slot.CompareTo(right.Allocation.Vertices.Slot);
    }

    /// <summary>
    ///     The number of indirect draws the submission loop will issue, which is one per change of buffer pair.
    /// </summary>
    private int CountRuns()
    {
        var runs = 0;
        VkBuffer? vertexBuffer = null;
        VkBuffer? indexBuffer = null;

        foreach (var chunk in _chunks)
        {
            if (!ReferenceEquals(chunk.Allocation.Vertices.Buffer, vertexBuffer) ||
                !ReferenceEquals(chunk.Allocation.Indices.Buffer, indexBuffer))
            {
                runs++;
                vertexBuffer = chunk.Allocation.Vertices.Buffer;
                indexBuffer = chunk.Allocation.Indices.Buffer;
            }
        }

        return runs;
    }

    /// <summary>
    ///     The number of distinct buffer pairs in use. When it is larger than the run count the ordering failed to gather a pair into one run, which would draw a chunk from another chunk's buffers.
    /// </summary>
    private int CountBufferPairs()
    {
        var pairs = new HashSet<(VkBuffer, VkBuffer)>();

        foreach (var chunk in _chunks)
        {
            pairs.Add((chunk.Allocation.Vertices.Buffer, chunk.Allocation.Indices.Buffer));
        }

        return pairs.Count;
    }

    private static int CountSolid(Span3D<byte> content, Vector3i min, Vector3i count)
    {
        var solid = 0;

        for (var z = 0; z < count.Z; z++)
        {
            for (var y = 0; y < count.Y; y++)
            {
                for (var x = 0; x < count.X; x++)
                {
                    if (content[min.X + x, min.Y + y, min.Z + z] > 127)
                    {
                        solid++;
                    }
                }
            }
        }

        return solid;
    }

    /// <summary>
    ///     Counts the ids of the samples the reconstruction can read, which are the solid ones within a voxel of the surface, and fills <paramref name="solidCounts" /> with every solid sample.
    ///     Anything deeper has a saturated field and no gradient, and the volume's interior is never sampled, so the near-surface counts are the ones the shader can show.
    /// </summary>
    private static void CountMaterials(Span3D<byte> content, Span3D<byte> material, Vector3i min, Vector3i count, Span<long> surfaceCounts, Span<long> solidCounts, ref long interiorCount)
    {
        for (var z = 0; z < count.Z; z++)
        {
            for (var y = 0; y < count.Y; y++)
            {
                for (var x = 0; x < count.X; x++)
                {
                    var contentValue = content[min.X + x, min.Y + y, min.Z + z];
                    var id = material[min.X + x, min.Y + y, min.Z + z];

                    if (id == SurfaceMaterial.Null)
                    {
                        continue;
                    }

                    if (id < solidCounts.Length)
                    {
                        solidCounts[id]++;
                    }

                    if (contentValue == 255)
                    {
                        interiorCount++;
                        continue;
                    }

                    if (id < surfaceCounts.Length)
                    {
                        surfaceCounts[id]++;
                    }
                }
            }
        }
    }

    private void DisposeDepthTextures()
    {
        if (_depthTextures == null)
        {
            return;
        }

        foreach (var depthTexture in _depthTextures)
        {
            depthTexture.Dispose();
        }

        _depthTextures = null;
    }

    private void DisposeArgBuffers()
    {
        if (_argBuffers == null)
        {
            return;
        }

        foreach (var argBuffer in _argBuffers)
        {
            argBuffer.Dispose();
        }

        _argBuffers = null;
    }
    
    public void Dispose()
    {
        _imGui.Dispose();
        _textRenderer.Dispose();
        _glyphAtlas.Dispose();
        _textSampler.Dispose();
        DisposeDepthTextures();
        DisposeArgBuffers();
        _quadRenderer2D.Dispose();
        _overlayQuadRenderer3D.Dispose();
        _lineRenderer3D.Dispose();
        _pipeline.Dispose();
        _arena?.Dispose();
    }
}