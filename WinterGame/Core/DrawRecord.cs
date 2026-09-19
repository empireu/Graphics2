using System.Numerics;
using System.Runtime.InteropServices;

namespace WinterGame.Core;

/// <summary>
///     Per-draw data describing where a mesh sits in the world and which material volume it samples.
///     Origin is in voxel units, so it stays exact for a whole world, and the shader applies <see cref="VoxelSize" /> to the sum once.
///     The record is 32 bytes because a std430 array element is rounded up to the struct's 16-byte alignment, so the extent fields fill that space rather than being padding.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DrawRecord
{
    public Vector3 Origin;
    public float VoxelSize;

    /// <summary>
    ///     Index of the volume in the bindless material volume array.
    /// </summary>
    public uint MaterialVolume;

    /// <summary>
    ///     Texels per volume, which the shader clamps its corner fetch against.
    /// </summary>
    public uint MaterialSizeX;
    public uint MaterialSizeY;
    public uint MaterialSizeZ;
}
