using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;
using Graphics2.Mathematics;

// ReSharper disable ConvertToPrimaryConstructor

namespace WinterGame.World.Storage;

public struct SectionPos : IEquatable<SectionPos>
{
    /// <summary>
    ///     The section is NxNxN voxels, where <c>N = 2^<see cref="Log"/></c>.
    /// </summary>
    public const int Log = 5;

    /// <summary>
    ///     Voxels per axis in a section.
    /// </summary>
    public const int SectionSize = 1 << Log;

    /// <summary>
    ///     Truncates a world voxel coordinate to its offset inside its section.
    /// </summary>
    public const int SectionMask = SectionSize - 1;

    /// <summary>
    ///     Number of bits allocated to X (including sign).
    /// </summary>
    private const int XBits = 22;
    
    /// <summary>
    ///     Number of bits allocated to Z (including sign).
    /// </summary>
    private const int ZBits = 22;
    
    /// <summary>
    ///     Number of bits allocated to Y (including sign).
    /// </summary>
    private const int YBits = 20;
    
    private const int XShift = YBits + ZBits;
    private const int ZShift = YBits;
    private const int YShift = 0;
    
    // Masks to truncate to the assigned bit widths
    private const ulong XMask = (1UL << XBits) - 1UL;
    private const ulong ZMask = (1UL << ZBits) - 1UL;
    private const ulong YMask = (1UL << YBits) - 1UL;
    
    // Shifts used to perform arithmetic sign extension back to 32-bit int
    private const int SignShiftX = 32 - XBits;
    private const int SignShiftZ = 32 - ZBits;
    private const int SignShiftY = 32 - YBits;
    
    public const int MinX = -(1 << (XBits - 1));
    public const int MaxX = (1 << (XBits - 1)) - 1;
    public const int MinZ = -(1 << (ZBits - 1));
    public const int MaxZ = (1 << (ZBits - 1)) - 1;
    public const int MinY = -(1 << (YBits - 1));
    public const int MaxY = (1 << (YBits - 1)) - 1;

    [Conditional("DEBUG")]
    private static void DebugValidate(int x, int y, int z)
    {
        Debug.Assert(
            x is >= MinX and <= MaxX,
            $"Section coordinate X {x} is out of packable bounds [{MinX}, {MaxX}]."
        );
        Debug.Assert(
            y is >= MinY and <= MaxY,
            $"Section coordinate Y {y} is out of packable bounds [{MinY}, {MaxY}]."
        );
        Debug.Assert(
            z is >= MinZ and <= MaxZ,
            $"Section coordinate Z {z} is out of packable bounds [{MinZ}, {MaxZ}]."
        );
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int VoxelToSectionCoord(int voxelCoord) => voxelCoord >> Log;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int SectionToVoxelCoord(int sectionCoord) => sectionCoord << Log;

    /// <summary>
    ///     Packs (X, Y, Z) section coordinates into a 64-bit unsigned integer key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Pack(int x, int y, int z)
    {
        DebugValidate(x, y, z);
        
        var result = 0UL;
        
        result |= ((ulong)x & XMask) << XShift;
        result |= ((ulong)z & ZMask) << ZShift;
        result |= ((ulong)y & YMask) << YShift;

        return result;
    }
    
    /// <summary>
    ///     Unpacks a 64-bit key back into section position.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Unpack(ulong key, out int x, out int y, out int z)
    {
        x = ((int)(key >> XShift) << SignShiftX) >> SignShiftX;
        z = ((int)(key >> ZShift) << SignShiftZ) >> SignShiftZ;
        y = ((int)(key >> YShift) << SignShiftY) >> SignShiftY;
    }
    
    public int X;
    public int Y;
    public int Z;

    public SectionPos(int x, int y, int z)
    {
        DebugValidate(x, y, z);
        
        X = x;
        Y = y;
        Z = z;
    }

    public SectionPos(ulong value)
    {
        Unpack(value, out X, out Y, out Z);
    }

    /// <summary>
    ///     The packed key of this section, which is its identity: what to compare, what to key a dictionary on, and what to unpack back into a position.
    ///     Packed coordinates are not contiguous, so a neighbor step has to go through <see cref="SectionPos(int, int, int)" /> rather than through arithmetic on the key.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ulong GetKey() => Pack(X, Y, Z);

    /// <summary>
    ///     The world voxel coordinate of the section's first corner, which is the origin its local coordinates are measured from.
    /// </summary>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector3i GetOrigin() => new(
        SectionToVoxelCoord(X),
        SectionToVoxelCoord(Y),
        SectionToVoxelCoord(Z)
    );
    
    public readonly bool Equals(SectionPos other)
    {
        return X == other.X && Y == other.Y && Z == other.Z;
    }

    public readonly override bool Equals(object? obj)
    {
        return obj is SectionPos other && Equals(other);
    }

    public readonly override int GetHashCode()
    {
        return HashCode.Combine(X, Y, Z);
    }

    public static bool operator ==(SectionPos left, SectionPos right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(SectionPos left, SectionPos right)
    {
        return !left.Equals(right);
    }
}