using System.Diagnostics;
using System.Runtime.InteropServices;
using Graphics2.Core;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using ImGuiNET;
using Silk.NET.Maths;
using WinterGame.Utilities;

namespace WinterGame;

/// <summary>
///     The exposure the tone mapper scales the scene by, adapted towards the scene's own brightness the way an eye adapts.
///     Each frame meters the world pass's colour into a small image of mean log luminance per block of the screen, owned per frame slot and copied into a readback buffer.
///     The CPU reads a slot's buffer when the slot comes round again, which <c>BeginFrame</c> has waited for, so the exposure trails the scene by the frames in flight. Render thread only.
/// </summary>
internal sealed class ExposureMeter : IDisposable
{
    /// <summary>
    ///     The meter's size in blocks, which ExposureMeter.slang's MeterSize has to equal.
    /// </summary>
    public const int Width = 64;

    public const int Height = 32;

    /// <summary>
    ///     The metered log2 luminance at which the exposure is 1. Daylight on snow meters -1.1 to -1.5, so the day keeps the look the palette was tuned for.
    /// </summary>
    private const float KeyEv = -1.3f;

    /// <summary>
    ///     How much of a change in the scene's brightness the exposure takes back, so a night or a cave still reads darker than the day. Night meters about -6, which half compensation lifts by 2.4 stops.
    /// </summary>
    private const float Compensation = 0.5f;

    private const float MinEv = -2.0f;
    private const float MaxEv = 4.0f;

    /// <summary>
    ///     The seconds the exposure takes to cover about two thirds of the way to its target as the scene darkens, and as it brightens. Eyes adapt to the light faster than to the dark.
    /// </summary>
    private const float AdaptToDarkSeconds = 1.5f;

    private const float AdaptToBrightSeconds = 0.5f;

    /// <summary>
    ///     The fractions of the weighted blocks, darkest first, between which the blocks are averaged, so a few very dark or very bright blocks do not swing the exposure.
    /// </summary>
    private const float LowPercentile = 0.3f;

    private const float HighPercentile = 0.9f;

    private const int HistogramBins = 64;
    private const float HistogramMinEv = -16.0f;
    private const float HistogramMaxEv = 8.0f;

    private readonly Pipeline _pipeline;
    private readonly VkSampler _sampler;
    private readonly Texture[] _targets;
    private readonly VkBuffer[] _readbacks;

    /// <summary>
    ///     Whether each slot's readback holds a measurement not read yet.
    /// </summary>
    private readonly bool[] _pending;

    /// <summary>
    ///     Each block's weight in the mean, highest at the centre of the screen.
    /// </summary>
    private readonly float[] _blockWeights = new float[Width * Height];

    private readonly float[] _binWeights = new float[HistogramBins];
    private readonly float[] _binSums = new float[HistogramBins];

    private bool _hasMeasurement;
    private bool _adapted;
    private float _adaptedEv;
    private long _lastTimestamp;
    private bool _automatic = true;
    private float _manualEv;

    public ExposureMeter(VulkanResourceFactory factory, int framesInFlight)
    {
        _pipeline = factory.CreatePipeline(new PipelineDescription
        {
            Source = Helpers.GetResourceText(typeof(App).Assembly, "WinterGame.Shaders.ExposureMeter.slang"),
            VertexEntryPoint = "vsMain",
            FragmentEntryPoint = "fsMain",
            ColorAttachments = [TextureFormat.R32Sfloat],
            DepthTest = false,
            DepthWrite = false,
            CullMode = CullMode.None,
            Blends = [BlendState.None]
        });

        _sampler = factory.CreateSampler(new SamplerDescription
        {
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            AnisotropyEnabled = false
        });

        _targets = new Texture[framesInFlight];
        _readbacks = new VkBuffer[framesInFlight];
        _pending = new bool[framesInFlight];

        for (var slot = 0; slot < framesInFlight; slot++)
        {
            _targets[slot] = factory.CreateTexture(new TextureDescription
            {
                Width = Width,
                Height = Height,
                Format = TextureFormat.R32Sfloat,
                Usage = TextureUsage.ColorAttachment | TextureUsage.TransferSource
            });

            _readbacks[slot] = factory.CreateBuffer(new BufferDescription
            {
                Size = Width * Height * sizeof(float),
                Usage = BufferUsage.Readback
            });
        }

        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var u = 2.0f * (x + 0.5f) / Width - 1.0f;
                var v = 2.0f * (y + 0.5f) / Height - 1.0f;

                // 1 at the centre, 0.4 at the middle of an edge and 0.25 in a corner:
                _blockWeights[y * Width + x] = 1.0f / (1.0f + 1.5f * (u * u + v * v));
            }
        }
    }

    /// <summary>
    ///     The exposure in stops, adapted or set by hand in the panel.
    /// </summary>
    public float ExposureEv => _automatic ? _adaptedEv : _manualEv;

    /// <summary>
    ///     The factor the tone mapper scales the scene by.
    /// </summary>
    public float Exposure => MathF.Pow(2.0f, ExposureEv);

    /// <summary>
    ///     The mean log2 luminance of the latest measurement read back.
    /// </summary>
    public float MeteredEv { get; private set; }

    /// <summary>
    ///     Reads this slot's last measurement, moves the exposure on, and meters <paramref name="scene"/>, which the world pass has finished, into this slot.
    ///     No pass may be open.
    /// </summary>
    public void Measure(Frame frame, VkTexture scene)
    {
        WgAssert.DebugIsOnRenderThread();

        var slot = (int)frame.SlotIndex;

        if (_pending[slot])
        {
            Read(slot);
            _pending[slot] = false;
        }

        Adapt();

        using (var pass = frame.BeginRenderPass(new RenderPassDescription
        {
            RenderArea = new Box2D<int>(Vector2D<int>.Zero, new Vector2D<int>(Width, Height)),
            ColorAttachments =
            [
                new ColorAttachment
                {
                    Target = _targets[slot],
                    LoadOp = LoadOp.DontCare,
                    StoreOp = StoreOp.Store,
                    FinalLayout = TextureLayout.ColorAttachment
                }
            ]
        }))
        {
            pass.SetPipeline(_pipeline);
            pass.SetViewport(0, 0, Width, Height);
            pass.SetScissor(0, 0, Width, Height);
            pass.BindTexture("sceneColor", scene);
            pass.BindSampler("sceneSampler", _sampler);
            pass.Draw(3);
        }

        frame.CopyTextureToBuffer(_targets[slot], _readbacks[slot]);
        _pending[slot] = true;
    }

    public void DrawPanel()
    {
        ImGui.Text($"Exposure: {ExposureEv:+0.00;-0.00} EV, metered {MeteredEv:F2} EV");
        ImGui.Checkbox("Automatic exposure", ref _automatic);

        if (!_automatic)
        {
            ImGui.SliderFloat("Exposure (EV)", ref _manualEv, MinEv - 2.0f, MaxEv + 2.0f);
        }
    }

    /// <summary>
    ///     The weighted mean of the blocks between the percentiles, from a histogram of their log luminance whose bins keep the sum of what fell in them.
    /// </summary>
    private void Read(int slot)
    {
        var blocks = MemoryMarshal.Cast<byte, float>(_readbacks[slot].MappedData)[..(Width * Height)];

        Array.Clear(_binWeights);
        Array.Clear(_binSums);

        var total = 0.0f;

        for (var index = 0; index < blocks.Length; index++)
        {
            var ev = blocks[index];

            if (!float.IsFinite(ev))
            {
                continue;
            }

            var bin = Math.Clamp((int)((ev - HistogramMinEv) * (HistogramBins / (HistogramMaxEv - HistogramMinEv))), 0, HistogramBins - 1);
            var weight = _blockWeights[index];

            _binWeights[bin] += weight;
            _binSums[bin] += weight * ev;
            total += weight;
        }

        if (total <= 0.0f)
        {
            return;
        }

        var low = total * LowPercentile;
        var high = total * HighPercentile;
        var below = 0.0f;
        var sum = 0.0f;
        var weightSum = 0.0f;

        for (var bin = 0; bin < HistogramBins; bin++)
        {
            var binWeight = _binWeights[bin];

            if (binWeight <= 0.0f)
            {
                continue;
            }

            var taken = Math.Clamp(Math.Min(below + binWeight, high) - Math.Max(below, low), 0.0f, binWeight);

            sum += taken * (_binSums[bin] / binWeight);
            weightSum += taken;
            below += binWeight;
        }

        if (weightSum > 0.0f)
        {
            MeteredEv = sum / weightSum;
            _hasMeasurement = true;
        }
    }

    /// <summary>
    ///     Moves the exposure towards the one the measurement asks for, by the time since the last frame. The first measurement is taken at once, so a session does not open with a fade.
    /// </summary>
    private void Adapt()
    {
        var now = Stopwatch.GetTimestamp();
        var seconds = _lastTimestamp == 0 ? 0.0f : Math.Min((float)Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalSeconds, 0.25f);
        _lastTimestamp = now;

        if (!_hasMeasurement)
        {
            return;
        }

        var target = Math.Clamp(Compensation * (KeyEv - MeteredEv), MinEv, MaxEv);

        if (!_adapted)
        {
            _adaptedEv = target;
            _adapted = true;
            return;
        }

        var timeConstant = target > _adaptedEv ? AdaptToDarkSeconds : AdaptToBrightSeconds;
        _adaptedEv += (target - _adaptedEv) * (1.0f - MathF.Exp(-seconds / timeConstant));
    }

    public void Dispose()
    {
        foreach (var target in _targets)
        {
            target.Dispose();
        }

        foreach (var readback in _readbacks)
        {
            readback.Dispose();
        }

        _sampler.Dispose();
        _pipeline.Dispose();
    }
}
