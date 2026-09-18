using System.Runtime.InteropServices;

namespace Graphics2.Core.Render;

[StructLayout(LayoutKind.Sequential)]
public struct DrawIndexedIndirectCommand
{
    /// <summary>
    ///     Bytes per command, which is the stride an indirect draw expects.
    /// </summary>
    public const uint SizeInBytes = 20;

    /// <summary>
    ///     Number of indices to draw.
    /// </summary>
    public uint IndexCount;

    /// <summary>
    ///     Number of instances, 1 unless the draw is instanced.
    /// </summary>
    public uint InstanceCount;

    /// <summary>
    ///     First index to read, counted in indices from the bound index buffer's offset.
    /// </summary>
    public uint FirstIndex;

    /// <summary>
    ///     Value added to each index to reach a vertex, counted in vertices from the bound vertex buffer's offset.
    /// </summary>
    public int VertexOffset;

    /// <summary>
    ///     The draw's number, carried to the shader as the instance index.
    /// </summary>
    public uint FirstInstance;
}
