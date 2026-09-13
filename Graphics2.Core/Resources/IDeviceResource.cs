namespace Graphics2.Core.Resources;

/// <summary>
///     A resource owned by the device. <see cref="IDeviceResource.Dispose"/> is safe to call at any time and waits for the device to go idle before destroying the resource.
///     <see cref="IDeviceResource.Destroy"/> does not wait and is only safe once the GPU is finished with the resource, which the frame disposal queue establishes.
///     Both entry points are idempotent.
/// </summary>
public interface IDeviceResource : IDisposable
{
    /// <summary>
    ///     Destroys the resource without waiting for the device to go idle.
    ///     Only call this when the GPU is known to be past the resource.
    /// </summary>
    void Destroy();
}
