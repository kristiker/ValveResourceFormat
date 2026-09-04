using Microsoft.Extensions.Logging;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// Owns the <see cref="VkInstance"/> and, in debug builds, the validation layer's debug messenger.
/// One per process; every <see cref="VulkanDevice"/> is created against it.
/// </summary>
public sealed unsafe class VulkanInstance : IDisposable
{
    private const string ValidationLayerName = "VK_LAYER_KHRONOS_validation";

    /// <summary>The loader-level instance handle.</summary>
    public VkInstance Handle { get; }

    /// <summary>Instance-level function pointers, dispatched against <see cref="Handle"/>.</summary>
    public VkInstanceApi Api { get; }

    private readonly VkDebugUtilsMessengerEXT debugMessenger;
    private readonly ILogger logger;
    private readonly DebugUtilsMessengerCallback? debugCallback;

    // Kept alive for the process: the messenger callback native code holds a raw function pointer to it.
    private delegate uint DebugUtilsMessengerCallback(VkDebugUtilsMessageSeverityFlagsEXT severity, VkDebugUtilsMessageTypeFlagsEXT type, VkDebugUtilsMessengerCallbackDataEXT* data, void* userData);

    private VulkanInstance(VkInstance handle, VkInstanceApi api, VkDebugUtilsMessengerEXT debugMessenger, ILogger logger, DebugUtilsMessengerCallback? debugCallback)
    {
        Handle = handle;
        Api = api;
        this.debugMessenger = debugMessenger;
        this.logger = logger;
        this.debugCallback = debugCallback;
    }

    /// <summary>
    /// Creates the instance, enabling the given platform surface extension (e.g. <c>VK_KHR_win32_surface</c>)
    /// alongside <c>VK_KHR_surface</c>. The validation layer and <c>VK_EXT_debug_utils</c> are enabled in
    /// debug builds when the layer is actually installed; its absence is a warning, not a failure.
    /// </summary>
    public static VulkanInstance Create(ILogger logger, string platformSurfaceExtension)
    {
        vkInitialize().CheckResult();

        var extensions = new List<string> { "VK_KHR_surface", platformSurfaceExtension };
        var layers = new List<string>();

#if DEBUG
        if (IsLayerAvailable(ValidationLayerName))
        {
            layers.Add(ValidationLayerName);
            extensions.Add("VK_EXT_debug_utils");
        }
        else
        {
            logger.LogWarning("{Layer} is not installed; Vulkan validation is disabled", ValidationLayerName);
        }
#endif

        using var extensionArray = new VkStringArray(extensions);
        using var layerArray = new VkStringArray(layers);

        VkUtf8String appName = "Source 2 Viewer"u8;

        var appInfo = new VkApplicationInfo
        {
            pApplicationName = appName,
            applicationVersion = new VkVersion(1, 0, 0),
            pEngineName = appName,
            engineVersion = new VkVersion(1, 0, 0),
            apiVersion = VkVersion.Version_1_4,
        };

        var createInfo = new VkInstanceCreateInfo
        {
            pApplicationInfo = &appInfo,
            enabledExtensionCount = extensionArray.Length,
            ppEnabledExtensionNames = extensionArray,
            enabledLayerCount = layerArray.Length,
            ppEnabledLayerNames = layerArray,
        };

        vkCreateInstance(&createInfo, null, out var instance).CheckResult();

        var api = new VkInstanceApi(instance);

        VkDebugUtilsMessengerEXT messenger = default;
        DebugUtilsMessengerCallback? callback = null;

#if DEBUG
        if (layers.Count > 0)
        {
            callback = (severity, type, data, userData) => DebugCallback(logger, severity, data);

            var messengerInfo = new VkDebugUtilsMessengerCreateInfoEXT
            {
                messageSeverity = VkDebugUtilsMessageSeverityFlagsEXT.Warning | VkDebugUtilsMessageSeverityFlagsEXT.Error,
                messageType = VkDebugUtilsMessageTypeFlagsEXT.General | VkDebugUtilsMessageTypeFlagsEXT.Validation | VkDebugUtilsMessageTypeFlagsEXT.Performance,
                pfnUserCallback = (delegate* unmanaged<VkDebugUtilsMessageSeverityFlagsEXT, VkDebugUtilsMessageTypeFlagsEXT, VkDebugUtilsMessengerCallbackDataEXT*, void*, uint>)
                    System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(callback),
            };

            api.vkCreateDebugUtilsMessengerEXT(&messengerInfo, null, out messenger).CheckResult();
        }
#endif

        return new VulkanInstance(instance, api, messenger, logger, callback);
    }

    private static uint DebugCallback(ILogger logger, VkDebugUtilsMessageSeverityFlagsEXT severity, VkDebugUtilsMessengerCallbackDataEXT* data)
    {
        var message = new string((sbyte*)data->pMessage);

        if (severity.HasFlag(VkDebugUtilsMessageSeverityFlagsEXT.Error))
        {
            logger.LogError("[Vulkan] {Message}", message);
        }
        else
        {
            logger.LogWarning("[Vulkan] {Message}", message);
        }

        return VK_FALSE;
    }

    private static bool IsLayerAvailable(string name)
    {
        uint count = 0;
        vkEnumerateInstanceLayerProperties(&count, null).CheckResult();

        var layers = new VkLayerProperties[count];

        fixed (VkLayerProperties* pLayers = layers)
        {
            vkEnumerateInstanceLayerProperties(&count, pLayers).CheckResult();

            for (var i = 0; i < count; i++)
            {
                if (new string((sbyte*)pLayers[i].layerName) == name)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (debugMessenger.IsNotNull)
        {
            Api.vkDestroyDebugUtilsMessengerEXT(debugMessenger);
        }

        Api.vkDestroyInstance();
    }
}
