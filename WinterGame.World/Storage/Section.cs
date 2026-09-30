using System.Diagnostics;
using System.Runtime.CompilerServices;

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
    ///     One value across the whole brick.
    /// </summary>
    Uniform = 2,
    /// <summary>
    ///     A dense grid of samples.
    /// </summary>
    Dense = 3
}

/// <summary>
///     Handle for a section. The backing data lives in the owning <see cref="SectionArena"/>.
/// </summary>
public readonly struct Section
{
    /// <summary>
    ///     Number of channels a brick stores a descriptor for.
    /// </summary>
    public const int VoxelChannelCount = 2;

    public const int BrickLog = 2;
    public const int BrickSize = 1 << BrickLog;
    public const int BricksPerAxis = SectionPos.SectionSize / BrickSize;
    public const int Bricks = BricksPerAxis * BricksPerAxis * BricksPerAxis;

    /// <summary>
    ///     Number of descriptors one channel stores, one per brick.
    /// </summary>
    public const int DescriptorsPerChannel = Bricks;

    /// <summary>
    ///     Number of descriptors one section owns in the slab: one channel's descriptors after the other.
    /// </summary>
    public const int DescriptorsPerSection = DescriptorsPerChannel * VoxelChannelCount;
    
    /// <summary>
    ///     Number of voxels one brick stores, which is the size of a dense array.
    /// </summary>
    public const int VoxelsPerBrick = BrickSize * BrickSize * BrickSize;
    
    /// <summary>
    ///     The arena that allocated the Section, for accessing the underlying storage.
    /// </summary>
    public readonly SectionArena Arena;
    
    /// <summary>
    ///     The unique position of the section. No other sections with this key exist in the arena.
    /// </summary>
    public readonly SectionPos Position;
    
    /// <summary>
    ///     Holds the actual reference to the array as well, since it doesn't move.
    /// </summary>
    public readonly DescriptorArray DescriptorArray;

    /// <summary>
    ///     Handle to the voxel storage for a section of the world.
    /// </summary>
    public Section(SectionArena arena, SectionPos position, DescriptorArray descriptorArray)
    {
        Arena = arena;
        Position = position;
        DescriptorArray = descriptorArray;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public BrickType GetBrickType(VoxelChannel channel, int brickIndex)
    {
        DebugValidateBrickIndex(channel, brickIndex);

        var descriptor = ReadDescriptor(channel, brickIndex);

        Debug.Assert(descriptor.Type != BrickType.None, "Brick type None is a poison value and was never written.");

        return descriptor.Type;
    }
    
    /// <summary>
    ///     Gets the uniform value of a brick whose type is <see cref="BrickType.Uniform"/>.
    ///     Illegal to call if the brick is of another type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public byte GetUniformValue(VoxelChannel channel, int brickIndex)
    {
        DebugValidateBrickIndex(channel, brickIndex);

        var descriptor = ReadDescriptor(channel, brickIndex);

        Debug.Assert(
            descriptor.Type == BrickType.Uniform,
            $"Brick {brickIndex} of {channel} is {descriptor.Type}, not Uniform."
        );

        return (byte)descriptor.UserData;
    }

    /// <summary>
    ///     Gets the voxel array of a brick whose type is <see cref="BrickType.Dense"/>.
    ///     Illegal to call if the brick is of another type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public VoxelArray GetDense(VoxelChannel channel, int brickIndex)
    {
        DebugValidateBrickIndex(channel, brickIndex);

        var descriptor = ReadDescriptor(channel, brickIndex);

        Debug.Assert(
            descriptor.Type == BrickType.Dense,
            $"Brick {brickIndex} of {channel} is {descriptor.Type}, not Dense."
        );

        return Arena.GetVoxelArray(VoxelArrayHandle.Unpack(descriptor.UserData));
    }
    
    /// <summary>
    ///     Evicts any allocations the brick is holding.
    ///     Doesn't mutate the descriptor; the caller is responsible for that.
    /// </summary>
    public void FreeStorage(VoxelChannel channel, int brickIndex)
    {
        DebugValidateBrickIndex(channel, brickIndex);

        var descriptor = ReadDescriptor(channel, brickIndex);

        switch (descriptor.Type)
        {
            case BrickType.None:
            case BrickType.Uniform:
                // Ignored
                break;
            case BrickType.Dense:
                Arena.FreeVoxelArray(VoxelArrayHandle.Unpack(descriptor.UserData));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(brickIndex), $"The descriptor type is {descriptor.Type}");
        }
    }

    /// <summary>
    ///     Evicts the dense array (if applicable) and sets the brick to be uniform.
    /// </summary>
    /// <param name="channel"></param>
    /// <param name="brickIndex"></param>
    /// <param name="value"></param>
    public void SetUniform(VoxelChannel channel, int brickIndex, byte value)
    {
        DebugValidateBrickIndex(channel, brickIndex);
        FreeStorage(channel, brickIndex);
        WriteDescriptor(channel, brickIndex, BrickDescriptor.Pack(BrickType.Uniform, value));
    }

    /// <summary>
    ///     If the brick isn't dense, allocates a new array from the arena and sets it.
    ///     Otherwise, this only clears the voxel values.
    /// </summary>
    public void SetDense(VoxelChannel channel, int brickIndex)
    {
        DebugValidateBrickIndex(channel, brickIndex);

        var descriptor = ReadDescriptor(channel, brickIndex);

        if (descriptor.Type != BrickType.Dense)
        {
            FreeStorage(channel, brickIndex);
            var newArray = Arena.AllocateVoxelArray();
            WriteDescriptor(channel, brickIndex, BrickDescriptor.Pack(BrickType.Dense, newArray.Handle.Pack()));
        }

        var array = GetDense(channel, brickIndex);
        
        array.Span.Fill(channel.GetClearValue());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool AreAllBricksOfType(VoxelChannel channel, BrickType value)
    {
        DebugValidateChannel(channel);

        var span = DescriptorArray.Span;
        
        var start = GetChannelDescriptorOffset(channel);

        for (var index = 0; index < DescriptorsPerChannel; index++)
        {
            if (span[start + index].Type != value)
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
    ///     Flat index of a voxel inside a dense brick's <see cref="VoxelArray"/>, x fastest, then y, then z.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBrickVoxelIndex(int x, int y, int z)
    {
        Debug.Assert(
            x is >= 0 and < BrickSize && y is >= 0 and < BrickSize && z is >= 0 and < BrickSize,
            $"Brick voxel coordinate ({x}, {y}, {z}) is out of bounds [0, {BrickSize})."
        );

        return x + y * BrickSize + z * BrickSize * BrickSize;
    }

    /// <summary>
    ///     Descriptor offset of a channel's first brick, relative to the section's first descriptor.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetChannelDescriptorOffset(VoxelChannel channel)
    {
        return (int)channel * DescriptorsPerChannel;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private BrickDescriptor ReadDescriptor(VoxelChannel channel, int brickIndex)
    {
        return DescriptorArray.Span[GetChannelDescriptorOffset(channel) + brickIndex];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteDescriptor(VoxelChannel channel, int brickIndex, BrickDescriptor descriptor)
    {
        DescriptorArray.Span[GetChannelDescriptorOffset(channel) + brickIndex] = descriptor;
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
    private static void DebugValidateBrickIndex(VoxelChannel channel, int brickIndex)
    {
        DebugValidateChannel(channel);
        Debug.Assert(
            brickIndex is >= 0 and < Bricks,
            $"Brick index {brickIndex} is out of range [0, {Bricks})."
        );
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