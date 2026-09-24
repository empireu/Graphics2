using System.Numerics;
using System.Runtime.InteropServices;

namespace WinterGame.Core;

/// <summary>
///     Per-draw data describing where a mesh sits in the world.
///     Origin is in voxel units, so it stays exact for a whole world, and the shader applies <see cref="VoxelSize"/> to the sum once.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DrawRecord
{
    public Vector3 Origin;
    public float VoxelSize;
}
