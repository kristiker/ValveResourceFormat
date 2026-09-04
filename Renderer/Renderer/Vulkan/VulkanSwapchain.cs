using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// The Vulkan side of the implicit main framebuffer: a chain of presentable images with no
/// default one, unlike OpenGL's framebuffer 0. Owns the swapchain, its image views, and the
/// per-image "safe to write" semaphore a frame waits on before reusing that image's command buffer.
/// </summary>
public sealed unsafe class VulkanSwapchain : IDisposable
{
    private readonly VulkanDevice device;
    private readonly VkSurfaceKHR surface;

    private VkSwapchainKHR handle;
    private VkImage[] images = [];
    private VkImageView[] imageViews = [];
    private VkImageLayout[] imageLayouts = [];
    private VkSemaphore[] renderFinishedSemaphores = [];

    /// <summary>Color format every image and image view was created with.</summary>
    public VkFormat Format { get; private set; }

    /// <summary>Pixel size of every image in the chain.</summary>
    public VkExtent2D Extent { get; private set; }

    /// <summary>Number of images in the chain.</summary>
    public int ImageCount => images.Length;

    /// <summary>Layout the given image was last transitioned to; update it after recording a transition.</summary>
    public ref VkImageLayout ImageLayout(int index) => ref imageLayouts[index];

    /// <summary>The swapchain image at this index.</summary>
    public VkImage Image(int index) => images[index];

    /// <summary>The color attachment view over the swapchain image at this index.</summary>
    public VkImageView ImageView(int index) => imageViews[index];

    /// <summary>The semaphore signaled once presentation of this index's image no longer needs its contents.</summary>
    public VkSemaphore RenderFinishedSemaphore(int index) => renderFinishedSemaphores[index];

    /// <summary>Creates the swapchain at the given pixel size.</summary>
    public VulkanSwapchain(VulkanDevice device, VkSurfaceKHR surface, uint width, uint height)
    {
        this.device = device;
        this.surface = surface;

        Create(width, height);
    }

    private void Create(uint width, uint height)
    {
        var instanceApi = device.Instance.Api;

        instanceApi.vkGetPhysicalDeviceSurfaceCapabilitiesKHR(device.PhysicalDevice, surface, out var capabilities).CheckResult();

        instanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(device.PhysicalDevice, surface, out var formatCount).CheckResult();
        var formats = new VkSurfaceFormatKHR[formatCount];

        fixed (VkSurfaceFormatKHR* pFormats = formats)
        {
            instanceApi.vkGetPhysicalDeviceSurfaceFormatsKHR(device.PhysicalDevice, surface, &formatCount, pFormats).CheckResult();
        }

        var surfaceFormat = ChooseSurfaceFormat(formats);

        instanceApi.vkGetPhysicalDeviceSurfacePresentModesKHR(device.PhysicalDevice, surface, out var presentModeCount).CheckResult();
        var presentModes = new VkPresentModeKHR[presentModeCount];

        fixed (VkPresentModeKHR* pPresentModes = presentModes)
        {
            instanceApi.vkGetPhysicalDeviceSurfacePresentModesKHR(device.PhysicalDevice, surface, &presentModeCount, pPresentModes).CheckResult();
        }

        var presentMode = ChoosePresentMode(presentModes);
        var extent = ChooseExtent(capabilities, width, height);

        var imageCount = capabilities.minImageCount + 1;

        if (capabilities.maxImageCount != 0 && imageCount > capabilities.maxImageCount)
        {
            imageCount = capabilities.maxImageCount;
        }

        var createInfo = new VkSwapchainCreateInfoKHR
        {
            surface = surface,
            minImageCount = imageCount,
            imageFormat = surfaceFormat.format,
            imageColorSpace = surfaceFormat.colorSpace,
            imageExtent = extent,
            imageArrayLayers = 1,
            imageUsage = VkImageUsageFlags.ColorAttachment,
            imageSharingMode = VkSharingMode.Exclusive,
            preTransform = capabilities.currentTransform,
            compositeAlpha = VkCompositeAlphaFlagsKHR.Opaque,
            presentMode = presentMode,
            clipped = true,
        };

        device.Api.vkCreateSwapchainKHR(&createInfo, out handle).CheckResult();

        Format = surfaceFormat.format;
        Extent = extent;

        device.Api.vkGetSwapchainImagesKHR(handle, out var actualImageCount).CheckResult();
        images = new VkImage[actualImageCount];

        fixed (VkImage* pImages = images)
        {
            device.Api.vkGetSwapchainImagesKHR(handle, &actualImageCount, pImages).CheckResult();
        }

        // Freshly acquired from the platform; OpenGL never exposes this state because it has no
        // separate acquire step, but Vulkan images start undefined until the first transition.
        imageLayouts = new VkImageLayout[images.Length];

        imageViews = new VkImageView[images.Length];
        renderFinishedSemaphores = new VkSemaphore[images.Length];

        for (var i = 0; i < images.Length; i++)
        {
            var viewCreateInfo = new VkImageViewCreateInfo
            {
                image = images[i],
                viewType = VkImageViewType.Image2D,
                format = Format,
                subresourceRange = new VkImageSubresourceRange
                {
                    aspectMask = VkImageAspectFlags.Color,
                    baseMipLevel = 0,
                    levelCount = 1,
                    baseArrayLayer = 0,
                    layerCount = 1,
                },
            };

            device.Api.vkCreateImageView(&viewCreateInfo, out imageViews[i]).CheckResult();
            device.Api.vkCreateSemaphore(out renderFinishedSemaphores[i]).CheckResult();
        }
    }

    /// <summary>
    /// Acquires the next presentable image, signaling <paramref name="imageAvailableSemaphore"/> once
    /// it is safe to write to. Returns <see langword="false"/> if the swapchain is out of date and
    /// must be recreated via <see cref="Recreate"/> before rendering.
    /// </summary>
    public bool AcquireNextImage(VkSemaphore imageAvailableSemaphore, out int imageIndex)
    {
        var result = device.Api.vkAcquireNextImageKHR(handle, ulong.MaxValue, imageAvailableSemaphore, VkFence.Null, out var index);
        imageIndex = (int)index;

        if (result is VkResult.ErrorOutOfDateKHR)
        {
            return false;
        }

        if (result is not (VkResult.Success or VkResult.SuboptimalKHR))
        {
            result.CheckResult();
        }

        return true;
    }

    /// <summary>
    /// Presents the given image, waiting on its render-finished semaphore. Returns <see langword="false"/>
    /// if the swapchain is out of date and must be recreated before the next frame.
    /// </summary>
    public bool Present(int imageIndex)
    {
        var swapchainHandle = handle;
        var index = (uint)imageIndex;
        var semaphore = renderFinishedSemaphores[imageIndex];

        var presentInfo = new VkPresentInfoKHR
        {
            waitSemaphoreCount = 1,
            pWaitSemaphores = &semaphore,
            swapchainCount = 1,
            pSwapchains = &swapchainHandle,
            pImageIndices = &index,
        };

        var result = device.Api.vkQueuePresentKHR(device.GraphicsQueue, &presentInfo);

        if (result is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR)
        {
            return false;
        }

        result.CheckResult();
        return true;
    }

    /// <summary>Destroys and recreates the swapchain at a new size, e.g. after the window resized.</summary>
    public void Recreate(uint width, uint height)
    {
        device.WaitIdle();
        DestroySwapchainObjects();
        Create(width, height);
    }

    private static VkSurfaceFormatKHR ChooseSurfaceFormat(VkSurfaceFormatKHR[] formats)
    {
        foreach (var format in formats)
        {
            if (format.format == VkFormat.B8G8R8A8Unorm && format.colorSpace == VkColorSpaceKHR.SrgbNonLinear)
            {
                return format;
            }
        }

        return formats[0];
    }

    private static VkPresentModeKHR ChoosePresentMode(VkPresentModeKHR[] modes)
    {
        foreach (var mode in modes)
        {
            if (mode == VkPresentModeKHR.Mailbox)
            {
                return mode;
            }
        }

        return VkPresentModeKHR.Fifo;
    }

    private static VkExtent2D ChooseExtent(VkSurfaceCapabilitiesKHR capabilities, uint width, uint height)
    {
        if (capabilities.currentExtent.width != uint.MaxValue)
        {
            return capabilities.currentExtent;
        }

        return new VkExtent2D
        {
            width = Math.Clamp(width, capabilities.minImageExtent.width, capabilities.maxImageExtent.width),
            height = Math.Clamp(height, capabilities.minImageExtent.height, capabilities.maxImageExtent.height),
        };
    }

    private void DestroySwapchainObjects()
    {
        foreach (var view in imageViews)
        {
            device.Api.vkDestroyImageView(view);
        }

        foreach (var semaphore in renderFinishedSemaphores)
        {
            device.Api.vkDestroySemaphore(semaphore);
        }

        if (handle.IsNotNull)
        {
            device.Api.vkDestroySwapchainKHR(handle);
        }

        imageViews = [];
        renderFinishedSemaphores = [];
        images = [];
        imageLayouts = [];
        handle = default;
    }

    /// <inheritdoc/>
    public void Dispose() => DestroySwapchainObjects();
}
