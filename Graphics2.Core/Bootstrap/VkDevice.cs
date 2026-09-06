using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
// ReSharper disable UnusedAutoPropertyAccessor.Local

namespace Graphics2.Core.Bootstrap;

public sealed class VkDeviceDescription
{
    // ReSharper disable once CollectionNeverUpdated.Global
    // ReSharper disable once FieldCanBeMadeReadOnly.Global
    public List<string> AdditionalExtensions = [];
}

public sealed unsafe class VkDevice : IDisposable
{
    private int _disposed;
    
    private VkDevice(VkInstance instance, DeviceInfo info, Device device, Queue queue)
    {
        Instance = instance;
        Info = info;
        Device = device;
        Queue = queue;
    }
    
    public readonly VkInstance Instance;
    public readonly DeviceInfo Info;
    public readonly Device Device;
    public readonly Queue Queue;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Instance.Vk.DestroyDevice(Device, null);
    }

    public static VkDevice Create(VkInstance instance, SurfaceKHR surface, VkDeviceDescription description)
    {
        if (!instance.Vk.TryGetInstanceExtension<KhrSurface>(instance.Handle, out var khrSurface))
        {
            throw new VkBootstrapException("Failed to get KHR surface extension");
        }

        var extensions = description.AdditionalExtensions.ToList();
        extensions.Add(KhrSwapchain.ExtensionName);

        var pickedInfo = PickGraphicsDevice(
            instance,
            extensions,
            new SurfaceTarget(khrSurface, surface),
            description
        );

        var queuePriority = 1.0f;
        var queueCreateInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = pickedInfo.ChosenGraphicsPresentFamily,
            QueueCount = 1,
            PQueuePriorities = &queuePriority
        };
        
        PhysicalDeviceVulkan11Features vk11Features = new()
        {
            SType = StructureType.PhysicalDeviceVulkan11Features,
            ShaderDrawParameters = true
        };
        
        PhysicalDeviceVulkan12Features vk12Features = new()
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            DescriptorIndexing = true,
            ShaderSampledImageArrayNonUniformIndexing = true,
            RuntimeDescriptorArray = true,
            DescriptorBindingSampledImageUpdateAfterBind = true,
            DescriptorBindingPartiallyBound = true,
            DescriptorBindingUpdateUnusedWhilePending = true
        };
        
        PhysicalDeviceVulkan13Features vk13Features = new()
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            DynamicRendering = true,
            Synchronization2 = true,
        };
        
        PhysicalDeviceFeatures2 features2 = new()
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &vk11Features,
            Features = new PhysicalDeviceFeatures
            {
                SamplerAnisotropy = true
            }
        };
        
        vk11Features.PNext = &vk12Features;
        vk12Features.PNext = &vk13Features;


        Device logicalDevice;
        
        var ppEnabledExtensions = (byte**)SilkMarshal.StringArrayToPtr(extensions);

        try
        {
            DeviceCreateInfo createInfo = new()
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = &features2,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueCreateInfo,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = ppEnabledExtensions
            };
            
            if (instance.Vk.CreateDevice(pickedInfo.PhysicalDevice, &createInfo, null, out logicalDevice) != Result.Success)
            {
                throw new Exception("Failed to create logical device");
            }
        }
        finally
        {
            SilkMarshal.Free((IntPtr)ppEnabledExtensions);
        }
        
        var queue = instance.Vk.GetDeviceQueue(logicalDevice, pickedInfo.ChosenGraphicsPresentFamily, 0);
        
        return new VkDevice(instance, pickedInfo, logicalDevice, queue);
    }

    private static DeviceInfo PickGraphicsDevice(VkInstance instance, List<string> extensions, SurfaceTarget surfaceTarget, VkDeviceDescription description)
    {
        uint physicalDeviceCount;
        if (instance.Vk.EnumeratePhysicalDevices(instance.Handle, &physicalDeviceCount, null) != Result.Success)
        {
            throw new VkBootstrapException("Failed to get physical device count");
        }

        if (physicalDeviceCount == 0)
        {
            throw new VkBootstrapException("Got 0 physical devices");
        }
        
        Span<PhysicalDevice> physicalDevices = stackalloc PhysicalDevice[(int)physicalDeviceCount];
        if (instance.Vk.EnumeratePhysicalDevices(instance.Handle, ref physicalDeviceCount, ref physicalDevices.GetPinnableReference()) != Result.Success)
        {
            throw new VkBootstrapException("Failed to enumerate physical devices after count");
        }
        
        var discreteDevices = physicalDevices.ToArray().Where(device =>
        {
            instance.Vk.GetPhysicalDeviceProperties(device, out var properties);

            return properties.DeviceType == PhysicalDeviceType.DiscreteGpu;
        });

        if (TryFindDevice(instance, surfaceTarget, extensions, discreteDevices, out var discreteDevice))
        {
            return discreteDevice;
        }
        
        var integratedDevices = physicalDevices.ToArray().Where(device =>
        {
            instance.Vk.GetPhysicalDeviceProperties(device, out var properties);

            return properties.DeviceType == PhysicalDeviceType.IntegratedGpu;
        });
        
        if (TryFindDevice(instance, surfaceTarget, extensions, integratedDevices, out var integratedGpu))
        {
            return integratedGpu;
        }

        throw new VkBootstrapException("Failed to find suitable device");
    }

    // ReSharper disable UnusedAutoPropertyAccessor.Global
    public readonly struct DeviceInfo
    {
        public PhysicalDevice PhysicalDevice { get; init; }
        
        public QueueFamilyScanResults FamilyScanResults { get; init; }
        
        public uint ChosenGraphicsPresentFamily { get; init; }
        
        public PhysicalDeviceVulkan11Features FeaturesVk11 { get; init; }
        
        public PhysicalDeviceVulkan12Features FeaturesVk12 { get; init; }
        
        public PhysicalDeviceVulkan13Features FeaturesVk13 { get; init; }
        
        public PhysicalDeviceFeatures2 Features2 { get; init; }
        
        public ulong DeviceLocalMemory { get; init; }

        public ulong MinUniformBufferOffsetAlignment { get; init; }
    }
    // ReSharper restore UnusedAutoPropertyAccessor.Global

    private static bool TryFindDevice(
        VkInstance instance,
        SurfaceTarget surfaceTarget,
        List<string> extensions,
        IEnumerable<PhysicalDevice> devices,
        out DeviceInfo result)
    {
        var candidates = devices.Select<PhysicalDevice, DeviceInfo?>(device =>
        {
            instance.Vk.GetPhysicalDeviceProperties(device, out var properties);

            if (properties.ApiVersion < Vk.Version13.Value)
            {
                return null;
            }

            var scanResults = FindGraphicsFamilyIndices(instance, device, surfaceTarget);
            var intersection = scanResults.Intersect();
            
            if (intersection.Count == 0)
            {
                return null;
            }

            uint extensionCount = 0;
            instance.Vk.EnumerateDeviceExtensionProperties(device, (byte*)0, &extensionCount, (ExtensionProperties*)0);

            if (extensionCount == 0)
            {
                return null;
            }
            
            Span<ExtensionProperties> availableExtensions = stackalloc ExtensionProperties[(int)extensionCount];
            instance.Vk.EnumerateDeviceExtensionProperties(device, (byte*)0, ref extensionCount, ref availableExtensions.GetPinnableReference());

            var availableExtensionNames = new List<string>();
            foreach (var availableExtension in availableExtensions[..(int)extensionCount])
            {
                availableExtensionNames.Add(SilkMarshal.PtrToString((IntPtr)availableExtension.ExtensionName)!);
            }

            if (!extensions.All(availableExtensionNames.Contains))
            {
                return null;
            }

            PhysicalDeviceVulkan11Features vk11Features = new()
            {
                SType = StructureType.PhysicalDeviceVulkan11Features
            };
            PhysicalDeviceVulkan12Features vk12Features = new()
            {
                SType = StructureType.PhysicalDeviceVulkan12Features
            };

            PhysicalDeviceVulkan13Features vk13Features = new()
            {
                SType = StructureType.PhysicalDeviceVulkan13Features
            };

            PhysicalDeviceFeatures2 features2 = new()
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &vk11Features
            };

            vk11Features.PNext = &vk12Features;
            vk12Features.PNext = &vk13Features;

            instance.Vk.GetPhysicalDeviceFeatures2(device, &features2);

            if (!(vk11Features.ShaderDrawParameters
                  && vk12Features.DescriptorIndexing
                  && vk12Features.ShaderSampledImageArrayNonUniformIndexing
                  && vk12Features.RuntimeDescriptorArray
                  && vk12Features.DescriptorBindingSampledImageUpdateAfterBind
                  && vk12Features.DescriptorBindingPartiallyBound
                  && vk12Features.DescriptorBindingUpdateUnusedWhilePending
                  && vk13Features.DynamicRendering
                  && features2.Features.SamplerAnisotropy))
            {
                return null;
            }

            return new DeviceInfo
            {
                PhysicalDevice = device,
                FamilyScanResults = scanResults,
                ChosenGraphicsPresentFamily = intersection[0],
                FeaturesVk11 = vk11Features,
                FeaturesVk12 = vk12Features,
                FeaturesVk13 = vk13Features,
                Features2 = features2,
                DeviceLocalMemory = DeviceLocalMemoryBytes(instance.Vk, device),
                MinUniformBufferOffsetAlignment = properties.Limits.MinUniformBufferOffsetAlignment
            };
        }).Where(x => x.HasValue).Select(x => x!.Value).ToList();

        if (candidates.Count == 0)
        {
            result = default;
            return false;
        }

        result = candidates.MaxBy(x => x.DeviceLocalMemory);

        return true;
    }
    
    private static ulong DeviceLocalMemoryBytes(Vk vk, PhysicalDevice device)
    {
        vk.GetPhysicalDeviceMemoryProperties(device, out var memoryProperties);
        
        var total = 0ul;
        for (var i = 0; i < memoryProperties.MemoryHeapCount; i++)
        {
            if (memoryProperties.MemoryHeaps[i].Flags.HasFlag(MemoryHeapFlags.DeviceLocalBit))
            {
                total += memoryProperties.MemoryHeaps[i].Size;
            }
        }
        
        return total;
    }
    
    private readonly struct SurfaceTarget(KhrSurface khrSurface, SurfaceKHR surface)
    {
        public readonly KhrSurface KhrSurface = khrSurface;
        public readonly SurfaceKHR Surface = surface;
    }

    public readonly struct QueueFamilyScanResults(List<uint> graphicsFamilies, List<uint> presentFamilies)
    {
        public readonly List<uint> GraphicsFamilies = graphicsFamilies;
        public readonly List<uint> PresentFamilies = presentFamilies;

        public List<uint> Intersect()
        {
            var families = new[]
            {
                GraphicsFamilies, PresentFamilies
            };

            return families[0].Where(v => families.Skip(1).All(family => family.Contains(v))).ToList();
        }
    }
    
    private static QueueFamilyScanResults FindGraphicsFamilyIndices(VkInstance instance, PhysicalDevice device, SurfaceTarget surfaceTarget)
    {
        uint queueFamilyCount = 0;
        instance.Vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueFamilyCount, null);

        if (queueFamilyCount == 0)
        {
            throw new VkBootstrapException("Got 0 queue families");
        }
        
        Span<QueueFamilyProperties> queueFamilies = stackalloc QueueFamilyProperties[(int)queueFamilyCount];
        instance.Vk.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, out queueFamilies.GetPinnableReference());

        var graphicsFamilies = new List<uint>();
        var presentFamilies = new List<uint>();
        
        for (var index = 0u; index < queueFamilyCount; index++)
        {
            if (queueFamilies[(int)index].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                graphicsFamilies.Add(index);
            }

            surfaceTarget.KhrSurface.GetPhysicalDeviceSurfaceSupport(
                device,
                index,
                surfaceTarget.Surface,
                out var presentSupport
            );

            if (presentSupport)
            {
                presentFamilies.Add(index);
            }
        }
        
        return new QueueFamilyScanResults(graphicsFamilies, presentFamilies);
    }
}