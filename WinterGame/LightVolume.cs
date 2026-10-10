using System.Numerics;
using System.Runtime.InteropServices;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.Mathematics;
using WinterGame.Utilities;
using WinterGame.World;
using WinterGame.World.Lighting;
using WinterGame.World.Storage;

namespace WinterGame;

/// <summary>
///     The light engine's window on the GPU, one R8 3D texture with a texel per cell that holds the cell's sky visibility, or 0 for a cell with no air.
///     A cell's texel is its brick coordinates modulo the texture's size, so a slot of the window is one box of texels.
///     The texture is created when the first message places the window, and each <see cref="Upload"/> copies the slots that changed since the last one. Render thread only.
/// </summary>
internal sealed class LightVolume : IDisposable
{
    private const int CellsPerSlot = Section.Bricks;

    /// <summary>
    ///     The size of a cell in metres.
    /// </summary>
    private const float CellSize = Section.BrickSize * WorldConstants.VoxelSize;

    /// <summary>
    ///     Nearest filtering with repeat addressing, which the world shader reads each cell through.
    /// </summary>
    public readonly VkSampler Sampler;

    private readonly VulkanResourceFactory _factory;
    private readonly MessageChannel<LightVolumeMessage> _channel;

    /// <summary>
    ///     The messages taken since the last <see cref="Upload"/>.
    /// </summary>
    private readonly List<LightVolumeMessage> _messages = [];

    private Texture? _texture;
    private bool _isWritten;

    /// <summary>
    ///     The number of the current upload, stamped on each slot it copies.
    /// </summary>
    private int _uploadStamp;

    private int[] _uploadStamps = [];

    /// <summary>
    ///     The index in <see cref="_messages"/> of each message the current upload copies.
    /// </summary>
    private int[] _uploads = [];

    private TextureCopyRegion[] _regions = [];

    public LightVolume(VulkanResourceFactory factory, MessageChannel<LightVolumeMessage> channel)
    {
        _factory = factory;
        _channel = channel;
        Sampler = factory.CreateSampler(new SamplerDescription
        {
            MinFilter = FilterMode.Nearest,
            MagFilter = FilterMode.Nearest,
            AnisotropyEnabled = false
        });
    }

    /// <summary>
    ///     Null until the light engine has placed its window.
    /// </summary>
    public VkTexture? Texture => _texture;

    /// <summary>
    ///     The window's lowest cell in xyz and the size of a cell in metres in w, as the world shader reads them.
    /// </summary>
    public Vector4 ShaderParameters { get; private set; }

    /// <summary>
    ///     Takes what the light engine sent since the last call. A move of the window applies at once, so <see cref="ShaderParameters"/> describes the texture as the next <see cref="Upload"/> leaves it.
    /// </summary>
    public void Take()
    {
        WgAssert.DebugIsOnRenderThread();

        var first = _messages.Count;

        _channel.Take(_messages);

        var messages = CollectionsMarshal.AsSpan(_messages);

        for (var index = first; index < messages.Length; index++)
        {
            ref readonly var message = ref messages[index];

            if (message.Kind == LightVolumeMessageKind.Placed)
            {
                Place(message.Min, message.Size);
            }
        }
    }

    /// <summary>
    ///     Records the copy of every slot that changed since the last upload. It has to be outside a pass, and before the pass that reads the texture.
    /// </summary>
    public void Upload(Frame frame)
    {
        WgAssert.DebugIsOnRenderThread();

        if (_texture == null)
        {
            return;
        }

        // A texture that was never written cannot be read, so a new one is cleared whole first:
        if (!_isWritten)
        {
            var zeros = frame.AllocateStorage((int)(_texture.Width * _texture.Height * _texture.Depth));

            zeros.Data.Clear();
            frame.CopyBufferToImage(zeros, _texture, [new TextureCopyRegion(0, 0, 0, 0, _texture.Width, _texture.Height, _texture.Depth)]);
            _isWritten = true;
        }

        var messages = CollectionsMarshal.AsSpan(_messages);
        var count = 0;

        _uploadStamp++;

        // The latest message for a slot replaces the earlier ones, and the regions of one copy must not overlap, so the messages are read newest first and a slot is copied once:
        for (var index = messages.Length - 1; index >= 0; index--)
        {
            ref readonly var message = ref messages[index];

            if (message.Kind != LightVolumeMessageKind.Slot || _uploadStamps[message.Slot] == _uploadStamp)
            {
                continue;
            }

            _uploadStamps[message.Slot] = _uploadStamp;
            _uploads[count++] = index;
        }

        if (count > 0)
        {
            var levels = frame.AllocateStorage(count * CellsPerSlot);

            for (var upload = 0; upload < count; upload++)
            {
                ref readonly var message = ref messages[_uploads[upload]];
                var offset = upload * CellsPerSlot;

                ((ReadOnlySpan<byte>)message.Levels).CopyTo(levels.Data[offset..]);
                _regions[upload] = new TextureCopyRegion(
                    (uint)offset,
                    (uint)message.Origin.X,
                    (uint)message.Origin.Y,
                    (uint)message.Origin.Z,
                    Section.BricksPerAxis,
                    Section.BricksPerAxis,
                    Section.BricksPerAxis
                );
            }

            frame.CopyBufferToImage(levels, _texture, _regions.AsSpan(0, count));
        }

        _messages.Clear();
    }

    public void Dispose()
    {
        _texture?.Dispose();
        Sampler.Dispose();
    }

    /// <summary>
    ///     Creates the texture the first time and points the shader parameters at the window's new lowest section.
    /// </summary>
    private void Place(SectionPos min, Vector3i size)
    {
        if (_texture == null)
        {
            _texture = _factory.CreateTexture(new TextureDescription
            {
                Width = (uint)size.X,
                Height = (uint)size.Y,
                Depth = (uint)size.Z,
                Format = TextureFormat.R8Unorm,
                Usage = TextureUsage.Sampled | TextureUsage.TransferDestination,
                Type = TextureType.Texture3D
            });

            var slotCount = size.X * size.Y * size.Z / CellsPerSlot;

            _uploadStamps = new int[slotCount];
            _uploads = new int[slotCount];
            _regions = new TextureCopyRegion[slotCount];
        }
        else if (_texture.Width != size.X || _texture.Height != size.Y || _texture.Depth != size.Z)
        {
            throw new InvalidOperationException($"The light window is {size.X}x{size.Y}x{size.Z} cells, and the volume was created for {_texture.Width}x{_texture.Height}x{_texture.Depth}.");
        }

        ShaderParameters = new Vector4(
            min.X << Section.BricksPerAxisLog,
            min.Y << Section.BricksPerAxisLog,
            min.Z << Section.BricksPerAxisLog,
            CellSize
        );
    }
}
