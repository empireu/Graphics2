using Graphics2.Data;
using WinterGame.Jobs;
using WinterGame.Utilities;
using WinterGame.World.Storage;

namespace WinterGame.World;

/// <summary>
///     Generates one section per work item into bricks.
///     Every slot owns a <see cref="SectionArena"/> sized to one section, and the worker running a slot writes its arena without locks and the main thread reads it once the job has completed.
///     The generation buffers are per worker and indexed by <see cref="WorkExecutionContext.ThreadId"/>.
///     <see cref="Allocate"/>, <see cref="GetResult"/> and <see cref="Free"/> are main thread only.
/// </summary>
public sealed class SectionGenerationExecutor : IWorkExecutor
{
    public const int DefaultSlotCount = 10;

    private readonly WorkExecutorTransformScratch<SectionPos, Section?> _slots;
    private readonly SectionArena[] _slotArenas;
    private readonly TerrainGraphSource[] _sources;
    private readonly Memory3D<byte>[] _content;
    private readonly Memory3D<byte>[] _material;

    public SectionGenerationExecutor(TerrainGraph graph, int workerCount, int slotCount = DefaultSlotCount)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, 1);

        _slots = new WorkExecutorTransformScratch<SectionPos, Section?>(new SectionPos[slotCount], clearInputToDefault: false);
        _slotArenas = new SectionArena[slotCount];

        for (var slot = 0; slot < slotCount; slot++)
        {
            _slotArenas[slot] = new SectionArena(descriptorArraysPerPage: 1, voxelArraysPerPage: Section.DescriptorsPerSection);
        }

        _sources = new TerrainGraphSource[workerCount];
        _content = new Memory3D<byte>[workerCount];
        _material = new Memory3D<byte>[workerCount];

        for (var worker = 0; worker < workerCount; worker++)
        {
            _sources[worker] = new TerrainGraphSource(graph);
            _content[worker] = Memory3D<byte>.Allocate(SectionPos.SectionSize, SectionPos.SectionSize, SectionPos.SectionSize);
            _material[worker] = Memory3D<byte>.Allocate(SectionPos.SectionSize, SectionPos.SectionSize, SectionPos.SectionSize);
        }

        FreeSlotCount = slotCount;
    }

    public int SlotCount => _slotArenas.Length;

    /// <summary>
    ///     Slots that <see cref="Allocate"/> can still hand out.
    /// </summary>
    public int FreeSlotCount { get; private set; }

    /// <summary>
    ///     Takes a slot for generating the section at <see cref="position"/>, which is the payload slot to publish.
    /// </summary>
    public int Allocate(SectionPos position)
    {
        WgAssert.DebugIsOnGameThread();

        _slots.AllocateInputSlot(out var slot) = position;
        FreeSlotCount--;

        return slot;
    }

    /// <summary>
    ///     The generated section of a slot whose job has completed.
    ///     It lives in the slot's arena, so it is valid until <see cref="Free"/> and has to be copied into a longer lived arena with <see cref="SectionArena.CreateCopy"/>.
    /// </summary>
    public Section GetResult(int slot)
    {
        WgAssert.DebugIsOnGameThread();

        return _slots.GetOutput(slot) ?? throw new InvalidOperationException($"Slot {slot} has no generated section.");
    }

    /// <summary>
    ///     Destroys the slot's section, if one was generated, and returns the slot.
    ///     The slot's job has to have completed, since its worker would otherwise still be writing the arena.
    ///     A slot whose publish was refused has no section, which is why the output can be empty here.
    /// </summary>
    public void Free(int slot)
    {
        WgAssert.DebugIsOnGameThread();

        var section = _slots.GetOutput(slot);

        if (section != null)
        {
            _slotArenas[slot].Destroy(section.Value.Position);
        }

        _slots.Free(slot);
        FreeSlotCount++;
    }

    public void Execute(in WorkExecutionContext context, int item)
    {
        var position = _slots.GetInputSlot(item);
        var worker = context.ThreadId;

        var min = position.GetOrigin();
        var maxInclusive = min + SectionPos.SectionMask;

        var content = _content[worker].Span;
        var material = _material[worker].Span;

        _sources[worker].Generate(min, maxInclusive, content, material);

        var section = _slotArenas[item].Create(position);

        WriteBricks(section, VoxelChannel.Content, content);
        WriteBricks(section, VoxelChannel.Material, material);

        _slots.WriteOutput(item, section);
    }

    /// <summary>
    ///     Stores a section sized box into a channel, creating the appropriate dense or uniform bricks.
    /// </summary>
    private static void WriteBricks(Section section, VoxelChannel channel, Span3D<byte> source)
    {
        Span<byte> brick = stackalloc byte[Section.VoxelsPerBrick];
        var data = source.Data;

        for (var brickZ = 0; brickZ < Section.BricksPerAxis; brickZ++)
        {
            for (var brickY = 0; brickY < Section.BricksPerAxis; brickY++)
            {
                for (var brickX = 0; brickX < Section.BricksPerAxis; brickX++)
                {
                    var originX = brickX * Section.BrickSize;
                    var originY = brickY * Section.BrickSize;
                    var originZ = brickZ * Section.BrickSize;

                    for (var z = 0; z < Section.BrickSize; z++)
                    {
                        for (var y = 0; y < Section.BrickSize; y++)
                        {
                            var row = originX + (originY + y) * source.SizeX + (originZ + z) * source.SizeX * source.SizeY;

                            data.Slice(row, Section.BrickSize).CopyTo(brick.Slice(Section.GetBrickVoxelIndex(0, y, z), Section.BrickSize));
                        }
                    }

                    var brickIndex = Section.GetBrickIndex(brickX, brickY, brickZ);

                    if (brick.IndexOfAnyExcept(brick[0]) < 0)
                    {
                        section.SetUniform(channel, brickIndex, brick[0]);
                    }
                    else
                    {
                        section.SetDense(channel, brickIndex);
                        brick.CopyTo(section.GetDense(channel, brickIndex).Span);
                    }
                }
            }
        }
    }
}
