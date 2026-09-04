using Vortice.Vulkan;
using static Vortice.Vulkan.Vma;
using static Vortice.Vulkan.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// A device-local 2D color texture: <see cref="VkImage"/>, its <see cref="VkImageView"/>, and the
/// VMA allocation backing it. Uploaded once through a staging buffer, the same shape
/// <see cref="VulkanBuffer.CreateWithData"/> uses for <see cref="BufferUsage.Static"/> data - mip
/// generation, compressed formats, and render targets are for when a real material needs them.
/// Disposing queues the actual destruction; see <see cref="VulkanDeleteQueue"/>.
/// </summary>
public sealed unsafe class VulkanImage : IDisposable
{
    private readonly VulkanDevice device;
    private readonly VmaAllocation allocation;

    /// <summary>The image handle.</summary>
    public VkImage Handle { get; }

    /// <summary>A view over the whole image, for sampling.</summary>
    public VkImageView View { get; }

    /// <summary>Pixel dimensions.</summary>
    public VkExtent2D Extent { get; }

    private VulkanImage(VulkanDevice device, VkImage handle, VmaAllocation allocation, VkImageView view, VkExtent2D extent)
    {
        this.device = device;
        Handle = handle;
        this.allocation = allocation;
        View = view;
        Extent = extent;
    }

    /// <summary>
    /// Creates a single-mip, single-layer <see cref="VkFormat.R8G8B8A8Unorm"/> texture already
    /// holding <paramref name="pixels"/> (tightly packed, row-major, 4 bytes per pixel), transitioned
    /// to <see cref="VkImageLayout.ShaderReadOnlyOptimal"/> and ready to sample.
    /// </summary>
    public static VulkanImage CreateRgba8(VulkanDevice device, string name, uint width, uint height, ReadOnlySpan<byte> pixels)
    {
        const VkFormat format = VkFormat.R8G8B8A8Unorm;
        var extent = new VkExtent2D { width = width, height = height };

        var imageCreateInfo = new VkImageCreateInfo
        {
            imageType = VkImageType.Image2D,
            format = format,
            extent = new VkExtent3D { width = width, height = height, depth = 1 },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            usage = VkImageUsageFlags.Sampled | VkImageUsageFlags.TransferDst,
        };

        var allocationCreateInfo = new VmaAllocationCreateInfo { usage = VmaMemoryUsage.AutoPreferDevice };

        vmaCreateImage(device.VmaAllocator, in imageCreateInfo, in allocationCreateInfo, out var image, out var allocation).CheckResult();

        var viewCreateInfo = new VkImageViewCreateInfo
        {
            image = image,
            viewType = VkImageViewType.Image2D,
            format = format,
            subresourceRange = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Color,
                baseMipLevel = 0,
                levelCount = 1,
                baseArrayLayer = 0,
                layerCount = 1,
            },
        };

        device.Api.vkCreateImageView(&viewCreateInfo, out var view).CheckResult();

        var staging = VulkanBuffer.Create(device, $"{name} (staging)", (ulong)pixels.Length, VkBufferUsageFlags.TransferSrc, BufferUsage.Dynamic);

        try
        {
            staging.SetData(pixels);

            var layout = VkImageLayout.Undefined;

            device.RunOneShotCommands(commandBuffer =>
            {
                VulkanBarrier.TransitionImage(device.Api, commandBuffer, image, VkImageAspectFlags.Color, ref layout, VkImageLayout.TransferDstOptimal,
                    VkPipelineStageFlags2.TopOfPipe, VkAccessFlags2.None, VkPipelineStageFlags2.Transfer, VkAccessFlags2.TransferWrite);

                var region = new VkBufferImageCopy
                {
                    imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, 0, 0, 1),
                    imageExtent = new VkExtent3D { width = width, height = height, depth = 1 },
                };

                device.Api.vkCmdCopyBufferToImage(commandBuffer, staging.Handle, image, VkImageLayout.TransferDstOptimal, 1, &region);

                VulkanBarrier.TransitionImage(device.Api, commandBuffer, image, VkImageAspectFlags.Color, ref layout, VkImageLayout.ShaderReadOnlyOptimal,
                    VkPipelineStageFlags2.Transfer, VkAccessFlags2.TransferWrite, VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderRead);
            });
        }
        finally
        {
            staging.Dispose();
        }

        return new VulkanImage(device, image, allocation, view, extent);
    }

    /// <summary>Queues this image's view and storage for destruction; see <see cref="VulkanDeleteQueue"/>.</summary>
    public void Dispose()
    {
        var image = Handle;
        var view = View;
        var imageAllocation = allocation;
        var api = device.Api;
        var vmaAllocator = device.VmaAllocator;

        device.DeleteQueue.Queue(() =>
        {
            api.vkDestroyImageView(view);
            vmaDestroyImage(vmaAllocator, image, imageAllocation);
        });
    }
}
