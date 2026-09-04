namespace Graphics2.Allocator;

/// <summary>
///     Handler for allocating the backing memory pages from the device. 
/// </summary>
/// <typeparam name="TBlockHandle"></typeparam>
public interface IAllocationLayer<TBlockHandle>
{
    /// <summary>
    ///     Allocates a page of the specified size, and returns the handle.
    /// </summary>
    public TBlockHandle AllocatePage(long size);

    /// <summary>
    ///     Frees the page by its handle.
    /// </summary>
    public void FreePage(TBlockHandle handle, long size);
}