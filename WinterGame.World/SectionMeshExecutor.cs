using System.Runtime.InteropServices;
using Graphics2.Data;
using Graphics2.Mathematics;
using WinterGame.Jobs;
using WinterGame.Utilities;
using WinterGame.World.Render;

namespace WinterGame.World;

/// <summary>
///     A meshed section, read out of its slot, so it is valid until the slot is freed.
///     The vertex positions are in samples from <see cref="Origin"/>, which is the window's lowest sample.
/// </summary>
public readonly ref struct SectionMeshResult(SectionPos position, Vector3i origin, ReadOnlySpan<VoxelVertex> vertices, ReadOnlySpan<int> indices)
{
    public readonly SectionPos Position = position;
    public readonly Vector3i Origin = origin;
    public readonly ReadOnlySpan<VoxelVertex> Vertices = vertices;
    public readonly ReadOnlySpan<int> Indices = indices;
}

/// <summary>
///     Meshes one section per work item from a window of samples the game thread fills.
///     Every slot owns its window and its output lists, which pass from the game thread to a worker with the publish and from the worker to the render thread with the job's completion, so no slot is shared and none needs a lock.
///     The mesher state is per worker, indexed by <see cref="WorkExecutionContext.ThreadId"/> and made when the worker meshes its first section.
///     <see cref="Allocate"/> and the window belong to the game thread, <see cref="GetResult"/> to the render thread, and <see cref="Free"/> to whichever thread holds the slot.
/// </summary>
public sealed class SectionMeshExecutor : IWorkExecutor
{
    private static readonly Vector3i CellCount = new(SectionPos.SectionSize);

    private readonly SurfaceFairingSettings _fairing;
    private readonly WorkExecutorTransformScratch<SectionPos, SectionPos?> _slots;
    private readonly Memory3D<byte>[] _content;
    private readonly Memory3D<byte>[] _material;
    private readonly List<VoxelVertex>[] _vertices;
    private readonly List<int>[] _indices;
    private readonly MultiLabelSurfaceNetsDescription[] _descriptions;
    private readonly MultiLabelSurfaceNetsStateStorage?[] _mesherStates;

    private int _freeSlotCount;

    public SectionMeshExecutor(SectionMeshingDescription description, int workerCount)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(description.SlotCount, 1, nameof(description.SlotCount));
        ArgumentOutOfRangeException.ThrowIfNegative(description.Fairing.Iterations, nameof(description.Fairing.Iterations));

        _fairing = description.Fairing;

        var window = description.GetWindow();

        // A section is reported once it and its 26 neighbors are ready, so the window has to stay inside them:
        if (window.Min.X < -SectionPos.SectionSize || window.Max.X >= 2 * SectionPos.SectionSize)
        {
            throw new ArgumentException($"{_fairing.Iterations} relaxation iterations read past the sections next to the one meshed.", nameof(description));
        }

        WindowOffset = window.Min;
        WindowSize = window.Max - window.Min + 1;

        var slotCount = description.SlotCount;

        _slots = new WorkExecutorTransformScratch<SectionPos, SectionPos?>(new SectionPos[slotCount], clearInputToDefault: false);
        _content = new Memory3D<byte>[slotCount];
        _material = new Memory3D<byte>[slotCount];
        _vertices = new List<VoxelVertex>[slotCount];
        _indices = new List<int>[slotCount];
        _descriptions = new MultiLabelSurfaceNetsDescription[slotCount];

        for (var slot = 0; slot < slotCount; slot++)
        {
            _content[slot] = Memory3D<byte>.Allocate(WindowSize.X, WindowSize.Y, WindowSize.Z);
            _material[slot] = Memory3D<byte>.Allocate(WindowSize.X, WindowSize.Y, WindowSize.Z);
            _vertices[slot] = [];
            _indices[slot] = [];

            _descriptions[slot] = new MultiLabelSurfaceNetsDescription
            {
                Content = _content[slot],
                Material = _material[slot],
                IsoLevel = WorldConstants.ContentIsoLevel,
                CellMin = -window.Min,
                CellCount = CellCount,
                Vertices = _vertices[slot],
                Indices = _indices[slot],
                Fairing = _fairing
            };
        }

        _mesherStates = new MultiLabelSurfaceNetsStateStorage?[workerCount];
        _freeSlotCount = slotCount;
        SlotCount = slotCount;
    }

    public int SlotCount { get; }

    /// <summary>
    ///     The window's lowest sample, relative to the origin of the section meshed.
    /// </summary>
    public Vector3i WindowOffset { get; }

    /// <summary>
    ///     Samples in the window on each axis.
    /// </summary>
    public Vector3i WindowSize { get; }

    /// <summary>
    ///     Slots that <see cref="Allocate"/> can still hand out. The render thread frees slots, so it only grows between two reads on the game thread.
    /// </summary>
    public int FreeSlotCount => Volatile.Read(ref _freeSlotCount);

    /// <summary>
    ///     The window's lowest sample in world samples, where the result's vertex positions start.
    /// </summary>
    public Vector3i GetWindowMin(SectionPos position)
    {
        return position.GetOrigin() + WindowOffset;
    }

    /// <summary>
    ///     Takes a slot for meshing the section at <see cref="position"/>, whose window the caller then fills before publishing the slot.
    /// </summary>
    public int Allocate(SectionPos position)
    {
        WgAssert.DebugIsOnGameThread();

        _slots.AllocateInputSlot(out var slot) = position;
        Interlocked.Decrement(ref _freeSlotCount);

        return slot;
    }

    /// <summary>
    ///     The content window of a slot, which holds <see cref="WindowSize"/> samples from <see cref="GetWindowMin"/>.
    /// </summary>
    public Span3D<byte> GetContentWindow(int slot)
    {
        WgAssert.DebugIsOnGameThread();

        return _content[slot].Span;
    }

    /// <summary>
    ///     The material window of a slot, the same shape as <see cref="GetContentWindow"/>.
    /// </summary>
    public Span3D<byte> GetMaterialWindow(int slot)
    {
        WgAssert.DebugIsOnGameThread();

        return _material[slot].Span;
    }

    /// <summary>
    ///     The mesh of a slot whose job has completed, valid until <see cref="Free"/>.
    ///     A section with no surface has no vertices and no indices.
    /// </summary>
    public SectionMeshResult GetResult(int slot)
    {
        WgAssert.DebugIsOnRenderThread();

        var position = _slots.GetOutput(slot) ?? throw new InvalidOperationException($"Slot {slot} has not been meshed.");

        return new SectionMeshResult(position, GetWindowMin(position), CollectionsMarshal.AsSpan(_vertices[slot]), CollectionsMarshal.AsSpan(_indices[slot]));
    }

    /// <summary>
    ///     Returns a slot, which is legal once its job has completed or when it was never published.
    /// </summary>
    public void Free(int slot)
    {
        _vertices[slot].Clear();
        _indices[slot].Clear();
        _slots.Free(slot);
        Interlocked.Increment(ref _freeSlotCount);
    }

    public void Execute(in WorkExecutionContext context, int item)
    {
        var position = _slots.GetInputSlot(item);

        // Each worker meshes on its own state, and only that worker ever touches it:
        var state = _mesherStates[context.ThreadId] ??= new MultiLabelSurfaceNetsStateStorage(CellCount, _fairing.Iterations);

        MultiLabelSurfaceNets.Mesh(state, _descriptions[item]);
        _slots.WriteOutput(item, position);
    }
}
