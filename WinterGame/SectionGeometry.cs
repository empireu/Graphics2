using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.Mathematics;
using WinterGame.Core;
using WinterGame.Utilities;
using WinterGame.World;
using WinterGame.World.Level.Meshing;
using WinterGame.World.Mesher;
using WinterGame.World.Storage;

// ReSharper disable ConvertToPrimaryConstructor
// ReSharper disable ForCanBeConvertedToForeach

namespace WinterGame;

/// <summary>
///     The level's section meshes on the GPU and the lists that draw them. It takes the meshes from a <see cref="SectionMeshReceiver"/> into a <see cref="MeshArena"/> with 16-bit indices, and each frame writes one draw record per section, which every view shares, and for each view one indirect command per section inside its frustum, drawn with one indirect draw per pair of arena buffers.
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

        /// <summary>
        ///     The lowest corner of the box around the mesh's vertices, in metres.
        /// </summary>
        public Vector3 BoundsMin;

        public Vector3 BoundsMax;
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
    ///     The draw records <see cref="Prepare"/> wrote and the argument buffer it sized, valid only in the frame it ran for.
    /// </summary>
    private RingAllocation _records;

    private VkBuffer? _args;

    /// <summary>
    ///     The commands the frame's views have written so far, each view after the one before.
    /// </summary>
    private int _commandCount;

    /// <summary>
    ///     The views <see cref="Prepare"/> sized the argument buffer for that have not been written yet.
    /// </summary>
    private int _viewsLeft;

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
    ///     The distinct pairs of buffers the sections use. A view of every section has as many runs when the ordering keeps every pair in one run.
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

        var positionMin = new Vector3(float.MaxValue);
        var positionMax = new Vector3(float.MinValue);

        for (var index = 0; index < mesh.Vertices.Length; index++)
        {
            positionMin = Vector3.Min(positionMin, mesh.Vertices[index].Position);
            positionMax = Vector3.Max(positionMax, mesh.Vertices[index].Position);
        }

        // The same arithmetic as the vertex shader's, so a vertex lies inside its bounds:
        var origin = new Vector3(mesh.Origin.X, mesh.Origin.Y, mesh.Origin.Z);

        _sections.Add(mesh.Position.GetKey(), new DrawnSection
        {
            Origin = mesh.Origin,
            Allocation = allocation,
            VertexCount = mesh.Vertices.Length,
            IndexCount = mesh.Indices.Length,
            BoundsMin = (origin + positionMin) * WorldConstants.VoxelSize,
            BoundsMax = (origin + positionMax) * WorldConstants.VoxelSize
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
    ///     Writes this frame's draw records, which every view shares, and sizes the argument buffer for <paramref name="viewCount"/> calls of <see cref="AddView"/>. Call it once per frame, before the first <see cref="AddView"/>.
    /// </summary>
    public void Prepare(Frame frame, int viewCount)
    {
        WgAssert.DebugIsOnRenderThread();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(viewCount);

        if (_drawOrderStale)
        {
            RebuildDrawOrder();
        }

        _runs.Clear();
        _commandCount = 0;
        _viewsLeft = viewCount;

        if (_drawOrder.Count == 0)
        {
            return;
        }

        _records = frame.AllocateStorage(_drawOrder.Count * Unsafe.SizeOf<DrawRecord>());
        _args = GetArgBuffer(frame, viewCount * _drawOrder.Count);

        var recordSpan = MemoryMarshal.Cast<byte, DrawRecord>(_records.Data);
        var drawOrder = CollectionsMarshal.AsSpan(_drawOrder);

        for (var index = 0; index < drawOrder.Length; index++)
        {
            var origin = drawOrder[index].Origin;

            recordSpan[index] = new DrawRecord
            {
                Origin = new Vector3(origin.X, origin.Y, origin.Z),
                VoxelSize = WorldConstants.VoxelSize
            };
        }
    }

    /// <summary>
    ///     Writes the commands of the sections whose bounds reach into <paramref name="frustum"/>, or of every section when it is null, and returns them as one view's list.
    ///     Each view has its own range of the argument buffer, because the GPU reads the commands only when it runs the frame. Only valid in the frame <see cref="Prepare"/> last ran for.
    /// </summary>
    public SectionDrawList AddView(Frustum? frustum)
    {
        WgAssert.DebugIsOnRenderThread();

        if (_viewsLeft == 0)
        {
            throw new InvalidOperationException("Prepare sized this frame's argument buffer for fewer views.");
        }

        _viewsLeft--;

        var firstRun = _runs.Count;
        var firstCommand = _commandCount;

        if (_drawOrder.Count == 0)
        {
            return new SectionDrawList(firstRun, 0, 0, 0);
        }

        var cull = frustum.HasValue;
        var volume = frustum.GetValueOrDefault();
        var drawOrder = CollectionsMarshal.AsSpan(_drawOrder);
        var argSpan = MemoryMarshal.Cast<byte, DrawIndexedIndirectCommand>(_args!.MappedData);
        var runFirstCommand = firstCommand;
        var runFirstSection = -1;
        var indexCount = 0L;

        for (var index = 0; index < drawOrder.Length; index++)
        {
            ref readonly var section = ref drawOrder[index];

            if (cull && !volume.Intersects(section.BoundsMin, section.BoundsMax))
            {
                continue;
            }

            if (runFirstSection < 0)
            {
                runFirstSection = index;
            }
            else if (!SharesBuffers(section, drawOrder[runFirstSection]))
            {
                AddRun(drawOrder[runFirstSection], runFirstCommand, _commandCount - runFirstCommand);
                runFirstCommand = _commandCount;
                runFirstSection = index;
            }

            argSpan[_commandCount] = new DrawIndexedIndirectCommand
            {
                IndexCount = (uint)section.IndexCount,
                InstanceCount = 1,
                FirstIndex = (uint)section.Allocation.Indices.ElementOffset,
                VertexOffset = section.Allocation.Vertices.ElementOffset,
                FirstInstance = (uint)index
            };

            _commandCount++;
            indexCount += section.IndexCount;
        }

        if (runFirstSection >= 0)
        {
            AddRun(drawOrder[runFirstSection], runFirstCommand, _commandCount - runFirstCommand);
        }

        return new SectionDrawList(firstRun, _runs.Count - firstRun, _commandCount - firstCommand, indexCount);
    }

    /// <summary>
    ///     Draws the sections of <paramref name="list"/> with the pipeline <paramref name="pass"/> has set, which reads <see cref="VoxelVertex"/> and declares the draw records as "draws", indexed by the command's first instance.
    ///     Only valid in the frame <see cref="AddView"/> wrote the list for.
    /// </summary>
    public void Draw(RenderPassEncoder pass, SectionDrawList list)
    {
        WgAssert.DebugIsOnRenderThread();

        if (list.RunCount == 0)
        {
            return;
        }

        pass.BindStorageBuffer("draws", _records);

        for (var index = list.FirstRun; index < list.FirstRun + list.RunCount; index++)
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

    private void AddRun(in DrawnSection first, int firstCommand, int commandCount)
    {
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

    private static bool SharesBuffers(in DrawnSection left, in DrawnSection right)
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
