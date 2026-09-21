using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace WinterGame.World.Storage;

/// <summary>
///     One brick channel's stored state packed into 32 bits.
/// </summary>
public readonly struct BrickDescriptor : IEquatable<BrickDescriptor>
{
    private const int BrickTypeBits = 2;
    private const uint BrickTypeMask = (1u << BrickTypeBits) - 1;

    /// <summary>
    ///     The number of bits the user data occupies.
    /// </summary>
    public const int BrickUserDataBits = 32 - BrickTypeBits;

    /// <summary>
    ///     The widest user data a descriptor can carry, which is what bounds the dense pool's slot count.
    /// </summary>
    public const uint MaxUserData = uint.MaxValue >> BrickTypeBits;

    private readonly uint _packed;

    private BrickDescriptor(uint packed)
    {
        _packed = packed;
    }

    public BrickType Type
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (BrickType)(_packed & BrickTypeMask);
    }

    /// <summary>
    ///     The payload, whose meaning depends on <see cref="Type" />.
    /// </summary>
    public uint UserData
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _packed >> BrickTypeBits;
    }

    /// <summary>
    ///     Packs a brick channel's state. <see cref="BrickType.None" /> is the value a fresh descriptor carries, so it packs like any other type and the readers are what reject it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BrickDescriptor Pack(BrickType type, uint userData)
    {
        DebugValidateType(type);
        DebugValidateUserData(userData);
        Debug.Assert(
            type is not (BrickType.None or BrickType.Provider) || userData == 0,
            $"A {type} brick carries no user data."
        );

        return new BrickDescriptor(((uint)type & BrickTypeMask) | (userData << BrickTypeBits));
    }

    /// <summary>
    ///     Rebuilds a descriptor from the raw word, which is what reading one back from storage does.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BrickDescriptor FromPacked(uint packed)
    {
        DebugValidateType((BrickType)(packed & BrickTypeMask));

        return new BrickDescriptor(packed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ToPacked() => _packed;

    [Conditional("DEBUG")]
    private static void DebugValidateType(BrickType type)
    {
        Debug.Assert((uint)type <= BrickTypeMask, $"Brick type {type} does not fit in {BrickTypeBits} bits.");
    }

    [Conditional("DEBUG")]
    private static void DebugValidateUserData(uint userData)
    {
        Debug.Assert(userData <= MaxUserData, $"Brick descriptor user data {userData} needs more than {BrickUserDataBits} bits.");
    }

    public bool Equals(BrickDescriptor other) => _packed == other._packed;

    public override bool Equals(object? obj) => obj is BrickDescriptor other && Equals(other);

    public override int GetHashCode() => (int)_packed;

    public static bool operator ==(BrickDescriptor left, BrickDescriptor right) => left.Equals(right);

    public static bool operator !=(BrickDescriptor left, BrickDescriptor right) => !left.Equals(right);
}
