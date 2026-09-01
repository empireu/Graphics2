namespace Graphics2.Core;

/// <summary>
///     Thrown when a vulkan-related operation fails.
/// </summary>
/// <param name="message"></param>
public class VkException(string message) : Exception(message);