using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace Graphics2.Core.Bootstrap;

public sealed record VkInstanceDescription
{
    /// <summary>
    ///     If true, the validation layers will be enabled.
    ///     Startup will fail if the SDK is not installed.
    /// </summary>
    public bool UseValidationLayers { get; init; }

    public string ApplicationName { get; init; } = "Graphics2";
    
    public SemVer ApplicationVersion { get; init; } = SemVer.Null;
    
    public List<string> ValidationLayers { get; init; } =
    [
        "VK_LAYER_KHRONOS_validation"
    ];

    /// <summary>
    ///     Extensions needed for validation are included implicitly, if validation is enabled.
    ///     This list has the additional extensions.
    /// </summary>
    // ReSharper disable once CollectionNeverUpdated.Global
    public List<string> AdditionalExtensions { get; init; } = [];

    public bool ValidationShowVerboseSeverity { get; init; }
    public bool ValidationShowWarningSeverity { get; init; } = true;
    public bool ValidationShowErrorSeverity { get; init; } = true;
    
    internal DebugUtilsMessageSeverityFlagsEXT MessageSeverityFlags
    {
        get
        {
            var flags = DebugUtilsMessageSeverityFlagsEXT.None;

            if (ValidationShowVerboseSeverity)
            {
                flags |= DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt;
            }

            if (ValidationShowWarningSeverity)
            {
                flags |= DebugUtilsMessageSeverityFlagsEXT.WarningBitExt;
            }

            if (ValidationShowErrorSeverity)
            {
                flags |= DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt;
            }

            return flags;
        }
    }

    public bool ValidationShowGeneralMessages { get; init; } = true;
    public bool ValidationShowPerformanceMessages { get; init; } = true;
    public bool ValidationShowValidationMessages { get; init; } = true;

    internal DebugUtilsMessageTypeFlagsEXT MessageTypeFlags
    {
        get
        {
            var flags = DebugUtilsMessageTypeFlagsEXT.None;

            if (ValidationShowGeneralMessages)
            {
                flags |= DebugUtilsMessageTypeFlagsEXT.GeneralBitExt;
            }

            if (ValidationShowPerformanceMessages)
            {
                flags |= DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt;
            }

            if (ValidationShowValidationMessages)
            {
                flags |= DebugUtilsMessageTypeFlagsEXT.ValidationBitExt;
            }

            return flags;
        }
    }
    
    /// <summary>
    ///     The validation message callback.
    /// </summary>
    public DebugUtilsMessengerCallbackFunctionEXT Callback { get; init; } = VkInstance.DefaultCallback;
}

public sealed unsafe class VkInstance : IDisposable
{
    public static readonly DebugUtilsMessengerCallbackFunctionEXT DefaultCallback = DefaultDebugCallback;
    
    public readonly Vk Vk;
    public readonly Instance Handle;
    private readonly Debugger? _debugger;

    private int _disposed;
    
    private VkInstance(Vk vk, Instance handle, Debugger? debugger)
    {
        Vk = vk;
        Handle = handle;
        _debugger = debugger;
    }
    
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _debugger?.DebugUtils.DestroyDebugUtilsMessenger(Handle, _debugger.DebugMessenger, null);
        Vk.DestroyInstance(Handle, null);
        Vk.Dispose();
    }
    
    public static VkInstance Create(VkInstanceDescription description)
    {
        var extensions = description.AdditionalExtensions.ToList();

        if (description.UseValidationLayers)
        {
            extensions.Add(ExtDebugUtils.ExtensionName);
        }
     
        var pApplicationName = (byte*)SilkMarshal.StringToPtr(description.ApplicationName);
        var pEngineName = (byte*)SilkMarshal.StringToPtr("No Engine");
        var ppEnabledExtensions = (byte**)SilkMarshal.StringArrayToPtr(extensions);
        var ppEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(description.ValidationLayers);
        
        try
        {
            var vk = Vk.GetApi();
        
            if (description.UseValidationLayers && !HasValidationSupport(vk, description.ValidationLayers, out var availableLayers))
            {
                vk.Dispose();
                
                throw new VkBootstrapException(
                    $"Requested validation layers ({string.Join(", ", description.ValidationLayers)}) aren't supported. " +
                    $"Available: {string.Join(", ", availableLayers)}"
                );
            }
        
            ApplicationInfo applicationInfo = new()
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = pApplicationName,
                ApplicationVersion = new Version32(
                    (uint)description.ApplicationVersion.Major, 
                    (uint)description.ApplicationVersion.Minor, 
                    (uint)description.ApplicationVersion.Patch
                ),
                PEngineName = pEngineName,
                EngineVersion = new Version32(1, 0, 0),
                ApiVersion = Vk.Version13
            };
      
            InstanceCreateInfo instanceCreateInfo = new()
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &applicationInfo,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = ppEnabledExtensions
            };

            if (!description.UseValidationLayers)
            {
                instanceCreateInfo.EnabledLayerCount = 0;
                instanceCreateInfo.PNext = null;
            }
            else
            {
                instanceCreateInfo.EnabledLayerCount = (uint)description.ValidationLayers.Count;
                instanceCreateInfo.PpEnabledLayerNames = ppEnabledLayerNames;

                DebugUtilsMessengerCreateInfoEXT debugCreateInfo = new()
                {
                    SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                    MessageSeverity = description.MessageSeverityFlags,
                    MessageType = description.MessageTypeFlags,
                    PfnUserCallback = description.Callback
                };

                instanceCreateInfo.PNext = &debugCreateInfo;
            }

            if (vk.CreateInstance(in instanceCreateInfo, null, out var instance) != Result.Success)
            {
                vk.Dispose();
                throw new VkBootstrapException("Failed to create instance");
            }

            var debugger = CreateDebugger(description, vk, instance);

            return new VkInstance(vk, instance, debugger);
        }
        finally
        {
            SilkMarshal.Free((IntPtr)pApplicationName);
            SilkMarshal.Free((IntPtr)pEngineName);
            SilkMarshal.Free((nint)ppEnabledExtensions);
            SilkMarshal.Free((nint)ppEnabledLayerNames);
        }
    }
    
    private static bool HasValidationSupport(Vk vk, List<string> validationLayers, out HashSet<string?> availableLayerNames)
    {
        uint layerCount = 0;
        vk.EnumerateInstanceLayerProperties(ref layerCount, null);
        var availableLayers = new LayerProperties[layerCount];
        fixed (LayerProperties* availableLayersPtr = availableLayers)
        {
            vk.EnumerateInstanceLayerProperties(ref layerCount, availableLayersPtr);
        }

        availableLayerNames = availableLayers.Select(layer => SilkMarshal.PtrToString((IntPtr)layer.LayerName)).ToHashSet();

        return validationLayers.All(availableLayerNames.Contains);
    }

    private static Debugger? CreateDebugger(VkInstanceDescription description, Vk vk, Instance instance)
    {
        if (!description.UseValidationLayers)
        {
            return null;
        }

        if (!vk.TryGetInstanceExtension<ExtDebugUtils>(instance, out var debugUtils))
        {
            vk.DestroyInstance(instance, null);
            vk.Dispose();
            throw new VkBootstrapException("Failed to get ext debug utils extension");
        }
    
        DebugUtilsMessengerCreateInfoEXT debugCreateInfo = new()
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = description.MessageSeverityFlags,
            MessageType = description.MessageTypeFlags,
            PfnUserCallback = description.Callback
        };

        if (debugUtils.CreateDebugUtilsMessenger(instance, in debugCreateInfo, null, out var debugMessenger) != Result.Success)
        {
            vk.DestroyInstance(instance, null);
            vk.Dispose();
            throw new VkBootstrapException("Failed to create debug messenger");
        }
        
        return new Debugger(debugUtils, debugMessenger, description.Callback);
    }
    
    private static uint DefaultDebugCallback(DebugUtilsMessageSeverityFlagsEXT messageSeverity, DebugUtilsMessageTypeFlagsEXT messageTypes, DebugUtilsMessengerCallbackDataEXT* pCallbackData, void* pUserData)
    {
        Console.WriteLine("Validation:" + SilkMarshal.PtrToString((nint)pCallbackData->PMessage));

        return Vk.False;
    }

    private sealed class Debugger(ExtDebugUtils debugUtils, DebugUtilsMessengerEXT debugMessenger, DebugUtilsMessengerCallbackFunctionEXT callback)
    {
        public readonly ExtDebugUtils DebugUtils = debugUtils;
        public readonly DebugUtilsMessengerEXT DebugMessenger = debugMessenger;
       
        // Prevents GC on this.
        // ReSharper disable once UnusedMember.Local
        private readonly DebugUtilsMessengerCallbackFunctionEXT _callback = callback;
    }
}