using Graphics2.Allocator;

namespace Graphics2.Tests;

internal sealed class MockAllocationLayer : IAllocationLayer<int>
{
    private int _nextHandle;

    public List<(int Handle, long Size)> Pages { get; } = [];

    public List<(int Handle, long Size)> FreedPages { get; } = [];

    public int AllocatePage(long size)
    {
        var handle = _nextHandle++;
        Pages.Add((handle, size));
        return handle;
    }

    public void FreePage(int handle, long size)
    {
        FreedPages.Add((handle, size));
    }
}
