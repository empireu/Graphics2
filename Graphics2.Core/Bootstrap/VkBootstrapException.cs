namespace Graphics2.Core.Bootstrap;

/// <summary>
///     Thrown during vulkan setup.
/// </summary>
/// <param name="message"></param>
public sealed class VkBootstrapException(string message) : VkException(message);