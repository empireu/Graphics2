using Graphics2.Allocator;
using Graphics2.Core.Bootstrap;
using Silk.NET.Vulkan;

namespace Graphics2.Core.Resources;

internal sealed unsafe class VulkanAllocationLayer : IAllocationLayer<VulkanMemory>
{
    private readonly VkDevice _device;
    private readonly uint _memoryTypeIndex;
    private readonly bool _mapMemory;

    private VulkanAllocationLayer(VkDevice device, uint memoryTypeIndex, bool mapMemory)
    {
        _device = device;
        _memoryTypeIndex = memoryTypeIndex;
        _mapMemory = mapMemory;
    }

    public static VulkanAllocationLayer Create(VkDevice device, MemoryUsage usage)
    {
        var vk = device.Instance.Vk;
        vk.GetPhysicalDeviceMemoryProperties(device.Info.PhysicalDevice, out var memoryProperties);

        var requiredFlags = usage switch
        {
            MemoryUsage.GpuOnly => MemoryPropertyFlags.DeviceLocalBit,
            MemoryUsage.CpuToGpu => MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            MemoryUsage.GpuToCpu => MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit,
            _ => throw new ArgumentOutOfRangeException(nameof(usage))
        };

        var allFlags = new int[memoryProperties.MemoryTypeCount];
        for (uint i = 0; i < memoryProperties.MemoryTypeCount; i++)
        {
            var type = memoryProperties.MemoryTypes[(int)i];
            
            allFlags[i] = (int)type.PropertyFlags;
            
            if ((type.PropertyFlags & requiredFlags) != requiredFlags)
            {
                continue;
            }

            return new VulkanAllocationLayer(device, i, usage != MemoryUsage.GpuOnly);
        }

        throw new VkAllocationException($"No memory type found for usage {usage} ({requiredFlags}, {string.Join(", ", allFlags)})");
    }

    public VulkanMemory AllocatePage(long size)
    {
        var vk = _device.Instance.Vk;

        var allocateInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = (ulong)size,
            MemoryTypeIndex = _memoryTypeIndex
        };

        if (vk.AllocateMemory(_device.Device, in allocateInfo, null, out var deviceMemory) != Result.Success)
        {
            throw new VkAllocationException($"Failed to allocate device memory {size}");
        }

        nint mappedBase = 0;
        if (_mapMemory)
        {
            void* ptr = null;
            if (vk.MapMemory(_device.Device, deviceMemory, 0, (ulong)size, MemoryMapFlags.None, &ptr) != Result.Success)
            {
                vk.FreeMemory(_device.Device, deviceMemory, null);
                
                throw new VkAllocationException("Failed to map memory");
            }

            if (ptr == null)
            {
                // To satisfy the linter:
                throw new VkAllocationException("Pointer null after map");
            }

            mappedBase = (nint)ptr;
        }

        return new VulkanMemory(deviceMemory, mappedBase);
    }

    public void FreePage(VulkanMemory handle, long size)
    {
        var vk = _device.Instance.Vk;

        if (handle.IsMapped)
        {
            vk.UnmapMemory(_device.Device, handle.Handle);
        }

        vk.FreeMemory(_device.Device, handle.Handle, null);
    }
}
