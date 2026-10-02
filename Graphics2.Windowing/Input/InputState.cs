using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Key = Silk.NET.Input.Key;
using MouseButton = Silk.NET.Input.MouseButton;

namespace Graphics2.Windowing.Input;

internal sealed class InputState : IDisposable
{
    private const int KeyCount = 349;
    private const int MouseButtonCount = 12;

    private readonly IInputContext _context;
    private readonly IKeyboard? _keyboard;
    private readonly IMouse? _mouse;

    private bool[] _currentKeys = new bool[KeyCount];
    private bool[] _previousKeys = new bool[KeyCount];
    private bool[] _currentMouseButtons = new bool[MouseButtonCount];
    private bool[] _previousMouseButtons = new bool[MouseButtonCount];

    private System.Numerics.Vector2 _previousMousePosition;
    private float _scrollDeltaX;
    private float _scrollDeltaY;
    private char[] _charBuffer = new char[64];
    private int _charCount;

    public InputState(IView view)
    {
        _context = view.CreateInput();

        if (_context.Keyboards.Count > 0)
        {
            _keyboard = _context.Keyboards[0];
            _keyboard.KeyChar += OnKeyChar;
        }

        if (_context.Mice.Count > 0)
        {
            _mouse = _context.Mice[0];
            _mouse.Scroll += OnScroll;
        }
    }

    public InputSnapshot BuildSnapshot(long timestamp)
    {
        (_currentKeys, _previousKeys) = (_previousKeys, _currentKeys);

        if (_keyboard != null)
        {
            foreach (var key in PollableKeys)
            {
                _currentKeys[(int)key] = _keyboard.IsKeyPressed(key);
            }
        }
        else
        {
            Array.Clear(_currentKeys);
        }

        (_currentMouseButtons, _previousMouseButtons) = (_previousMouseButtons, _currentMouseButtons);

        if (_mouse != null)
        {
            for (var i = 0; i < MouseButtonCount; i++)
            {
                _currentMouseButtons[i] = _mouse.IsButtonPressed((MouseButton)i);
            }
        }
        else
        {
            Array.Clear(_currentMouseButtons);
        }

        var currentPos = _mouse?.Position ?? System.Numerics.Vector2.Zero;
        var mousePosition = new Vector2D<float>(currentPos.X, currentPos.Y);
        var previousMousePosition = new Vector2D<float>(_previousMousePosition.X, _previousMousePosition.Y);
        _previousMousePosition = currentPos;

        var scrollDelta = new Vector2D<float>(_scrollDeltaX, _scrollDeltaY);
        _scrollDeltaX = 0;
        _scrollDeltaY = 0;

        var characters = _charBuffer;
        var characterCount = _charCount;
        _charCount = 0;

        if (characterCount >= _charBuffer.Length)
        {
            _charBuffer = new char[64];
        }

        return new InputSnapshot
        {
            Timestamp = timestamp,
            CurrentKeys = _currentKeys,
            PreviousKeys = _previousKeys,
            MousePosition = mousePosition,
            PreviousMousePosition = previousMousePosition,
            CurrentMouseButtons = _currentMouseButtons,
            PreviousMouseButtons = _previousMouseButtons,
            ScrollDelta = scrollDelta,
            CharacterBuffer = characters,
            CharacterCount = characterCount
        };
    }

    public G2CursorMode CursorMode
    {
        get => (G2CursorMode)(_mouse?.Cursor.CursorMode ?? Silk.NET.Input.CursorMode.Normal);
        set => _mouse?.Cursor.CursorMode = (CursorMode)value;
    }

    public string? ClipboardText
    {
        get => _keyboard?.ClipboardText;
        set
        {
            if (_keyboard != null && value != null)
            {
                _keyboard.ClipboardText = value;
            }
        }
    }

    private void OnKeyChar(IKeyboard keyboard, char c)
    {
        if (_charCount >= _charBuffer.Length)
        {
            Array.Resize(ref _charBuffer, _charBuffer.Length * 2);
        }

        _charBuffer[_charCount++] = c;
    }

    private void OnScroll(IMouse mouse, ScrollWheel wheel)
    {
        _scrollDeltaX += wheel.X;
        _scrollDeltaY += wheel.Y;
    }

    private static readonly Key[] PollableKeys = BuildPollableKeys();

    private static Key[] BuildPollableKeys()
    {
        var values = new List<Key>();
        foreach (var key in Enum.GetValues<Key>())
        {
            if ((int)key >= 0 && (int)key < KeyCount)
            {
                values.Add(key);
            }
        }

        return values.ToArray();
    }

    public void Dispose()
    {
        _context.Dispose();
    }
}
