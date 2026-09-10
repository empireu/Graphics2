using System.Runtime.InteropServices;
using Graphics2.Core.Render;
using Graphics2.Core.Resources;
using Graphics2.Windowing.Input;
using ImGuiNET;
using Silk.NET.Maths;

namespace Graphics2.ImGui;

[StructLayout(LayoutKind.Explicit)]
public struct ImGuiPushConstants(Vector2D<float> scale, Vector2D<float> translate, uint textureIndex)
{
    [FieldOffset(0)] public Vector2D<float> Scale = scale;
    [FieldOffset(8)] public Vector2D<float> Translate = translate;
    [FieldOffset(16)] public uint TextureIndex = textureIndex;
    [FieldOffset(20)] private readonly uint _padding;
}

public sealed unsafe class ImGuiRenderer(VulkanResourceFactory factory) : IDisposable
{
    private const string ShaderSource = """
        struct VertexInput {
            float2 position : POSITION;
            float2 uv : TEXCOORD0;
            float4 color : COLOR0;
        };

        struct VertexOutput {
            float4 position : SV_POSITION;
            float2 uv : TEXCOORD0;
            float4 color : COLOR0;
        };

        struct PushConstants {
            float2 scale;
            float2 translate;
            uint textureIndex;
        };

        [[vk::push_constant]] PushConstants pc;

        SamplerState fontSampler;

        [[vk::binding(0, 1)]] Texture2D<float4> textures[64];

        [shader("vertex")]
        VertexOutput vsMain(VertexInput input) {
            VertexOutput output;
            float2 projected = input.position * pc.scale + pc.translate;
            output.position = float4(projected, 0.0, 1.0);
            output.uv = input.uv;
            output.color = input.color;
            return output;
        }

        [shader("fragment")]
        float4 fsMain(VertexOutput input) : SV_TARGET {
            float4 texColor = textures[pc.textureIndex].Sample(fontSampler, input.uv);
            return input.color * texColor;
        }
        """;

    private Pipeline? _pipeline;
    private Texture? _fontTexture;
    private TextureArray? _textureArray;
    private VkSampler? _sampler;
    private IntPtr _context;
    private int _disposed;

    private Func<string?>? _getClipboardText;
    private Action<string?>? _setClipboardText;
    private GetClipboardTextDelegate? _getClipboardFn;
    private SetClipboardTextDelegate? _setClipboardFn;
    private IntPtr _lastClipboardText;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetClipboardTextDelegate(IntPtr userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SetClipboardTextDelegate(IntPtr userData, IntPtr text);

    public void Initialize(Vector2D<int> displaySize, Func<string?>? getClipboardText = null, Action<string?>? setClipboardText = null)
    {
        _context = ImGuiNET.ImGui.CreateContext();
        ImGuiNET.ImGui.SetCurrentContext(_context);

        var io = ImGuiNET.ImGui.GetIO();
        io.DisplaySize = new System.Numerics.Vector2(displaySize.X, displaySize.Y);
        io.DisplayFramebufferScale = new System.Numerics.Vector2(1.0f, 1.0f);
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;

        _getClipboardText = getClipboardText;
        _setClipboardText = setClipboardText;

        if (getClipboardText != null && setClipboardText != null)
        {
            _getClipboardFn = GetClipboardTextCallback;
            _setClipboardFn = SetClipboardTextCallback;
            var platformIo = ImGuiNET.ImGui.GetPlatformIO();
            platformIo.Platform_GetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(_getClipboardFn);
            platformIo.Platform_SetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(_setClipboardFn);
        }

        var fonts = io.Fonts;
        fonts.GetTexDataAsRGBA32(out nint pixelPtr, out var width, out var height);

        _fontTexture = factory.CreateTexture(new TextureDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = TextureFormat.R8G8B8A8Unorm,
            Usage = TextureUsage.Sampled | TextureUsage.TransferDestination
        });

        var pixelSpan = new Span<byte>((void*)pixelPtr, width * height * 4);
        factory.UpdateTexture(_fontTexture, pixelSpan);

        _textureArray = factory.CreateTextureArray(64);
        _textureArray.Add(_fontTexture);

        _sampler = factory.CreateSampler(new SamplerDescription
        {
            AnisotropyEnabled = false
        });

        _pipeline = factory.CreatePipeline(new PipelineDescription
        {
            Source = ShaderSource,
            VertexEntryPoint = "vsMain",
            FragmentEntryPoint = "fsMain",
            ColorAttachments = [TextureFormat.B8G8R8A8Srgb],
            DepthAttachment = TextureFormat.D32Sfloat,
            DepthTest = false,
            DepthWrite = false,
            CullMode = CullMode.None,
            Blends = [BlendState.AlphaBlend],
            VertexLayout = new VertexLayout
            {
                Stride = 20,
                Attributes =
                [
                    new VertexLayoutAttribute("position", VertexAttributeFormat.Float2, 0),
                    new VertexLayoutAttribute("uv", VertexAttributeFormat.Float2, 8),
                    new VertexLayoutAttribute("color", VertexAttributeFormat.R8G8B8A8Unorm, 16)
                ]
            },
            PushConstantLayout = PushConstantLayout.FromStruct<ImGuiPushConstants>()
        });

        io.Fonts.SetTexID(IntPtr.Zero);
    }

    public IntPtr AddTexture(VkTexture texture)
    {
        return new IntPtr(_textureArray!.Add(texture));
    }

    public void Update(float deltaTime, InputSnapshot input, Vector2D<int> displaySize)
    {
        var io = ImGuiNET.ImGui.GetIO();
        io.DisplaySize = new System.Numerics.Vector2(displaySize.X, displaySize.Y);
        io.DeltaTime = deltaTime;

        io.AddMousePosEvent(input.MousePosition.X, input.MousePosition.Y);
        io.AddMouseButtonEvent(0, input.IsMouseDown(G2MouseButton.Left));
        io.AddMouseButtonEvent(1, input.IsMouseDown(G2MouseButton.Right));
        io.AddMouseButtonEvent(2, input.IsMouseDown(G2MouseButton.Middle));
        io.AddMouseWheelEvent(input.ScrollDelta.X, input.ScrollDelta.Y);

        for (var i = 0; i < input.CharacterCount; i++)
        {
            io.AddInputCharacter(input.CharacterBuffer[i]);
        }

        UpdateKeyModifiers(input);
        UpdateKeyMap(input);

        ImGuiNET.ImGui.NewFrame();
    }

    private static void UpdateKeyModifiers(InputSnapshot input)
    {
        var io = ImGuiNET.ImGui.GetIO();
        io.AddKeyEvent(ImGuiKey.ModCtrl, input.IsKeyDown(G2Key.ControlLeft) || input.IsKeyDown(G2Key.ControlRight));
        io.AddKeyEvent(ImGuiKey.ModShift, input.IsKeyDown(G2Key.ShiftLeft) || input.IsKeyDown(G2Key.ShiftRight));
        io.AddKeyEvent(ImGuiKey.ModAlt, input.IsKeyDown(G2Key.AltLeft) || input.IsKeyDown(G2Key.AltRight));
        io.AddKeyEvent(ImGuiKey.ModSuper, input.IsKeyDown(G2Key.SuperLeft) || input.IsKeyDown(G2Key.SuperRight));
    }

    private static void UpdateKeyMap(InputSnapshot input)
    {
        var io = ImGuiNET.ImGui.GetIO();
        foreach (var (g2Key, imGuiKey) in KeyMapping)
        {
            io.AddKeyEvent(imGuiKey, input.IsKeyDown(g2Key));
        }
    }

    public void Submit(Frame frame, RenderPassEncoder pass)
    {
        ImGuiNET.ImGui.Render();
        var drawData = ImGuiNET.ImGui.GetDrawData();
        if (!drawData.Valid)
        {
            return;
        }

        pass.SetPipeline(_pipeline!);
        pass.SetViewport(0, 0, frame.Extent.X, frame.Extent.Y);

        var scale = new Vector2D<float>(2.0f / drawData.DisplaySize.X, 2.0f / drawData.DisplaySize.Y);
        var translate = new Vector2D<float>(-1.0f - drawData.DisplayPos.X * scale.X, -1.0f - drawData.DisplayPos.Y * scale.Y);

        pass.BindTextureArray("textures", _textureArray!);
        pass.BindSampler("fontSampler", _sampler!);

        for (var n = 0; n < drawData.CmdListsCount; n++)
        {
            var cmdList = drawData.CmdLists[n];
            var vtxBuffer = cmdList.VtxBuffer;
            var idxBuffer = cmdList.IdxBuffer;

            var vtxSize = (uint)(vtxBuffer.Size * 20);
            var idxSize = (uint)(idxBuffer.Size * 2);

            var vtxAlloc = frame.AllocateVertex(vtxSize);
            var idxAlloc = frame.AllocateVertex(idxSize);

            var vtxSrc = new Span<byte>((void*)vtxBuffer.Data, (int)vtxSize);
            var idxSrc = new Span<byte>((void*)idxBuffer.Data, (int)idxSize);
            vtxSrc.CopyTo(vtxAlloc.Data);
            idxSrc.CopyTo(idxAlloc.Data);

            pass.SetVertexBuffer(vtxAlloc.Buffer, vtxAlloc.Offset);
            pass.SetIndexBuffer(idxAlloc.Buffer, IndexType.UInt16, idxAlloc.Offset);

            for (var cmdIdx = 0; cmdIdx < cmdList.CmdBuffer.Size; cmdIdx++)
            {
                var cmd = cmdList.CmdBuffer[cmdIdx];

                if (cmd.UserCallback != IntPtr.Zero)
                {
                    continue;
                }

                var clipX = (int)(cmd.ClipRect.X - drawData.DisplayPos.X);
                var clipY = (int)(cmd.ClipRect.Y - drawData.DisplayPos.Y);
                var clipW = (uint)(cmd.ClipRect.Z - cmd.ClipRect.X);
                var clipH = (uint)(cmd.ClipRect.W - cmd.ClipRect.Y);

                if (clipW > 0 && clipH > 0)
                {
                    var textureIndex = (uint)cmd.TextureId.ToInt64();
                    pass.PushConstants(new ImGuiPushConstants(scale, translate, textureIndex));
                    pass.SetScissor(clipX, clipY, clipW, clipH);
                    pass.DrawIndexed(cmd.ElemCount, 1, cmd.IdxOffset, (int)cmd.VtxOffset);
                }
            }
        }
    }

    private static readonly (G2Key, ImGuiKey)[] KeyMapping = BuildKeyMapping();

    private static (G2Key, ImGuiKey)[] BuildKeyMapping()
    {
        return
        [
            (G2Key.Tab, ImGuiKey.Tab),
            (G2Key.Left, ImGuiKey.LeftArrow),
            (G2Key.Right, ImGuiKey.RightArrow),
            (G2Key.Up, ImGuiKey.UpArrow),
            (G2Key.Down, ImGuiKey.DownArrow),
            (G2Key.PageUp, ImGuiKey.PageUp),
            (G2Key.PageDown, ImGuiKey.PageDown),
            (G2Key.Home, ImGuiKey.Home),
            (G2Key.End, ImGuiKey.End),
            (G2Key.Insert, ImGuiKey.Insert),
            (G2Key.Delete, ImGuiKey.Delete),
            (G2Key.Backspace, ImGuiKey.Backspace),
            (G2Key.Space, ImGuiKey.Space),
            (G2Key.Enter, ImGuiKey.Enter),
            (G2Key.Escape, ImGuiKey.Escape),
            (G2Key.ControlLeft, ImGuiKey.LeftCtrl),
            (G2Key.ShiftLeft, ImGuiKey.LeftShift),
            (G2Key.AltLeft, ImGuiKey.LeftAlt),
            (G2Key.SuperLeft, ImGuiKey.LeftSuper),
            (G2Key.ControlRight, ImGuiKey.RightCtrl),
            (G2Key.ShiftRight, ImGuiKey.RightShift),
            (G2Key.AltRight, ImGuiKey.RightAlt),
            (G2Key.SuperRight, ImGuiKey.RightSuper),
            (G2Key.Menu, ImGuiKey.Menu),
            (G2Key.Number0, ImGuiKey._0),
            (G2Key.Number1, ImGuiKey._1),
            (G2Key.Number2, ImGuiKey._2),
            (G2Key.Number3, ImGuiKey._3),
            (G2Key.Number4, ImGuiKey._4),
            (G2Key.Number5, ImGuiKey._5),
            (G2Key.Number6, ImGuiKey._6),
            (G2Key.Number7, ImGuiKey._7),
            (G2Key.Number8, ImGuiKey._8),
            (G2Key.Number9, ImGuiKey._9),
            (G2Key.A, ImGuiKey.A),
            (G2Key.B, ImGuiKey.B),
            (G2Key.C, ImGuiKey.C),
            (G2Key.D, ImGuiKey.D),
            (G2Key.E, ImGuiKey.E),
            (G2Key.F, ImGuiKey.F),
            (G2Key.G, ImGuiKey.G),
            (G2Key.H, ImGuiKey.H),
            (G2Key.I, ImGuiKey.I),
            (G2Key.J, ImGuiKey.J),
            (G2Key.K, ImGuiKey.K),
            (G2Key.L, ImGuiKey.L),
            (G2Key.M, ImGuiKey.M),
            (G2Key.N, ImGuiKey.N),
            (G2Key.O, ImGuiKey.O),
            (G2Key.P, ImGuiKey.P),
            (G2Key.Q, ImGuiKey.Q),
            (G2Key.R, ImGuiKey.R),
            (G2Key.S, ImGuiKey.S),
            (G2Key.T, ImGuiKey.T),
            (G2Key.U, ImGuiKey.U),
            (G2Key.V, ImGuiKey.V),
            (G2Key.W, ImGuiKey.W),
            (G2Key.X, ImGuiKey.X),
            (G2Key.Y, ImGuiKey.Y),
            (G2Key.Z, ImGuiKey.Z),
            (G2Key.F1, ImGuiKey.F1),
            (G2Key.F2, ImGuiKey.F2),
            (G2Key.F3, ImGuiKey.F3),
            (G2Key.F4, ImGuiKey.F4),
            (G2Key.F5, ImGuiKey.F5),
            (G2Key.F6, ImGuiKey.F6),
            (G2Key.F7, ImGuiKey.F7),
            (G2Key.F8, ImGuiKey.F8),
            (G2Key.F9, ImGuiKey.F9),
            (G2Key.F10, ImGuiKey.F10),
            (G2Key.F11, ImGuiKey.F11),
            (G2Key.F12, ImGuiKey.F12),
            (G2Key.Apostrophe, ImGuiKey.Apostrophe),
            (G2Key.Comma, ImGuiKey.Comma),
            (G2Key.Minus, ImGuiKey.Minus),
            (G2Key.Period, ImGuiKey.Period),
            (G2Key.Slash, ImGuiKey.Slash),
            (G2Key.Semicolon, ImGuiKey.Semicolon),
            (G2Key.Equal, ImGuiKey.Equal),
            (G2Key.LeftBracket, ImGuiKey.LeftBracket),
            (G2Key.BackSlash, ImGuiKey.Backslash),
            (G2Key.RightBracket, ImGuiKey.RightBracket),
            (G2Key.GraveAccent, ImGuiKey.GraveAccent),
            (G2Key.CapsLock, ImGuiKey.CapsLock),
            (G2Key.ScrollLock, ImGuiKey.ScrollLock),
            (G2Key.NumLock, ImGuiKey.NumLock),
            (G2Key.PrintScreen, ImGuiKey.PrintScreen),
            (G2Key.Pause, ImGuiKey.Pause),
            (G2Key.Keypad0, ImGuiKey.Keypad0),
            (G2Key.Keypad1, ImGuiKey.Keypad1),
            (G2Key.Keypad2, ImGuiKey.Keypad2),
            (G2Key.Keypad3, ImGuiKey.Keypad3),
            (G2Key.Keypad4, ImGuiKey.Keypad4),
            (G2Key.Keypad5, ImGuiKey.Keypad5),
            (G2Key.Keypad6, ImGuiKey.Keypad6),
            (G2Key.Keypad7, ImGuiKey.Keypad7),
            (G2Key.Keypad8, ImGuiKey.Keypad8),
            (G2Key.Keypad9, ImGuiKey.Keypad9),
            (G2Key.KeypadDecimal, ImGuiKey.KeypadDecimal),
            (G2Key.KeypadDivide, ImGuiKey.KeypadDivide),
            (G2Key.KeypadMultiply, ImGuiKey.KeypadMultiply),
            (G2Key.KeypadSubtract, ImGuiKey.KeypadSubtract),
            (G2Key.KeypadAdd, ImGuiKey.KeypadAdd),
            (G2Key.KeypadEnter, ImGuiKey.KeypadEnter),
            (G2Key.KeypadEqual, ImGuiKey.KeypadEqual)
        ];
    }

    private IntPtr GetClipboardTextCallback(IntPtr userData)
    {
        if (_lastClipboardText != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_lastClipboardText);
        }

        var text = _getClipboardText?.Invoke() ?? string.Empty;
        _lastClipboardText = Marshal.StringToHGlobalAnsi(text);
        return _lastClipboardText;
    }

    private void SetClipboardTextCallback(IntPtr userData, IntPtr text)
    {
        var str = text != IntPtr.Zero ? Marshal.PtrToStringAnsi(text) : null;
        _setClipboardText?.Invoke(str);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _pipeline?.Dispose();
        _textureArray?.Dispose();
        _fontTexture?.Dispose();
        _sampler?.Dispose();

        if (_lastClipboardText != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_lastClipboardText);
            _lastClipboardText = IntPtr.Zero;
        }

        if (_context != IntPtr.Zero)
        {
            ImGuiNET.ImGui.DestroyContext(_context);
            _context = IntPtr.Zero;
        }
    }
}
