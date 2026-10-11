using Graphics2.Core.Bootstrap;
using Silk.NET.Vulkan;

namespace Graphics2.Core.Render;

/// <summary>
///     A frame slot's timestamp queries and the labels its frame wrote them under. The first query is the frame's start.
/// </summary>
internal sealed unsafe class FrameTimestamps : IDisposable
{
    private const int Capacity = Frame.MaxTimestamps + 1;

    private readonly VkDevice _device;
    private readonly QueryPool _pool;
    private readonly string[] _labels = new string[Capacity];
    private readonly ulong[] _ticks = new ulong[Capacity];

    /// <summary>
    ///     The bits of a timestamp that count, so a difference across a wrap still comes out right.
    /// </summary>
    private readonly ulong _validMask;

    private int _count;
    private int _disposed;

    private FrameTimestamps(VkDevice device, QueryPool pool)
    {
        _device = device;
        _pool = pool;

        var validBits = (int)device.Info.TimestampValidBits;
        _validMask = validBits >= 64 ? ulong.MaxValue : (1ul << validBits) - 1;
    }

    /// <summary>
    ///     Creates the queries, or returns null when the device's queue writes no timestamps.
    /// </summary>
    public static FrameTimestamps? Create(VkDevice device)
    {
        if (device.Info.TimestampValidBits == 0)
        {
            return null;
        }

        var createInfo = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = Capacity
        };

        if (device.Instance.Vk.CreateQueryPool(device.Device, in createInfo, null, out var pool) != Result.Success)
        {
            throw new VkBootstrapException("Failed to create timestamp query pool");
        }

        return new FrameTimestamps(device, pool);
    }

    /// <summary>
    ///     Writes the time between each of the slot's last frame's timestamps and the one before it into <paramref name="timings"/> and returns how many it wrote, or -1 when the results are not all available yet.
    ///     Only meaningful once the slot's fence has been waited for.
    /// </summary>
    public int Read(Span<GpuTiming> timings)
    {
        if (_count < 2)
        {
            return 0;
        }

        fixed (ulong* ticks = _ticks)
        {
            var result = _device.Instance.Vk.GetQueryPoolResults(_device.Device, _pool, 0, (uint)_count, (nuint)(_count * sizeof(ulong)), ticks, sizeof(ulong), QueryResultFlags.Result64Bit);

            if (result != Result.Success)
            {
                return -1;
            }
        }

        var millisecondsPerTick = _device.Info.TimestampPeriod * 1e-6;

        for (var index = 1; index < _count; index++)
        {
            var ticks = (_ticks[index] - _ticks[index - 1]) & _validMask;
            timings[index - 1] = new GpuTiming(_labels[index], ticks * millisecondsPerTick);
        }

        return _count - 1;
    }

    /// <summary>
    ///     Resets the queries and writes the frame's start. Has to be the first thing the frame records, outside any pass.
    /// </summary>
    public void Begin(CommandBuffer commandBuffer)
    {
        _device.Instance.Vk.CmdResetQueryPool(commandBuffer, _pool, 0, Capacity);
        _count = 0;

        Write(commandBuffer, string.Empty);
    }

    /// <summary>
    ///     Writes the time at which every command recorded before this one has finished.
    /// </summary>
    public void Write(CommandBuffer commandBuffer, string label)
    {
        if (_count == Capacity)
        {
            throw new InvalidOperationException($"A frame can write at most {Frame.MaxTimestamps} timestamps.");
        }

        _device.Instance.Vk.CmdWriteTimestamp2(commandBuffer, PipelineStageFlags2.AllCommandsBit, _pool, (uint)_count);
        _labels[_count] = label;
        _count++;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _device.Instance.Vk.DestroyQueryPool(_device.Device, _pool, null);
    }
}
