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
