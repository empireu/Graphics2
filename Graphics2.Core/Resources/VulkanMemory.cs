using Silk.NET.Vulkan;

namespace Graphics2.Core.Resources;

internal readonly struct VulkanMemory(DeviceMemory handle, nint mappedBase)
{
    public readonly DeviceMemory Handle = handle;
    public readonly nint MappedBase = mappedBase;
    
    public bool IsMapped => MappedBase != 0;
}
