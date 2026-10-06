using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;

// ReSharper disable ConvertToPrimaryConstructor

namespace WinterGame.World.Storage;

/// <summary>
///     The position of a block of sections in block units, which is a section position shifted right by <see cref="Log"/>.
///     The shift floors, so a negative section lands in the block below zero.
/// </summary>
public struct SectionBlockPos : IEquatable<SectionBlockPos>
{
    /// <summary>
    ///     The block is NxNxN sections, where <c>N = 2^<see cref="Log"/></c>.
    /// </summary>
    public const int Log = 2;

    /// <summary>
    ///     Sections per axis in a block.
    /// </summary>
    public const int BlockSize = 1 << Log;

    /// <summary>
    ///     Truncates a section coordinate to its offset inside its block.
    /// </summary>
    public const int BlockMask = BlockSize - 1;

    /// <summary>
    ///     Sections in a block, which is one bit each in a 64-bit block mask.
    /// </summary>
    public const int SectionsPerBlock = BlockSize * BlockSize * BlockSize;

    public int X;
    public int Y;
    public int Z;

    public SectionBlockPos(int x, int y, int z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public SectionBlockPos(ulong key)
    {
        Unpack(key, out X, out Y, out Z);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SectionBlockPos FromSection(SectionPos position) => new(position.X >> Log, position.Y >> Log, position.Z >> Log);

    /// <summary>
    ///     Packs block coordinates into a 64-bit key with the layout of <see cref="SectionPos.Pack"/>, which holds every block coordinate, since a block coordinate is never further from zero than its sections' coordinates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Pack(int x, int y, int z) => SectionPos.Pack(x, y, z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Unpack(ulong key, out int x, out int y, out int z) => SectionPos.Unpack(key, out x, out y, out z);

    /// <summary>
    ///     The index of a section inside its block, X fastest, then Y, then Z, so the sections of one row inside a block are adjacent bits of a block mask.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetLocalIndex(int x, int y, int z) => (x & BlockMask) | ((y & BlockMask) << Log) | ((z & BlockMask) << (2 * Log));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetLocalIndex(SectionPos position) => GetLocalIndex(position.X, position.Y, position.Z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ulong GetKey() => Pack(X, Y, Z);

    /// <summary>
    ///     The section at <paramref name="localIndex"/> inside this block.
    /// </summary>
    [Pure]
    public readonly SectionPos GetSection(int localIndex) => new(
        (X << Log) | (localIndex & BlockMask),
        (Y << Log) | ((localIndex >> Log) & BlockMask),
        (Z << Log) | (localIndex >> (2 * Log))
    );

    public readonly bool Equals(SectionBlockPos other)
    {
        return X == other.X && Y == other.Y && Z == other.Z;
    }

    public readonly override bool Equals(object? obj)
    {
        return obj is SectionBlockPos other && Equals(other);
    }

    public readonly override int GetHashCode()
    {
        return HashCode.Combine(X, Y, Z);
    }

    public static bool operator ==(SectionBlockPos left, SectionBlockPos right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(SectionBlockPos left, SectionBlockPos right)
    {
        return !left.Equals(right);
    }
}
