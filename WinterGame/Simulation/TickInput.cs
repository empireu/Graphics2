using Graphics2.Windowing.Input;

namespace WinterGame.Simulation;

/// <summary>
///     The input of every frame since the last tick, combined into what one tick reads.
///     A key is down when it was down in the latest frame, and pressed when any of those frames pressed it, so a tap between two ticks is not lost.
/// </summary>
internal sealed class TickInput
{
    private bool[] _keysDown = [];
    private bool[] _keysPressed = [];

    /// <summary>
    ///     The camera's yaw when the tick was handed its input. The render thread owns the rotation and the tick only reads it.
    /// </summary>
    public float Yaw;

    /// <summary>
    ///     The camera's pitch when the tick was handed its input.
    /// </summary>
    public float Pitch;

    public void AddFrame(InputSnapshot input)
    {
        var keyCount = input.CurrentKeys.Count;

        if (_keysDown.Length != keyCount)
        {
            _keysDown = new bool[keyCount];
            _keysPressed = new bool[keyCount];
        }

        for (var key = 0; key < keyCount; key++)
        {
            var isDown = input.CurrentKeys[key];

            _keysDown[key] = isDown;
            _keysPressed[key] |= isDown && !input.PreviousKeys[key];
        }
    }

    /// <summary>
    ///     Releases every key, for the frames in which the game does not have focus.
    /// </summary>
    public void ReleaseKeys()
    {
        Array.Clear(_keysDown);
    }

    /// <summary>
    ///     Forgets the presses a tick has read.
    /// </summary>
    public void ClearPresses()
    {
        Array.Clear(_keysPressed);
    }

    /// <summary>
    ///     Overwrites <paramref name="destination"/> with this input, so the game thread can tick from a copy the render thread no longer writes.
    /// </summary>
    public void CopyTo(TickInput destination)
    {
        if (destination._keysDown.Length != _keysDown.Length)
        {
            destination._keysDown = new bool[_keysDown.Length];
            destination._keysPressed = new bool[_keysPressed.Length];
        }

        _keysDown.CopyTo(destination._keysDown, 0);
        _keysPressed.CopyTo(destination._keysPressed, 0);
        destination.Yaw = Yaw;
        destination.Pitch = Pitch;
    }

    public bool IsKeyDown(G2Key key)
    {
        var index = (int)key;
        return index >= 0 && index < _keysDown.Length && _keysDown[index];
    }

    public bool WasKeyPressed(G2Key key)
    {
        var index = (int)key;
        return index >= 0 && index < _keysPressed.Length && _keysPressed[index];
    }
}
