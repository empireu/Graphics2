using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinterGame.Core;

/// <summary>
///     The camera and the light for one frame, read by the world shaders as one uniform buffer.
///     Every member is 16 bytes or a multiple of it, so the sequential layout is also the std140 layout the shader reads, and a new member has to keep that.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameLighting
{
    public Matrix4x4 ViewProj;

    /// <summary>
    ///     Unit vector towards the directional light in xyz.
    /// </summary>
    public Vector4 LightDirection;

    public Vector4 LightRadiance;
    public Vector4 SkyZenith;
    public Vector4 SkyHorizon;

    public ShadowCascades ShadowCascades;

    /// <summary>
    ///     The debug view in x: 0 is none, 1 tints every sunlit pixel by the shadow cascade it read, and 2 by the angle between its surface and the light.
    /// </summary>
    public Vector4 DebugOptions;
}

/// <summary>
///     One cascade of the sun's shadow map, as the world shaders read it.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ShadowCascade
{
    /// <summary>
    ///     World position to the cascade's clip space, which its tile of the map covers.
    /// </summary>
    public Matrix4x4 ViewProj;

    /// <summary>
    ///     The cascade's texel size in metres in x.
    /// </summary>
    public Vector4 Parameters;
}

/// <summary>
///     The sun's shadow cascades, smallest first. The shader's array has to have the same length.
/// </summary>
[InlineArray(SunShadow.CascadeCount)]
internal struct ShadowCascades
{
    private ShadowCascade _element;
}
