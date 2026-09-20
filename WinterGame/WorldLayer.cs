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

    private const float VoxelSize = 0.25f;

    private int _seed = 3141;
    private float _voxelsPerUnit = 16.0f;
    private float _surfaceHeight = 16.0f;
    private float _reliefVoxels = 12.0f; 
    private int _chunkCells = 32;
    private int _chunksX = 4;
    private int _chunksZ = 4;

    private float _snowLine = 16.0f;
    private float _snowLineFade = 3.0f;
    private float _snowSlopeMin = 0.80f;
    private float _snowSlopeMax = 0.95f;
    private float _rockSlopeMin = 0.45f;
    private float _rockSlopeMax = 0.70f;

    /// <summary>
    ///     Has to match the material volume array size in Shader.slang, which BindTextureArray checks.
    /// </summary>
    private const int VolumeSlots = 1024;

    private MeshArena? _arena;
    private VolumeArena? _volumes;
    private readonly List<Chunk> _chunks = [];   
    private bool _rebuildRequested = true;
    private long _solidVoxels;
    private long _vertexCount;
    private long _indexCount;

   
    private sealed class Chunk
    {
        public Vector3i Origin;
        public MeshAllocation Allocation;
        public VolumeHandle Volume;
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
            VertexLayout = VertexLayout.FromStruct<SurfaceNetsVertex>(),
            PushConstantLayout = PushConstantLayout.FromStruct<PushConstants>(),
            SsboLayouts = new Dictionary<string, SsboLayout>
            {
                ["draws"] = SsboLayout.FromStruct<DrawRecord>()
            }
        });
        
        _arena = new MeshArena(factory, new MeshArenaDescription
        {
            VertexStride = sizeof(SurfaceNetsVertex),
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
                Size = 20 * 1000,
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

                var arena = _arena!;
                var volumes = _volumes!;

                worldPass.BindTextureArray("materialVolumes", volumes.Array);

                var records = frame.AllocateStorage(_chunks.Count * Unsafe.SizeOf<DrawRecord>());
                var recordSpan = MemoryMarshal.Cast<byte, DrawRecord>(records.Data);
                for (var index = 0; index < _chunks.Count; index++)
                {
                    var chunk = _chunks[index];
                    recordSpan[index] = new DrawRecord
                    {
                        Origin = new Vector3(chunk.Origin.X, chunk.Origin.Y, chunk.Origin.Z),
                        VoxelSize = VoxelSize,
                        MaterialVolume = chunk.Volume.Index,
                        MaterialSizeX = (uint)volumes.SizeX,
                        MaterialSizeY = (uint)volumes.SizeY,
                        MaterialSizeZ = (uint)volumes.SizeZ
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
            ImGui.InputInt("Chunks X", ref _chunksX, 1);
            ImGui.InputInt("Chunks Z", ref _chunksZ, 1);
            ImGui.InputFloat("Unit", ref _voxelsPerUnit, 1.0f);
            ImGui.InputFloat("Height", ref _surfaceHeight, 0.5f);
            ImGui.InputFloat("Relief", ref _reliefVoxels, 0.5f);

            _chunkCells = Math.Clamp(_chunkCells, 1, 64);
            _chunksX = Math.Clamp(_chunksX, 1, 16);
            _chunksZ = Math.Clamp(_chunksZ, 1, 16);
            _voxelsPerUnit = MathF.Max(_voxelsPerUnit, 0.01f);

            ImGui.Separator();
            ImGui.Text("World state:");
            ImGui.Text($"Chunks: {_chunks.Count}");
            ImGui.Text($"Voxel size: {VoxelSize} m");
            ImGui.Text($"World: {_chunksX * _chunkCells * VoxelSize} x {_chunksZ * _chunkCells * VoxelSize} m");
            ImGui.Text($"Solid Voxels: {_solidVoxels}");
            ImGui.Text($"Vertices: {_vertexCount}");
            ImGui.Text($"Indices: {_indexCount}");

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
            ImGui.InputFloat("Snow line", ref _snowLine, 0.5f);
            ImGui.InputFloat("Snow fade", ref _snowLineFade, 0.5f);
            ImGui.InputFloat("Snow slope min", ref _snowSlopeMin, 0.01f);
            ImGui.InputFloat("Snow slope max", ref _snowSlopeMax, 0.01f);
            ImGui.InputFloat("Rock slope min", ref _rockSlopeMin, 0.01f);
            ImGui.InputFloat("Rock slope max", ref _rockSlopeMax, 0.01f);

            _snowLineFade = MathF.Max(_snowLineFade, 0.0f);
            _snowSlopeMin = Math.Clamp(_snowSlopeMin, 0.0f, 1.0f);
            _snowSlopeMax = Math.Clamp(_snowSlopeMax, 0.0f, 1.0f);
            _rockSlopeMin = Math.Clamp(_rockSlopeMin, 0.0f, 1.0f);
            _rockSlopeMax = Math.Clamp(_rockSlopeMax, 0.0f, 1.0f);

            if (_volumes != null)
            {
                var volumeStats = _volumes.GetStats();

                ImGui.Text($"Volumes: {volumeStats.UsedSlots} used, {volumeStats.Created} created of {volumeStats.SlotCount} slot(s), {volumeStats.Bytes / 1024} KiB");
            }

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
        var arena = _arena!;

        foreach (var chunk in _chunks)
        {
            arena.MarkFree(frame, chunk.Allocation);
            _volumes?.MarkFree(frame, chunk.Volume);
        }

        _chunks.Clear();
        _solidVoxels = 0;
        _vertexCount = 0;
        _indexCount = 0;

        var cellCount = new Vector3i(_chunkCells, _chunkCells, _chunkCells);

        // Every chunk reads the same window shape, so the volume extent comes from the same box the content is built from:
        var window = SurfaceNetsMesher.GetSampleRange(Vector3i.Zero, cellCount);
        var volumes = EnsureVolumeArena(frame, window.Max.X - window.Min.X + 1, window.Max.Y - window.Min.Y + 1, window.Max.Z - window.Min.Z + 1);

        var generator = new FbmTerrainGenerator(
            _seed,
            _voxelsPerUnit,
            _surfaceHeight,
            _reliefVoxels,
            new SurfaceMaterialClassifier(_snowLine, _snowLineFade, _snowSlopeMin, _snowSlopeMax, _rockSlopeMin, _rockSlopeMax)
        );

        Span<long> materialCounts = stackalloc long[SurfaceMaterial.Count];
        long interiorCount = 0;

        for (var chunkZ = 0; chunkZ < _chunksZ; chunkZ++)
        {
            for (var chunkX = 0; chunkX < _chunksX; chunkX++)
            {
                var cellMin = new Vector3i(chunkX * _chunkCells, 0, chunkZ * _chunkCells);
                var box = SurfaceNetsMesher.GetSampleRange(cellMin, cellCount);

                var content = Memory3D<byte>.Allocate(
                    box.Max.X - box.Min.X + 1,
                    box.Max.Y - box.Min.Y + 1,
                    box.Max.Z - box.Min.Z + 1
                );

                var material = Memory3D<byte>.Allocate(content.SizeX, content.SizeY, content.SizeZ);

                generator.Generate(box.Min, box.Max, content.Span, material.Span);

                var vertices = new List<SurfaceNetsVertex>();
                var indices = new List<int>();

                SurfaceNetsMesher.Mesh(new SurfaceNetsMeshingDescription
                {
                    Content = content,
                    IsoLevel = 127,
                    Vertices = vertices,
                    Indices = indices
                });

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

                // The material window is exactly the box the mesher read, so a volume texel is a sample and the shader indexes it with the mesh's own coordinates:
                var volume = volumes.Upload(frame, material.Span.Data);

                _chunks.Add(new Chunk
                {
                    Origin = box.Min,
                    Allocation = allocation,
                    Volume = volume,
                    VertexCount = vertices.Count,
                    IndexCount = indexScratch.Length
                });

                _vertexCount += vertices.Count;
                _indexCount += indexScratch.Length;
                _solidVoxels += CountSolid(content.Span);
                CountMaterials(content.Span, material.Span, materialCounts, ref interiorCount);
            }
        }

        // Slot order is not page order, and the submission loop needs the pages contiguous:
        // A run breaks when either buffer changes, so ordering groups the chunks that share a pair.
        _chunks.Sort(CompareChunksByBuffer);
        Console.WriteLine($"World: {_chunks.Count} chunk(s), {_chunksX * _chunkCells * VoxelSize} x {_chunksZ * _chunkCells * VoxelSize} m, {_vertexCount} verts, {_indexCount} indices, {_solidVoxels} solid voxels");
        Console.WriteLine($"Materials: rock={materialCounts[SurfaceMaterial.Rock]}, ice={materialCounts[SurfaceMaterial.Ice]}, snow={materialCounts[SurfaceMaterial.Snow]} near the surface, {interiorCount} interior sample(s)");
        Console.WriteLine($"Draws: {CountRuns()} run(s) over {CountBufferPairs()} buffer pair(s)");
    }

    /// <summary>
    ///     The arena the material volumes live in. Its extent has to equal the mesh window, so a change of the chunk size replaces it, because an image cannot be resized.
    /// </summary>
    private VolumeArena EnsureVolumeArena(Frame frame, int sizeX, int sizeY, int sizeZ)
    {
        if (_volumes != null && _volumes.SizeX == sizeX && _volumes.SizeY == sizeY && _volumes.SizeZ == sizeZ)
        {
            return _volumes;
        }

        if (_volumes != null)
        {
            // The frames in flight may still read the images of the old extent, so it is retired through the frame rather than destroyed here:
            frame.DisposeLater(_volumes);
        }

        _volumes = new VolumeArena(_app.Factory, new VolumeArenaDescription
        {
            SlotCount = VolumeSlots,
            SizeX = sizeX,
            SizeY = sizeY,
            SizeZ = sizeZ
        });

        return _volumes;
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

    private static int CountSolid(Span3D<byte> content)
    {
        var solid = 0;
        var data = content.Data;

        for (var index = 0; index < data.Length; index++)
        {
            if (data[index] > 127)
            {
                solid++;
            }
        }

        return solid;
    }

    /// <summary>
    ///     Counts the ids of the samples the reconstruction can read, which are the solid ones within a voxel of the surface.
    ///     Anything deeper has a saturated field and no gradient, and the volume's interior is never sampled, so counting it would drown the visible split.
    /// </summary>
    private static void CountMaterials(Span3D<byte> content, Span3D<byte> material, Span<long> counts, ref long interiorCount)
    {
        var contentData = content.Data;
        var materialData = material.Data;

        for (var index = 0; index < materialData.Length; index++)
        {
            var id = materialData[index];

            if (id == SurfaceMaterial.Null)
            {
                continue;
            }

            if (contentData[index] == 255)
            {
                interiorCount++;
                continue;
            }

            if (id < counts.Length)
            {
                counts[id]++;
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
        _volumes?.Dispose();
    }
}