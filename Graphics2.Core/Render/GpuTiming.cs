namespace Graphics2.Core.Render;

/// <summary>
///     The GPU time from the timestamp before one a frame wrote with <see cref="Frame.WriteTimestamp"/>, or from the frame's start, to that one.
/// </summary>
public readonly struct GpuTiming(string label, double milliseconds)
{
    public readonly string Label = label;
    public readonly double Milliseconds = milliseconds;
}
