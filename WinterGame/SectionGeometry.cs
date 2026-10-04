using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
///     The level's section meshes on the GPU and the list that draws them. It takes the meshes from a <see cref="SectionMeshReceiver"/> into a <see cref="MeshArena"/> with 16-bit indices, and each frame writes one draw record and one indirect command per section, which every pass of the frame then draws with one indirect draw per pair of arena buffers.
///     It owns no pipeline, so a pass draws it with its own. A mesh the arena cannot hold throws. Render thread only.
/// </summary>
internal sealed class SectionGeometry : ISectionMeshSink, IDisposable
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

    /// <summary>
    ///     A run of commands whose sections share a pair of buffers, drawn with one indirect draw.
    /// </summary>
    private struct DrawRun
    {
        public VkBuffer Vertices;
        public VkBuffer Indices;
        public int FirstCommand;
        public int CommandCount;
    }

    private readonly VulkanResourceFactory _factory;
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
    private readonly List<DrawRun> _runs = [];
    private bool _drawOrderStale;

    /// <summary>
    ///     The frame the uploads and frees are recorded into, set only during <see cref="TakeMeshes"/>.
    /// </summary>
    private Frame? _frame;

    /// <summary>
    ///     The draw records and commands <see cref="Prepare"/> wrote, valid only in the frame it wrote them for.
    /// </summary>
    private RingAllocation _records;

    private VkBuffer? _args;

    public SectionGeometry(VulkanResourceFactory factory, int framesInFlight)
    {
        _factory = factory;

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
    ///     The indirect draws each pass issues this frame, one per run of sections sharing a pair of buffers.
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
    ///     Writes this frame's draw records and commands, which every <see cref="Draw"/> in the frame reads. Call it once per frame, before the first pass that draws the sections.
    /// </summary>
    public void Prepare(Frame frame)
    {
        WgAssert.DebugIsOnRenderThread();

        if (_drawOrderStale)
        {
            RebuildDrawOrder();
        }

        _runs.Clear();
        RunCount = 0;

        if (_drawOrder.Count == 0)
        {
            return;
        }

        _records = frame.AllocateStorage(_drawOrder.Count * Unsafe.SizeOf<DrawRecord>());
        _args = GetArgBuffer(frame, _drawOrder.Count);

        var recordSpan = MemoryMarshal.Cast<byte, DrawRecord>(_records.Data);
        var argSpan = MemoryMarshal.Cast<byte, DrawIndexedIndirectCommand>(_args.MappedData);
        var runFirstCommand = 0;

        for (var index = 0; index < _drawOrder.Count; index++)
        {
            var section = _drawOrder[index];

            if (index > 0 && !SharesBuffers(section, _drawOrder[index - 1]))
            {
                AddRun(runFirstCommand, index - runFirstCommand);
                runFirstCommand = index;
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

        AddRun(runFirstCommand, _drawOrder.Count - runFirstCommand);
        RunCount = _runs.Count;
    }

    /// <summary>
    ///     Draws every section with the pipeline <paramref name="pass"/> has set, which reads <see cref="VoxelVertex"/> and declares the draw records as "draws", indexed by the command's first instance.
    ///     Only valid in the frame <see cref="Prepare"/> last ran for.
    /// </summary>
    public void Draw(RenderPassEncoder pass)
    {
        WgAssert.DebugIsOnRenderThread();

        if (_runs.Count == 0)
        {
            return;
        }

        pass.BindStorageBuffer("draws", _records);

        for (var index = 0; index < _runs.Count; index++)
        {
            var run = _runs[index];

            pass.SetVertexBuffer(run.Vertices);
            pass.SetIndexBuffer(run.Indices, IndexType.UInt16);
            pass.DrawIndexedIndirect(_args!, (ulong)run.FirstCommand * DrawIndexedIndirectCommand.SizeInBytes, (uint)run.CommandCount);
        }
    }

    public void Dispose()
    {
        for (var slot = 0; slot < _argBuffers.Length; slot++)
        {
            _argBuffers[slot]?.Dispose();
            _argBuffers[slot] = null;
        }

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

    private void AddRun(int firstCommand, int commandCount)
    {
        var first = _drawOrder[firstCommand];

        _runs.Add(new DrawRun
        {
            Vertices = first.Allocation.Vertices.Buffer,
            Indices = first.Allocation.Indices.Buffer,
            FirstCommand = firstCommand,
            CommandCount = commandCount
        });
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

    private static bool SharesBuffers(DrawnSection left, DrawnSection right)
    {
        return ReferenceEquals(left.Allocation.Vertices.Buffer, right.Allocation.Vertices.Buffer) &&
               ReferenceEquals(left.Allocation.Indices.Buffer, right.Allocation.Indices.Buffer);
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
