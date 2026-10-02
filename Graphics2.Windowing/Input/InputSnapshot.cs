using Silk.NET.Maths;

// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Graphics2.Windowing.Input;

public sealed class InputSnapshot
{
    /// <summary>
    ///     The <see cref="System.Diagnostics.Stopwatch"/> timestamp at which the window's events were pumped, which is the instant this input describes.
    /// </summary>
    public required long Timestamp { get; init; }

    public required IReadOnlyList<bool> CurrentKeys { get; init; }
    public required IReadOnlyList<bool> PreviousKeys { get; init; }
    
    public required Vector2D<float> MousePosition { get; init; }
    public required Vector2D<float> PreviousMousePosition { get; init; }
    
    public required IReadOnlyList<bool> CurrentMouseButtons { get; init; }
    public required IReadOnlyList<bool> PreviousMouseButtons { get; init; }
    
    public required Vector2D<float> ScrollDelta { get; init; }
    
    public required IReadOnlyList<char> CharacterBuffer { get; init; }
    public required int CharacterCount { get; init; }

    public bool IsKeyDown(G2Key key)
    {
        var i = (int)key;
        return i >= 0 && i < CurrentKeys.Count && CurrentKeys[i];
    }

    public bool WasKeyPressed(G2Key key)
    {
        var i = (int)key;
        return i >= 0 && i < CurrentKeys.Count && CurrentKeys[i] && !PreviousKeys[i];
    }

    public bool WasKeyReleased(G2Key key)
    {
        var i = (int)key;
        return i >= 0 && i < CurrentKeys.Count && !CurrentKeys[i] && PreviousKeys[i];
    }

    public bool IsMouseDown(G2MouseButton button)
    {
        var i = (int)button;
        return i >= 0 && i < CurrentMouseButtons.Count && CurrentMouseButtons[i];
    }

    public bool WasMousePressed(G2MouseButton button)
    {
        var i = (int)button;
        return i >= 0 && i < CurrentMouseButtons.Count && CurrentMouseButtons[i] && !PreviousMouseButtons[i];
    }

    public bool WasMouseReleased(G2MouseButton button)
    {
        var i = (int)button;
        return i >= 0 && i < CurrentMouseButtons.Count && !CurrentMouseButtons[i] && PreviousMouseButtons[i];
    }
}
