using System.Diagnostics;

namespace WinterGame.Core;

public sealed class FrameTimeTracker
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly double[] _samples;
    private readonly double[] _sorted;

    private int _nextIndex;
    private double _lastSeconds;
    private bool _primed;

    public FrameTimeTracker(int capacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);

        _samples = new double[capacity];
        _sorted = new double[capacity];
    }

    public void Update()
    {
        var seconds = _stopwatch.Elapsed.TotalSeconds;
        _stopwatch.Restart();

        if (!_primed)
        {
            _primed = true;
            return;
        }

        _lastSeconds = seconds;
        TotalFrames++;

        var length = WindowFrames;

        if (length == _samples.Length)
        {
            var oldest = _samples[_nextIndex];
            var removal = Array.BinarySearch(_sorted, 0, length, oldest);
            Debug.Assert(removal >= 0);

            Array.Copy(_sorted, removal + 1, _sorted, removal, length - removal - 1);
            WindowSeconds -= oldest;
            length--;
        }

        var index = Array.BinarySearch(_sorted, 0, length, seconds);

        if (index < 0)
        {
            index = ~index;
        }

        if (index < length)
        {
            Array.Copy(_sorted, index, _sorted, index + 1, length - index);
        }

        _sorted[index] = seconds;
        _samples[_nextIndex] = seconds;
        _nextIndex = _nextIndex + 1 == _samples.Length ? 0 : _nextIndex + 1;
        WindowSeconds += seconds;
        WindowFrames = length + 1;
    }
    
    public int WindowFrames { get; private set; }

    public long TotalFrames { get; private set; }

    public double WindowSeconds { get; private set; }

    public double LastMilliseconds => _lastSeconds * 1000.0;

    public double AverageMilliseconds => WindowFrames == 0 
        ? 0.0
        : WindowSeconds / WindowFrames * 1000.0;

    public double FramesPerSecond => WindowSeconds <= 0.0 
        ? 0.0
        : WindowFrames / WindowSeconds;

    public double MinimumMilliseconds => WindowFrames == 0 
        ? 0.0 
        : _sorted[0] * 1000.0;

    public double MaximumMilliseconds => WindowFrames == 0 
        ? 0.0 
        : _sorted[WindowFrames - 1] * 1000.0;

    public double MedianMilliseconds => PercentileMilliseconds(0.5);

    public double P95Milliseconds => PercentileMilliseconds(0.95);

    public double P99Milliseconds => PercentileMilliseconds(0.99);
    
    private double PercentileMilliseconds(double percentile)
    {
        if (WindowFrames == 0)
        {
            return 0.0;
        }

        var rank = (int)Math.Ceiling(percentile * WindowFrames) - 1;

        return _sorted[Math.Clamp(rank, 0, WindowFrames - 1)] * 1000.0;
    }
}
