using Graphics2.Allocator;
using Graphics2.Core.Bootstrap;
using Silk.NET.Vulkan;

namespace Graphics2.Core.Resources;

public sealed unsafe class VulkanResourceFactory : IDisposable
{
    private const long DefaultBlockSize = 256 * 1024 * 1024;

    private readonly VkDevice _device;
    private readonly BlockAllocator<VulkanMemory> _gpuOnly;
    private readonly BlockAllocator<VulkanMemory> _cpuToGpu;
    private readonly BlockAllocator<VulkanMemory> _gpuToCpu;
    private int _disposed;

    public VulkanResourceFactory(VkDevice device)
    {
        _device = device;

        var gpuOnlyLayer = VulkanAllocationLayer.Create(device, MemoryUsage.GpuOnly);
        var cpuToGpuLayer = VulkanAllocationLayer.Create(device, MemoryUsage.CpuToGpu);
        var gpuToCpuLayer = VulkanAllocationLayer.Create(device, MemoryUsage.GpuToCpu);

        _gpuOnly = new BlockAllocator<VulkanMemory>(DefaultBlockSize, gpuOnlyLayer);
        _cpuToGpu = new BlockAllocator<VulkanMemory>(DefaultBlockSize, cpuToGpuLayer);
        _gpuToCpu = new BlockAllocator<VulkanMemory>(DefaultBlockSize, gpuToCpuLayer);
    }

    public VkBuffer CreateBuffer(BufferDescription description)
    {
        if (description.Size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(description), "Size must be positive.");
        }

        var vk = _device.Instance.Vk;

        var vkUsage = BufferUsageToVk(description.Usage);
        var createInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)description.Size,
            Usage = vkUsage,
            SharingMode = SharingMode.Exclusive
        };

        if (vk.CreateBuffer(_device.Device, in createInfo, null, out var bufferHandle) != Result.Success)
        {
            throw new VkBootstrapException("Failed to create buffer.");
        }

        var requirements = new MemoryRequirements2
        {
            SType = StructureType.MemoryRequirements2
        };
        
        var memoryDedicatedInfo = new MemoryDedicatedRequirements
        {
            SType = StructureType.MemoryDedicatedRequirements
        };
        
        requirements.PNext = &memoryDedicatedInfo;

        var bufferMemoryInfo = new BufferMemoryRequirementsInfo2
        {
            SType = StructureType.BufferMemoryRequirementsInfo2,
            Buffer = bufferHandle
        };
        
        vk.GetBufferMemoryRequirements2(_device.Device, in bufferMemoryInfo, &requirements);

        var allocator = BufferUsageToAllocator(description.Usage);
        var allocation = allocator.Allocate((long)requirements.MemoryRequirements.Size, (long)requirements.MemoryRequirements.Alignment);
        var memory = allocator.GetBase(allocation);

        if (vk.BindBufferMemory(_device.Device, bufferHandle, memory.Handle, (ulong)allocation.Offset) != Result.Success)
        {
            allocator.Free(allocation);
            vk.DestroyBuffer(_device.Device, bufferHandle, null);
            throw new VkBootstrapException("Failed to bind buffer memory.");
        }

        return new VkBuffer(_device, allocator, bufferHandle, allocation, memory, description.Size, BufferUsageToMemoryUsage(description.Usage));
    }

    public VkShader CreateShader(ShaderDescription description)
    {
        var vk = _device.Instance.Vk;

        fixed (byte* spirV = description.SpirV.Span)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)description.SpirV.Length,
                PCode = (uint*)spirV
            };

            if (vk.CreateShaderModule(_device.Device, in createInfo, null, out var shaderModule) != Result.Success)
            {
                throw new VkBootstrapException("Failed to create shader module.");
            }

            return new VkShader(_device, shaderModule, description.Stage, description.EntryPoint);
        }
    }

    private BlockAllocator<VulkanMemory> BufferUsageToAllocator(BufferUsage usage)
    {
        var memoryUsage = BufferUsageToMemoryUsage(usage);
        return memoryUsage switch
        {
            MemoryUsage.GpuOnly => _gpuOnly,
            MemoryUsage.CpuToGpu => _cpuToGpu,
            MemoryUsage.GpuToCpu => _gpuToCpu,
            _ => throw new ArgumentOutOfRangeException(nameof(usage))
        };
    }

    private static MemoryUsage BufferUsageToMemoryUsage(BufferUsage usage)
    {
        if (usage.HasFlag(BufferUsage.Staging) || usage.HasFlag(BufferUsage.Uniform))
        {
            return MemoryUsage.CpuToGpu;
        }

        return MemoryUsage.GpuOnly;
    }

    private static BufferUsageFlags BufferUsageToVk(BufferUsage usage)
    {
        var flags = BufferUsageFlags.None;

        if (usage.HasFlag(BufferUsage.Vertex))
        {
            flags |= BufferUsageFlags.VertexBufferBit;
        }
        
        if (usage.HasFlag(BufferUsage.Index))
        {
            flags |= BufferUsageFlags.IndexBufferBit;
        }
        
        if (usage.HasFlag(BufferUsage.Uniform))
        {
            flags |= BufferUsageFlags.UniformBufferBit;
        }
        
        if (usage.HasFlag(BufferUsage.Storage))
        {
            flags |= BufferUsageFlags.StorageBufferBit;
        }
        
        if (usage.HasFlag(BufferUsage.Staging))
        {
            flags |= BufferUsageFlags.TransferSrcBit;
        }
        
        if (usage.HasFlag(BufferUsage.Indirect))
        {
            flags |= BufferUsageFlags.IndirectBufferBit;
        }

        return flags;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var vk = _device.Instance.Vk;
        vk.DeviceWaitIdle(_device.Device);

        _gpuOnly.Dispose();
        _cpuToGpu.Dispose();
        _gpuToCpu.Dispose();
    }
}
