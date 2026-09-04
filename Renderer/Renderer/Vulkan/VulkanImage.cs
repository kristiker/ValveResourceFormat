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

    /// <summary>Number of mip levels the image was created with.</summary>
    public int NumMipLevels { get; }

    /// <summary>Format the image was created with.</summary>
    public VkFormat Format { get; }

    private VulkanImage(VulkanDevice device, VkImage handle, VmaAllocation allocation, VkImageView view, VkExtent2D extent, int numMipLevels, VkFormat format)
    {
        this.device = device;
        Handle = handle;
        this.allocation = allocation;
        View = view;
        Extent = extent;
        NumMipLevels = numMipLevels;
        Format = format;
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

        return new VulkanImage(device, image, allocation, view, extent, numMipLevels: 1, format);
    }

    /// <summary>
    /// Creates a single-mip, single-layer <see cref="VkFormat.D32Sfloat"/> depth attachment, sized to
    /// match a render target, transitioned to <see cref="VkImageLayout.DepthAttachmentOptimal"/> and
    /// never touched again outside a render pass - nothing samples it, so unlike
    /// <see cref="CreateRgba8"/> it needs no descriptor-facing view work or upload.
    /// </summary>
    public static VulkanImage CreateDepth(VulkanDevice device, string name, uint width, uint height)
    {
        const VkFormat format = VkFormat.D32Sfloat;
        var extent = new VkExtent2D { width = width, height = height };

        var imageCreateInfo = new VkImageCreateInfo
        {
            imageType = VkImageType.Image2D,
            format = format,
            extent = new VkExtent3D { width = width, height = height, depth = 1 },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            usage = VkImageUsageFlags.DepthStencilAttachment,
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
                aspectMask = VkImageAspectFlags.Depth,
                baseMipLevel = 0,
                levelCount = 1,
                baseArrayLayer = 0,
                layerCount = 1,
            },
        };

        device.Api.vkCreateImageView(&viewCreateInfo, out var view).CheckResult();

        var layout = VkImageLayout.Undefined;

        device.RunOneShotCommands(commandBuffer =>
        {
            VulkanBarrier.TransitionImage(device.Api, commandBuffer, image, VkImageAspectFlags.Depth, ref layout, VkImageLayout.DepthAttachmentOptimal,
                VkPipelineStageFlags2.TopOfPipe, VkAccessFlags2.None,
                VkPipelineStageFlags2.EarlyFragmentTests | VkPipelineStageFlags2.LateFragmentTests, VkAccessFlags2.DepthStencilAttachmentWrite);
        });

        return new VulkanImage(device, image, allocation, view, extent, numMipLevels: 1, format);
    }

    /// <summary>
    /// Creates a single-mip, single-layer, device-local image usable as both a compute shader's
    /// <c>imageStore</c> target and a sampled bindless texture - written by compute, read by a later
    /// fragment shader, the two connected only by whatever barrier the caller records between them.
    /// Starts in <see cref="VkImageLayout.Undefined"/> and is never touched here again; unlike
    /// <see cref="CreateRgba8"/> and <see cref="CreateDepth"/>, its layout changes every frame (write
    /// layout before dispatch, read layout before sampling), which only the caller can time correctly.
    /// </summary>
    public static VulkanImage CreateStorage(VulkanDevice device, string name, uint width, uint height, VkFormat format)
    {
        var extent = new VkExtent2D { width = width, height = height };

        var imageCreateInfo = new VkImageCreateInfo
        {
            imageType = VkImageType.Image2D,
            format = format,
            extent = new VkExtent3D { width = width, height = height, depth = 1 },
            mipLevels = 1,
            arrayLayers = 1,
            samples = VkSampleCountFlags.Count1,
            usage = VkImageUsageFlags.Storage | VkImageUsageFlags.Sampled,
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

        return new VulkanImage(device, image, allocation, view, extent, numMipLevels: 1, format);
    }

    /// <summary>
    /// Creates a device-local image with storage committed but no data uploaded - the Vulkan
    /// counterpart of <c>GL.CreateTextures</c> followed by <c>GL.TextureStorage2D</c>. Every mip is
    /// left in <see cref="VkImageLayout.ShaderReadOnlyOptimal"/> (with whatever driver-undefined bytes
    /// the allocation happened to contain) rather than tracked per mip, so
    /// <see cref="SetData(int, uint, uint, ReadOnlySpan{byte})"/> can treat every upload the same way:
    /// transition that one mip out and back, unconditionally, rather than needing to know whether it
    /// has been written before.
    /// </summary>
    public static VulkanImage CreateWithStorage2D(VulkanDevice device, string name, uint width, uint height, VkFormat format, int mipLevels)
    {
        var extent = new VkExtent2D { width = width, height = height };

        var imageCreateInfo = new VkImageCreateInfo
        {
            imageType = VkImageType.Image2D,
            format = format,
            extent = new VkExtent3D { width = width, height = height, depth = 1 },
            mipLevels = (uint)mipLevels,
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
                levelCount = (uint)mipLevels,
                baseArrayLayer = 0,
                layerCount = 1,
            },
        };

        device.Api.vkCreateImageView(&viewCreateInfo, out var view).CheckResult();

        var layout = VkImageLayout.Undefined;

        device.RunOneShotCommands(commandBuffer =>
        {
            var range = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Color,
                baseMipLevel = 0,
                levelCount = (uint)mipLevels,
                baseArrayLayer = 0,
                layerCount = 1,
            };

            VulkanBarrier.TransitionImage(device.Api, commandBuffer, image, range, ref layout, VkImageLayout.ShaderReadOnlyOptimal,
                VkPipelineStageFlags2.TopOfPipe, VkAccessFlags2.None, VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderRead);
        });

        return new VulkanImage(device, image, allocation, view, extent, mipLevels, format);
    }

    /// <summary>
    /// Uploads one mip level's pixel data (tightly packed, row-major), the Vulkan counterpart of
    /// <c>GL.TextureSubImage2D</c>. The mip is assumed to already be
    /// <see cref="VkImageLayout.ShaderReadOnlyOptimal"/> beforehand (true immediately after
    /// <see cref="CreateWithStorage2D"/>, and true again after this returns) and is left there after.
    /// </summary>
    public void SetData(int mipLevel, uint mipWidth, uint mipHeight, ReadOnlySpan<byte> pixels)
    {
        var staging = VulkanBuffer.Create(device, $"mip {mipLevel} upload staging", (ulong)pixels.Length, VkBufferUsageFlags.TransferSrc, BufferUsage.Dynamic);

        try
        {
            staging.SetData(pixels);

            var range = new VkImageSubresourceRange
            {
                aspectMask = VkImageAspectFlags.Color,
                baseMipLevel = (uint)mipLevel,
                levelCount = 1,
                baseArrayLayer = 0,
                layerCount = 1,
            };

            var layout = VkImageLayout.ShaderReadOnlyOptimal;
            var image = Handle;

            device.RunOneShotCommands(commandBuffer =>
            {
                VulkanBarrier.TransitionImage(device.Api, commandBuffer, image, range, ref layout, VkImageLayout.TransferDstOptimal,
                    VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderRead, VkPipelineStageFlags2.Transfer, VkAccessFlags2.TransferWrite);

                var region = new VkBufferImageCopy
                {
                    imageSubresource = new VkImageSubresourceLayers(VkImageAspectFlags.Color, (uint)mipLevel, 0, 1),
                    imageExtent = new VkExtent3D { width = mipWidth, height = mipHeight, depth = 1 },
                };

                device.Api.vkCmdCopyBufferToImage(commandBuffer, staging.Handle, image, VkImageLayout.TransferDstOptimal, 1, &region);

                VulkanBarrier.TransitionImage(device.Api, commandBuffer, image, range, ref layout, VkImageLayout.ShaderReadOnlyOptimal,
                    VkPipelineStageFlags2.Transfer, VkAccessFlags2.TransferWrite, VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderRead);
            });
        }
        finally
        {
            staging.Dispose();
        }
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
