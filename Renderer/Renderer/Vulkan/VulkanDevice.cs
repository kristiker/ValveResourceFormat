using Microsoft.Extensions.Logging;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vma;
using static Vortice.Vulkan.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// Picks a physical device that can present to a given surface and support the features this
/// renderer requires, then creates the logical device, its one graphics/present queue, and a
/// command pool. One per <see cref="VulkanInstance"/> for now; sufficient until a second GPU
/// or a dedicated transfer queue is needed.
/// </summary>
public sealed unsafe class VulkanDevice : IDisposable
{
    /// <summary>The instance this device was created against.</summary>
    public VulkanInstance Instance { get; }

    /// <summary>The selected physical device.</summary>
    public VkPhysicalDevice PhysicalDevice { get; }

    /// <summary>The logical device handle.</summary>
    public VkDevice Handle { get; }

    /// <summary>Device-level function pointers, dispatched against <see cref="Handle"/>.</summary>
    public VkDeviceApi Api { get; }

    /// <summary>Queue family index used for both graphics and presentation.</summary>
    public uint GraphicsQueueFamily { get; }

    /// <summary>The single graphics/present queue.</summary>
    public VkQueue GraphicsQueue { get; }

    /// <summary>Command pool for command buffers submitted to <see cref="GraphicsQueue"/>, reset per-buffer.</summary>
    public VkCommandPool CommandPool { get; }

    /// <summary>Name and driver string of the selected GPU, for logging.</summary>
    public string DeviceDescription { get; }

    /// <summary>The sub-allocator every <see cref="VulkanBuffer"/> (and later, image) allocates from.</summary>
    public VmaAllocator VmaAllocator { get; }

    /// <summary>
    /// Where <c>Dispose()</c> on a GPU resource actually lands; see <see cref="VulkanDeleteQueue"/>
    /// for why a caller does not have to know whether the GPU is still using it.
    /// </summary>
    public VulkanDeleteQueue DeleteQueue { get; } = new();

    private VulkanDevice(VulkanInstance instance, VkPhysicalDevice physicalDevice, VkDevice device, VkDeviceApi api,
        uint graphicsQueueFamily, VkQueue graphicsQueue, VkCommandPool commandPool, string deviceDescription, VmaAllocator vmaAllocator)
    {
        Instance = instance;
        PhysicalDevice = physicalDevice;
        Handle = device;
        Api = api;
        GraphicsQueueFamily = graphicsQueueFamily;
        GraphicsQueue = graphicsQueue;
        CommandPool = commandPool;
        DeviceDescription = deviceDescription;
        VmaAllocator = vmaAllocator;
    }

    /// <summary>
    /// Selects a physical device that can present to <paramref name="surface"/> and supports dynamic
    /// rendering and synchronization2 (both core in Vulkan 1.3), and creates the logical device.
    /// </summary>
    public static VulkanDevice Create(VulkanInstance instance, VkSurfaceKHR surface, ILogger logger)
    {
        instance.Api.vkEnumeratePhysicalDevices(out var deviceCount).CheckResult();

        if (deviceCount == 0)
        {
            throw new InvalidOperationException("No Vulkan-capable physical devices were found.");
        }

        var physicalDevices = new VkPhysicalDevice[deviceCount];

        fixed (VkPhysicalDevice* pDevices = physicalDevices)
        {
            instance.Api.vkEnumeratePhysicalDevices(&deviceCount, pDevices).CheckResult();
        }

        VkPhysicalDevice? chosen = null;
        uint chosenGraphicsFamily = 0;
        VkPhysicalDeviceProperties chosenProperties = default;
        var chosenIsDiscrete = false;

        foreach (var candidate in physicalDevices)
        {
            if (!SupportsRequiredFeatures(instance, candidate))
            {
                continue;
            }

            if (!TryFindGraphicsPresentQueueFamily(instance, candidate, surface, out var queueFamily))
            {
                continue;
            }

            instance.Api.vkGetPhysicalDeviceProperties(candidate, out var properties);
            var isDiscrete = properties.deviceType == VkPhysicalDeviceType.DiscreteGpu;

            // Prefer the first discrete GPU; otherwise keep the first suitable device found.
            if (chosen is null || (isDiscrete && !chosenIsDiscrete))
            {
                chosen = candidate;
                chosenGraphicsFamily = queueFamily;
                chosenProperties = properties;
                chosenIsDiscrete = isDiscrete;
            }
        }

        if (chosen is not { } physicalDevice)
        {
            throw new InvalidOperationException(
                "No Vulkan device supports presenting to this window with dynamic rendering and synchronization2 (both core since Vulkan 1.3).");
        }

        var deviceName = GetFixedString(chosenProperties.deviceName);
        var description = $"{deviceName} (Vulkan {chosenProperties.apiVersion})";
        logger.LogInformation("Vulkan device: {Description}", description);

        var queuePriority = 1.0f;
        var queueCreateInfo = new VkDeviceQueueCreateInfo
        {
            queueFamilyIndex = chosenGraphicsFamily,
            queueCount = 1,
            pQueuePriorities = &queuePriority,
        };

        using var extensionArray = new VkStringArray(new List<string> { "VK_KHR_swapchain" });

        var features12 = new VkPhysicalDeviceVulkan12Features
        {
            timelineSemaphore = true,
        };

        var features13 = new VkPhysicalDeviceVulkan13Features
        {
            pNext = &features12,
            dynamicRendering = true,
            synchronization2 = true,
        };

        var deviceCreateInfo = new VkDeviceCreateInfo
        {
            pNext = &features13,
            queueCreateInfoCount = 1,
            pQueueCreateInfos = &queueCreateInfo,
            enabledExtensionCount = extensionArray.Length,
            ppEnabledExtensionNames = extensionArray,
        };

        instance.Api.vkCreateDevice(physicalDevice, &deviceCreateInfo, out var device).CheckResult();

        var deviceApi = new VkDeviceApi(instance.Api, device);

        deviceApi.vkGetDeviceQueue(chosenGraphicsFamily, 0, out var graphicsQueue);

        var poolCreateInfo = new VkCommandPoolCreateInfo
        {
            flags = VkCommandPoolCreateFlags.ResetCommandBuffer,
            queueFamilyIndex = chosenGraphicsFamily,
        };

        deviceApi.vkCreateCommandPool(&poolCreateInfo, out var commandPool).CheckResult();

        var allocatorCreateInfo = new VmaAllocatorCreateInfo
        {
            instance = instance.Handle,
            physicalDevice = physicalDevice,
            device = device,
            vulkanApiVersion = VkVersion.Version_1_4,
        };

        vmaCreateAllocator(in allocatorCreateInfo, out var vmaAllocator).CheckResult();

        return new VulkanDevice(instance, physicalDevice, device, deviceApi, chosenGraphicsFamily, graphicsQueue, commandPool, description, vmaAllocator);
    }

    /// <summary>
    /// Records into a temporary command buffer, submits it, and blocks until it completes. For
    /// infrequent host-to-device work (buffer/image uploads) where a dedicated transfer queue and
    /// async pacing are not worth the complexity yet; see <see cref="VulkanBuffer.CreateWithData"/>.
    /// </summary>
    public void RunOneShotCommands(Action<VkCommandBuffer> record)
    {
        var allocateInfo = new VkCommandBufferAllocateInfo
        {
            commandPool = CommandPool,
            level = VkCommandBufferLevel.Primary,
            commandBufferCount = 1,
        };

        VkCommandBuffer commandBuffer;
        Api.vkAllocateCommandBuffers(&allocateInfo, &commandBuffer).CheckResult();

        try
        {
            Api.vkBeginCommandBuffer(commandBuffer, VkCommandBufferUsageFlags.OneTimeSubmit).CheckResult();
            record(commandBuffer);
            Api.vkEndCommandBuffer(commandBuffer).CheckResult();

            var commandBufferInfo = new VkCommandBufferSubmitInfo { commandBuffer = commandBuffer };
            var submitInfo = new VkSubmitInfo2
            {
                commandBufferInfoCount = 1,
                pCommandBufferInfos = &commandBufferInfo,
            };

            Api.vkQueueSubmit2(GraphicsQueue, submitInfo, VkFence.Null).CheckResult();

            // Infrequent enough (uploads, not per-draw) that blocking the whole queue is fine; a
            // fenced wait scoped to just this command buffer is not worth it yet.
            Api.vkQueueWaitIdle(GraphicsQueue).CheckResult();
        }
        finally
        {
            Api.vkFreeCommandBuffers(CommandPool, commandBuffer);
        }
    }

    private static bool SupportsRequiredFeatures(VulkanInstance instance, VkPhysicalDevice physicalDevice)
    {
        var features13 = new VkPhysicalDeviceVulkan13Features();
        var features2 = new VkPhysicalDeviceFeatures2
        {
            pNext = &features13,
        };

        instance.Api.vkGetPhysicalDeviceFeatures2(physicalDevice, &features2);

        return features13.dynamicRendering && features13.synchronization2;
    }

    private static bool TryFindGraphicsPresentQueueFamily(VulkanInstance instance, VkPhysicalDevice physicalDevice, VkSurfaceKHR surface, out uint queueFamilyIndex)
    {
        instance.Api.vkGetPhysicalDeviceQueueFamilyProperties(physicalDevice, out var count);

        var families = new VkQueueFamilyProperties[count];

        fixed (VkQueueFamilyProperties* pFamilies = families)
        {
            instance.Api.vkGetPhysicalDeviceQueueFamilyProperties(physicalDevice, &count, pFamilies);
        }

        for (uint i = 0; i < count; i++)
        {
            if (!families[i].queueFlags.HasFlag(VkQueueFlags.Graphics))
            {
                continue;
            }

            instance.Api.vkGetPhysicalDeviceSurfaceSupportKHR(physicalDevice, i, surface, out var presentSupported);

            if (presentSupported)
            {
                queueFamilyIndex = i;
                return true;
            }
        }

        queueFamilyIndex = 0;
        return false;
    }

    private static string GetFixedString(byte* fixedBuffer) => new((sbyte*)fixedBuffer);

    /// <summary>Waits until every operation submitted to any queue on this device has completed.</summary>
    public void WaitIdle() => Api.vkDeviceWaitIdle().CheckResult();

    /// <summary>
    /// Waits for the device to go idle, flushes every resource the delete queue was still holding
    /// (nothing can be in flight once idle, so this is always safe here), then destroys the
    /// allocator, command pool, and device itself. Call after every other Vulkan object owned by
    /// this device has been destroyed or handed to the delete queue.
    /// </summary>
    public void Dispose()
    {
        WaitIdle();
        DeleteQueue.Flush();

        vmaDestroyAllocator(VmaAllocator);
        Api.vkDestroyCommandPool(CommandPool);
        Api.vkDestroyDevice();
    }
}
