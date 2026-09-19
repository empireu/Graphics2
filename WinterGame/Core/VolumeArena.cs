using System.Diagnostics;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
// ReSharper disable LoopCanBeConvertedToQuery

// ReSharper disable ForCanBeConvertedToForeach

namespace WinterGame.Core;

public sealed class VolumeArenaDescription
{
    /// <summary>
    ///     The number of volumes the arena hands out.
    ///     It has to match the array size the shader declares, and it has to cover the volumes the frames in flight can still read as well as the ones replacing them, because a slot returns only once the frame that read it has finished.
    /// </summary>
    public required int SlotCount { get; init; }

    /// <summary>
    ///     The number of texels in a volume on the x axis.
    /// </summary>
    public required int SizeX { get; init; }

    /// <summary>
    ///     The number of texels in a volume on the y axis.
    /// </summary>
    public required int SizeY { get; init; }

    /// <summary>
    ///     The number of texels in a volume on the z axis.
    /// </summary>
    public required int SizeZ { get; init; }

    public TextureFormat Format { get; init; } = TextureFormat.R8Uint;
}

/// <summary>
///     Rented volume. <see cref="Index"/> is what per-draw data carries so the shader can sample it.
/// </summary>
public readonly struct VolumeHandle(VolumeArena arena, uint index) : IEquatable<VolumeHandle>
{
    public readonly VolumeArena Arena = arena;

    public readonly uint Index = index;

    public bool IsEmpty => Arena == null;

    public bool Equals(VolumeHandle other)
    {
        return ReferenceEquals(Arena, other.Arena) && Index == other.Index;
    }

    public override bool Equals(object? obj)
    {
        return obj is VolumeHandle other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Arena, Index);
    }

    public static bool operator ==(VolumeHandle left, VolumeHandle right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(VolumeHandle left, VolumeHandle right)
    {
        return !left.Equals(right);
    }
}

/// <summary>
///     How much of a volume arena is in use.
/// </summary>
public readonly struct VolumeArenaStats(int slotCount, int created, int usedSlots, long bytes)
{
    public readonly int SlotCount = slotCount;

    /// <summary>
    ///     The number of slots an image has been created for, which is what the bytes are paid for.
    /// </summary>
    public readonly int Created = created;

    public readonly int UsedSlots = usedSlots;

    public readonly long Bytes = bytes;
}

/// <summary>
///     Fixed-size volume slots in a bindless texture array.
///     A volume is a 3D image holding one texel per cell of the mesh that samples it, so a shader reads it with the mesh's own local coordinates and needs no material data in the vertex.
///     The images are created on a slot's first use and each slot is registered in the array once, because that descriptor records the image view and is never rewritten; a slot is only handed out again once no frame in flight can still read it.
/// </summary>
public sealed class VolumeArena : IDisposable
{
    /// <summary>
    ///     An upload stages through the geometry ring, whose offsets are aligned to 4 bytes, and a copy into an image requires the buffer offset to be a multiple of the format's texel size.
    /// </summary>
    private const int StagingAlignment = 4;

    private readonly VulkanResourceFactory _factory;
    private readonly Texture?[] _slots;
    private readonly Stack<uint> _freeSlots = [];
    private readonly int _bytesPerVolume;

#if DEBUG
    private readonly HashSet<uint> _inUse = [];
#endif

    public readonly int SlotCount;
    public readonly int SizeX;
    public readonly int SizeY;
    public readonly int SizeZ;
    public readonly TextureFormat Format;

    /// <summary>
    ///     The registry the volumes are bound through, as <c>pass.BindTextureArray("materialVolumes", arena.Array)</c>.
    /// </summary>
    public readonly TextureArray Array;

    public VolumeArena(VulkanResourceFactory factory, VolumeArenaDescription description)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(description.SlotCount);

        if (description.SizeX <= 0 || description.SizeY <= 0 || description.SizeZ <= 0)
        {
            throw new ArgumentException(
                $"A volume has to have a positive extent, but the description has {description.SizeX}x{description.SizeY}x{description.SizeZ}.",
                nameof(description)
            );
        }

        var texelSize = description.Format.TexelSize();

        if (texelSize > StagingAlignment)
        {
            throw new ArgumentException(
                $"A {description.Format} texel is {texelSize} bytes and the geometry ring aligns its offsets to {StagingAlignment}, while a copy into an image requires the buffer offset to be a multiple of the texel size.",
                nameof(description)
            );
        }

        _factory = factory;
        SlotCount = description.SlotCount;
        SizeX = description.SizeX;
        SizeY = description.SizeY;
        SizeZ = description.SizeZ;
        Format = description.Format;
        _bytesPerVolume = checked(SizeX * SizeY * SizeZ * texelSize);

        // The array size is the shader's declared array size, which BindTextureArray checks against the registry's capacity:
        Array = factory.CreateTextureArray((uint)description.SlotCount, TextureType.Texture3D);

        _slots = new Texture?[SlotCount];

        for (var slot = SlotCount - 1; slot >= 0; slot--)
        {
            _freeSlots.Push((uint)slot);
        }
    }

    /// <summary>
    ///     Takes a free slot and writes the whole volume in one call.
    ///     A slot the arena has not used before gets its image here, so the caller does not know or care whether the slot is fresh.
    /// </summary>
    public VolumeHandle Upload(Frame frame, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (data.Length != _bytesPerVolume)
        {
            throw new ArgumentException(
                $"A volume of {SizeX}x{SizeY}x{SizeZ} {Format} texels is {_bytesPerVolume} bytes, but the upload is {data.Length} bytes.",
                nameof(data)
            );
        }

        if (!_freeSlots.TryPop(out var slot))
        {
            throw new InvalidOperationException($"All {SlotCount} slots in volume arena are in use.");
        }

#if DEBUG
        Debug.Assert(_inUse.Add(slot));
#endif

        var texture = _slots[slot];
        var isNew = texture == null;

        if (texture == null)
        {
            texture = _factory.CreateTexture(new TextureDescription
            {
                Width = (uint)SizeX,
                Height = (uint)SizeY,
                Depth = (uint)SizeZ,
                Format = Format,
                Usage = TextureUsage.Sampled | TextureUsage.TransferDestination,
                Type = TextureType.Texture3D
            });

            _slots[slot] = texture;
        }

        var staging = frame.AllocateVertex(data.Length);
        
        data.CopyTo(staging.Data);
        
        frame.CopyBufferToImage(
            staging.Buffer,
            staging.Offset,
            texture, 
            0, 0, 0,
            (uint)SizeX, (uint)SizeY, (uint)SizeZ
        );

        // A fresh image is registered only after the copy, because the descriptor declares the layout the copy's flush will put it in:
        if (isNew)
        {
            RegisterSlot(slot, texture);
        }

        return new VolumeHandle(this, slot);
    }

    /// <summary>
    ///     Marks the volume as freed. The slot returns to the arena once the frame that could still read it has finished.
    /// </summary>
    public void MarkFree(Frame frame, in VolumeHandle volume)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (!ReferenceEquals(volume.Arena, this))
        {
            throw new ArgumentException("The volume was rented from another arena.", nameof(volume));
        }

#if DEBUG
        Debug.Assert(_inUse.Remove(volume.Index));
#endif

        frame.DisposeLater(new FreeSlotReturnHandler(_freeSlots, volume.Index));
    }

    /// <summary>
    ///     What the arena is holding, for the debug overlay.
    /// </summary>
    public VolumeArenaStats GetStats()
    {
        var created = 0;

        for (var slot = 0; slot < _slots.Length; slot++)
        {
            if (_slots[slot] != null)
            {
                created++;
            }
        }

        return new VolumeArenaStats(SlotCount, created, SlotCount - _freeSlots.Count, (long)created * _bytesPerVolume);
    }

    /// <summary>
    ///     A slot's array index is what the shader samples with, so it has to be the index the arena tracks.
    ///     Both are handed out in ascending order and a slot is never removed from the array, which is what keeps them equal.
    /// </summary>
    private void RegisterSlot(uint slot, Texture texture)
    {
        var index = Array.Add(texture);

        if (index != slot)
        {
            throw new InvalidOperationException($"The volume array handed out index {index} for slot {slot}, and a slot's array index is what the shader samples with.");
        }
    }

    private sealed class FreeSlotReturnHandler(Stack<uint> freeSlots, uint slot) : IDisposable
    {
        public void Dispose()
        {
            freeSlots.Push(slot);
        }
    }

    public void Dispose()
    {
        foreach (var texture in _slots)
        {
            texture?.Dispose();
        }

        Array.Dispose();
    }
}
