using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.Mathematics;
using WinterGame.Core;
using WinterGame.Utilities;
using WinterGame.World;
using WinterGame.World.Render;
// ReSharper disable ConvertToPrimaryConstructor

// ReSharper disable ForCanBeConvertedToForeach

namespace WinterGame;

/// <summary>
///     Draws the level's sections. It takes their meshes from a <see cref="SectionMeshReceiver"/> into a <see cref="MeshArena"/> with 16-bit indices, and draws them with one indirect draw per pair of arena buffers.
///     A mesh the arena cannot hold throws. Render thread only.
/// </summary>
internal sealed class SectionRenderer : ISectionMeshSink, IDisposable
{
    /// <summary>
    ///     The most vertices a mesh may have.
    /// </summary>
    private const int MaxVertices = ushort.MaxValue + 1;

    /// <summary>
    ///     The commands an argument buffer holds at first. It doubles when a frame draws more sections.
    /// </summary>
    private const int InitialCommandCapacity = 1024;

    private struct DrawnSection
    {
        public Vector3i Origin;
        public MeshAllocation Allocation;
        public int VertexCount;
        public int IndexCount;
    }

    private struct PushConstants
    {
        // ReSharper disable once NotAccessedField.Local
        public Matrix4x4 ViewProj;
    }

    private readonly VulkanResourceFactory _factory;
    private readonly Pipeline _pipeline;
    private readonly MeshArena _arena;

    /// <summary>
    ///     One per frame slot, because a frame writes its commands while the previous frame may still read its own.
    /// </summary>
    private readonly VkBuffer?[] _argBuffers;

    private readonly Dictionary<ulong, DrawnSection> _sections = [];

    /// <summary>
    ///     The sections ordered so that the ones sharing a pair of buffers are contiguous, rebuilt when the set changes.
    /// </summary>
    private readonly List<DrawnSection> _drawOrder = [];

    private readonly HashSet<(VkBuffer, VkBuffer)> _bufferPairs = [];
    private bool _drawOrderStale;

    /// <summary>
    ///     The frame the uploads and frees are recorded into, set only during <see cref="TakeMeshes"/>.
    /// </summary>
    private Frame? _frame;

    public SectionRenderer(VulkanResourceFactory factory, int framesInFlight)
    {
        _factory = factory;

        _pipeline = factory.CreatePipeline(new PipelineDescription
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

        // Pages are created on first use, so the large classes cost nothing until a mesh needs them, and the largest vertex class is what a 16-bit index can address:
        _arena = new MeshArena(factory, new MeshArenaDescription
        {
            VertexStride = Unsafe.SizeOf<VoxelVertex>(),
            IndexType = IndexType.UInt16,
            VertexClasses =
            [
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 512, SlotsPerPage = 64 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 1024, SlotsPerPage = 64 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 2048, SlotsPerPage = 64 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 4096, SlotsPerPage = 32 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 8192, SlotsPerPage = 8 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 16384, SlotsPerPage = 4 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 32768, SlotsPerPage = 2 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = MaxVertices, SlotsPerPage = 1 }
            ],
            IndexClasses =
            [
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 2048, SlotsPerPage = 64 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 4096, SlotsPerPage = 64 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 8192, SlotsPerPage = 64 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 16384, SlotsPerPage = 64 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 32768, SlotsPerPage = 16 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 65536, SlotsPerPage = 8 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 131072, SlotsPerPage = 4 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 262144, SlotsPerPage = 2 },
                new MeshArenaDescription.BufferClass { ElementsPerSlot = 524288, SlotsPerPage = 1 }
            ]
        });

        _argBuffers = new VkBuffer?[framesInFlight];
    }

    public int SectionCount => _sections.Count;

    public long VertexCount { get; private set; }

    public long IndexCount { get; private set; }

    /// <summary>
    ///     The indirect draws the last <see cref="Draw"/> issued, one per run of sections sharing a pair of buffers.
    /// </summary>
    public int RunCount { get; private set; }

    /// <summary>
    ///     The distinct pairs of buffers the drawn sections use. It equals <see cref="RunCount"/> when the ordering keeps every pair in one run.
    /// </summary>
    public int BufferPairCount { get; private set; }

    public MeshArenaStats GetArenaStats()
    {
        return _arena.GetStats();
    }

    /// <summary>
    ///     Takes the finished meshes and the removals from <paramref name="receiver"/>, recording the uploads into <paramref name="frame"/>, which must have no pass open.
    /// </summary>
    public void TakeMeshes(Frame frame, SectionMeshReceiver receiver)
    {
        WgAssert.DebugIsOnRenderThread();

        _frame = frame;

        try
        {
            receiver.Update(this);
        }
        finally
        {
            _frame = null;
        }
    }

    void ISectionMeshSink.SetMesh(SectionMeshResult mesh)
    {
        var frame = GetFrame();

        FreeMesh(frame, mesh.Position);

        if (mesh.Indices.IsEmpty)
        {
            return;
        }

        if (mesh.Vertices.Length > MaxVertices)
        {
            throw new InvalidOperationException($"Section {mesh.Position} has {mesh.Vertices.Length} vertices, more than 16-bit indices can address.");
        }

        // Throws when the mesh is larger than the largest class:
        var allocation = _arena.Allocate(mesh.Vertices.Length, mesh.Indices.Length);

        var vertexBytes = MemoryMarshal.AsBytes(mesh.Vertices);
        var vertexStaging = frame.AllocateVertex(vertexBytes.Length);
        vertexBytes.CopyTo(vertexStaging.Data);
        frame.CopyBuffer(vertexStaging, allocation.Vertices.Buffer, (ulong)allocation.Vertices.BaseAddress);

        var indexStaging = frame.AllocateVertex(mesh.Indices.Length * sizeof(ushort));
        var indices = MemoryMarshal.Cast<byte, ushort>(indexStaging.Data);

        for (var index = 0; index < mesh.Indices.Length; index++)
        {
            indices[index] = (ushort)mesh.Indices[index];
        }

        frame.CopyBuffer(indexStaging, allocation.Indices.Buffer, (ulong)allocation.Indices.BaseAddress);

        _sections.Add(mesh.Position.GetKey(), new DrawnSection
        {
            Origin = mesh.Origin,
            Allocation = allocation,
            VertexCount = mesh.Vertices.Length,
            IndexCount = mesh.Indices.Length
        });

        VertexCount += mesh.Vertices.Length;
        IndexCount += mesh.Indices.Length;
        _drawOrderStale = true;
    }

    void ISectionMeshSink.RemoveMesh(SectionPos position)
    {
        FreeMesh(GetFrame(), position);
    }

    /// <summary>
    ///     Draws every section into <paramref name="pass"/>, which needs a depth attachment.
    /// </summary>
    public void Draw(Frame frame, RenderPassEncoder pass, Matrix4x4 viewProj)
    {
        WgAssert.DebugIsOnRenderThread();

        if (_drawOrderStale)
        {
            RebuildDrawOrder();
        }

        RunCount = 0;

        if (_drawOrder.Count == 0)
        {
            return;
        }

        pass.SetPipeline(_pipeline);
        pass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);
        pass.SetScissor(0, 0, (uint)frame.Extent.X, (uint)frame.Extent.Y);

        pass.PushConstants(new PushConstants
        {
            ViewProj = viewProj
        });

        var records = frame.AllocateStorage(_drawOrder.Count * Unsafe.SizeOf<DrawRecord>());
        var recordSpan = MemoryMarshal.Cast<byte, DrawRecord>(records.Data);

        pass.BindStorageBuffer("draws", records);

        var args = GetArgBuffer(frame, _drawOrder.Count);
        var argSpan = MemoryMarshal.Cast<byte, DrawIndexedIndirectCommand>(args.MappedData);
        VkBuffer? vertexBuffer = null;
        VkBuffer? indexBuffer = null;
        var runFirstCommand = 0;

        for (var index = 0; index < _drawOrder.Count; index++)
        {
            var section = _drawOrder[index];

            if (!ReferenceEquals(section.Allocation.Vertices.Buffer, vertexBuffer) ||
                !ReferenceEquals(section.Allocation.Indices.Buffer, indexBuffer))
            {
                DrawRun(pass, args, runFirstCommand, index - runFirstCommand);

                vertexBuffer = section.Allocation.Vertices.Buffer;
                indexBuffer = section.Allocation.Indices.Buffer;
                runFirstCommand = index;
                pass.SetVertexBuffer(vertexBuffer);
                pass.SetIndexBuffer(indexBuffer, IndexType.UInt16);
            }

            recordSpan[index] = new DrawRecord
            {
                Origin = new Vector3(section.Origin.X, section.Origin.Y, section.Origin.Z),
                VoxelSize = WorldConstants.VoxelSize
            };

            argSpan[index] = new DrawIndexedIndirectCommand
            {
                IndexCount = (uint)section.IndexCount,
                InstanceCount = 1,
                FirstIndex = (uint)section.Allocation.Indices.ElementOffset,
                VertexOffset = section.Allocation.Vertices.ElementOffset,
                FirstInstance = (uint)index
            };
        }

        DrawRun(pass, args, runFirstCommand, _drawOrder.Count - runFirstCommand);
    }

    public void Dispose()
    {
        for (var slot = 0; slot < _argBuffers.Length; slot++)
        {
            _argBuffers[slot]?.Dispose();
            _argBuffers[slot] = null;
        }

        _pipeline.Dispose();
        _arena.Dispose();
    }

    private Frame GetFrame()
    {
        return _frame ?? throw new InvalidOperationException("Meshes are only taken inside TakeMeshes, which records their uploads into its frame.");
    }

    private void FreeMesh(Frame frame, SectionPos position)
    {
        if (!_sections.Remove(position.GetKey(), out var section))
        {
            return;
        }

        _arena.MarkFree(frame, section.Allocation);

        VertexCount -= section.VertexCount;
        IndexCount -= section.IndexCount;
        _drawOrderStale = true;
    }

    private void RebuildDrawOrder()
    {
        _drawOrder.Clear();
        _bufferPairs.Clear();

        foreach (var section in _sections.Values)
        {
            _drawOrder.Add(section);
            _bufferPairs.Add((section.Allocation.Vertices.Buffer, section.Allocation.Indices.Buffer));
        }

        // Slot order is not page order, and a run breaks when either buffer changes, so ordering groups the sections that share a pair:
        _drawOrder.Sort(CompareByBuffer);

        BufferPairCount = _bufferPairs.Count;
        _drawOrderStale = false;
    }

    /// <summary>
    ///     The frame slot's argument buffer, replaced by a larger one when it holds fewer than <paramref name="commandCount"/> commands.
    /// </summary>
    private VkBuffer GetArgBuffer(Frame frame, int commandCount)
    {
        var current = _argBuffers[frame.SlotIndex];

        if (current != null && current.Size >= commandCount * DrawIndexedIndirectCommand.SizeInBytes)
        {
            return current;
        }

        if (current != null)
        {
            frame.DisposeLater(current);
        }

        var capacity = InitialCommandCapacity;

        while (capacity < commandCount)
        {
            capacity *= 2;
        }

        var buffer = _factory.CreateBuffer(new BufferDescription
        {
            Size = capacity * DrawIndexedIndirectCommand.SizeInBytes,
            Usage = BufferUsage.IndirectHostVisible
        });

        _argBuffers[frame.SlotIndex] = buffer;

        return buffer;
    }

    /// <summary>
    ///     Issues one indirect draw for a run of commands, or nothing when the run is empty.
    /// </summary>
    private void DrawRun(RenderPassEncoder pass, VkBuffer args, int firstCommand, int commandCount)
    {
        if (commandCount <= 0)
        {
            return;
        }

        pass.DrawIndexedIndirect(args, (ulong)firstCommand * DrawIndexedIndirectCommand.SizeInBytes, (uint)commandCount);
        RunCount++;
    }

    /// <summary>
    ///     Orders the sections so that every run of sections sharing a pair of buffers is contiguous.
    /// </summary>
    private static int CompareByBuffer(DrawnSection left, DrawnSection right)
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
}
