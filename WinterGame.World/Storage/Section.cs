using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
// ReSharper disable ForCanBeConvertedToForeach

namespace WinterGame.World.Storage;

/// <summary>
///     What a brick's stored data is.
/// </summary>
public enum BrickType : byte
{
    /// <summary>
    ///     Invalid descriptor.
    /// </summary>
    None = 0,
    /// <summary>
    ///     Nothing is stored; the data is provided by the world generator.
    /// </summary>
    Provider = 1,
    /// <summary>
    ///     One value across the whole brick.
    /// </summary>
    Uniform = 2,

    /// <summary>
    ///     A dense grid of samples.
    /// </summary>
    Dense = 3
}

/// <summary>
///     Which of a brick's two stored fields a type or a value describes.
/// </summary>
public enum VoxelChannel : byte
{
    /// <summary>
    ///     The normalized signed distance, where larger values are closer to solid.
    /// </summary>
    Content = 0,

    /// <summary>
    ///     An index into the material table, with 255 meaning no material.
    /// </summary>
    Material = 1
}

public readonly struct Section
{
    private const int BrickTypeWidthLog = 1;
    private const int BitsPerBrickType = 1 << BrickTypeWidthLog;
    
    private const uint BrickTypeMask = (1u << BitsPerBrickType) - 1;
    
    /// <summary>
    ///     6 is <c>log2(64)</c> (the segment size in bits).
    /// </summary>
    private const int IndexToSegmentShift = 6 - BrickTypeWidthLog;
    
    /// <summary>
    ///     Number of brick types stored per 64-bit segment.
    /// </summary>
    public const int BrickTypesPerSegment = 64 / BitsPerBrickType;
    
    /// <summary>
    ///     Number of channels a brick stores a type and a value for.
    /// </summary>
    public const int VoxelChannelCount = 2;
    
    /// <summary>
    ///     Number of 64-bit segments needed to store one channel's brick types.
    /// </summary>
    public const int BrickTypeSegmentsPerChannel = (Bricks + (BrickTypesPerSegment - 1)) >> IndexToSegmentShift;
    
    /// <summary>
    ///     Number of 64-bit segments one section owns in the slab: one bitmap per channel.
    /// </summary>
    public const int BrickTypeSegmentsPerSection = BrickTypeSegmentsPerChannel * VoxelChannelCount;
    
    public const int BrickLog = 2;
    public const int BrickSize = 1 << BrickLog;
    public const int BricksPerAxis = SectionPos.SectionSize / BrickSize;
    public const int Bricks = BricksPerAxis * BricksPerAxis * BricksPerAxis;

    public readonly SectionPos Pos;

    private readonly ulong[] _brickTypeStorage;
    private readonly int _brickTypeStorageOffset;
    
    public Section(SectionPos pos, ulong[] brickTypeStorage, int brickTypeStorageOffset)
    {
        ArgumentNullException.ThrowIfNull(brickTypeStorage);
        ArgumentOutOfRangeException.ThrowIfNegative(brickTypeStorageOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            brickTypeStorageOffset + BrickTypeSegmentsPerSection,
            brickTypeStorage.Length,
            nameof(brickTypeStorageOffset)
        );

        Pos = pos;
        _brickTypeStorage = brickTypeStorage;
        _brickTypeStorageOffset = brickTypeStorageOffset;
    }
    
    public void FillBrickType(VoxelChannel channel, BrickType value)
    {
        DebugValidateChannel(channel);
        Debug.Assert(value != BrickType.None, "Brick type None is a poison value and cannot be written.");
        
        var destination = _brickTypeStorage.AsSpan(
            _brickTypeStorageOffset + GetChannelWordOffset(channel),
            BrickTypeSegmentsPerChannel
        );
        
        destination.Fill(GetBrickTypeWord(value));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BrickType GetBrickType(VoxelChannel channel, int brickIndex)
    {
        DebugValidateBrickType(channel, brickIndex);
        
        var wordIndex = _brickTypeStorageOffset + GetChannelWordOffset(channel) + (brickIndex >> IndexToSegmentShift);
        var shift = (brickIndex << BrickTypeWidthLog) & 63;
        
        var segment = _brickTypeStorage[wordIndex];
        var type = (BrickType)((segment >> shift) & BrickTypeMask);
        
        Debug.Assert(type != BrickType.None, "Brick type None is a poison value and was never written.");
        
        return type;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetBrickType(VoxelChannel channel, int brickIndex, BrickType value)
    {
        DebugValidateBrickType(channel, brickIndex, value);
        
        var wordIndex = _brickTypeStorageOffset + GetChannelWordOffset(channel) + (brickIndex >> IndexToSegmentShift);
        var shift = (brickIndex << BrickTypeWidthLog) & 63;
        
        var clearMask = ~((ulong)BrickTypeMask << shift);
        var insertValue = ((ulong)value & BrickTypeMask) << shift;
        
        _brickTypeStorage[wordIndex] = (_brickTypeStorage[wordIndex] & clearMask) | insertValue;
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AreAllBricksOfType(VoxelChannel channel, BrickType value)
    {
        DebugValidateChannel(channel);
        
        var expectedWord = GetBrickTypeWord(value);
        var start = _brickTypeStorageOffset + GetChannelWordOffset(channel);
        var slice = new ReadOnlySpan<ulong>(_brickTypeStorage, start, BrickTypeSegmentsPerChannel);

        if (Vector256.IsHardwareAccelerated && slice.Length >= 4)
        {
            // Broadcasts the expected word:
            var expectedVector = Vector256.Create(expectedWord);
        
            // Reinterprets the ulong span as a Vector256 span: 
            var vectorSpan = MemoryMarshal.Cast<ulong, Vector256<ulong>>(slice);

            for (var index = 0; index < vectorSpan.Length; index++)
            {
                ref readonly var vec = ref vectorSpan[index];
                
                if (vec != expectedVector)
                {
                    return false;
                }
            }

            // Takes the scalar tail (which wouldn't actually exist):
            slice = slice[(vectorSpan.Length * 4)..];
        }

        // Scalar implementation via word-level parallelism:
        for (var index = 0; index < slice.Length; index++)
        {
            if (slice[index] != expectedWord)
            {
                return false;
            }
        }

        return true;
    }
    
    /// <summary>
    ///     Flat index of a local brick coordinate, x fastest, then y, then z.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBrickIndex(int x, int y, int z)
    {
        DebugValidateBrickCoord(x, y, z);
        
        return x + y * BricksPerAxis + z * BricksPerAxis * BricksPerAxis;
    }
    
    /// <summary>
    ///     Broadcasts a brick type into every 2-bit slot of a 64-bit segment.
    ///     A type fits its own width, so the multiply cannot carry between slots.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong GetBrickTypeWord(BrickType value)
    {
        return (ulong)value * (ulong.MaxValue / BrickTypeMask);
    }
    
    /// <summary>
    ///     Word offset of a channel's first segment, relative to the section's first word.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetChannelWordOffset(VoxelChannel channel)
    {
        return (int)channel * BrickTypeSegmentsPerChannel;
    }
    
    [Conditional("DEBUG")]
    private static void DebugValidateChannel(VoxelChannel channel)
    {
        Debug.Assert(
            channel is VoxelChannel.Content or VoxelChannel.Material,
            $"Voxel channel {channel} is not a defined channel."
        );
    }
    
    [Conditional("DEBUG")]
    private static void DebugValidateBrickType(VoxelChannel channel, int brickIndex)
    {
        DebugValidateChannel(channel);
        Debug.Assert(
            brickIndex is >= 0 and < Bricks,
            $"Brick index {brickIndex} is out of range [0, {Bricks})."
        );
    }
    
    [Conditional("DEBUG")]
    private static void DebugValidateBrickType(VoxelChannel channel, int brickIndex, BrickType value)
    {
        DebugValidateBrickType(channel, brickIndex);
        Debug.Assert(value != BrickType.None, "Brick type None is a poison value and cannot be written.");
    }
    
    [Conditional("DEBUG")]
    private static void DebugValidateBrickCoord(int x, int y, int z)
    {
        Debug.Assert(
            x is >= 0 and < BricksPerAxis,
            $"Brick coordinate X {x} is out of bounds [0, {BricksPerAxis})."
        );
        Debug.Assert(
            y is >= 0 and < BricksPerAxis,
            $"Brick coordinate Y {y} is out of bounds [0, {BricksPerAxis})."
        );
        Debug.Assert(
            z is >= 0 and < BricksPerAxis,
            $"Brick coordinate Z {z} is out of bounds [0, {BricksPerAxis})."
        );
    }
}