using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>One <c>vkCmdPipelineBarrier2</c> image layout transition, shared by swapchain images and <see cref="VulkanImage"/>.</summary>
public static unsafe class VulkanBarrier
{
    /// <summary>Records a full-resource layout transition and updates <paramref name="currentLayout"/> to match.</summary>
    public static void TransitionImage(VkDeviceApi api, VkCommandBuffer commandBuffer, VkImage image, VkImageAspectFlags aspect,
        ref VkImageLayout currentLayout, VkImageLayout targetLayout,
        VkPipelineStageFlags2 srcStage, VkAccessFlags2 srcAccess, VkPipelineStageFlags2 dstStage, VkAccessFlags2 dstAccess)
    {
        var range = new VkImageSubresourceRange
        {
            aspectMask = aspect,
            baseMipLevel = 0,
            levelCount = VK_REMAINING_MIP_LEVELS,
            baseArrayLayer = 0,
            layerCount = VK_REMAINING_ARRAY_LAYERS,
        };

        TransitionImage(api, commandBuffer, image, range, ref currentLayout, targetLayout, srcStage, srcAccess, dstStage, dstAccess);
    }

    /// <summary>
    /// Records a layout transition over exactly <paramref name="range"/> and updates
    /// <paramref name="currentLayout"/> to match - for a texture whose mips are uploaded (and so
    /// transitioned) one at a time rather than as a whole resource; see
    /// <see cref="VulkanImage.SetData"/>.
    /// </summary>
    public static void TransitionImage(VkDeviceApi api, VkCommandBuffer commandBuffer, VkImage image, in VkImageSubresourceRange range,
        ref VkImageLayout currentLayout, VkImageLayout targetLayout,
        VkPipelineStageFlags2 srcStage, VkAccessFlags2 srcAccess, VkPipelineStageFlags2 dstStage, VkAccessFlags2 dstAccess)
    {
        var barrier = new VkImageMemoryBarrier2
        {
            srcStageMask = srcStage,
            srcAccessMask = srcAccess,
            dstStageMask = dstStage,
            dstAccessMask = dstAccess,
            oldLayout = currentLayout,
            newLayout = targetLayout,
            image = image,
            subresourceRange = range,
        };

        var dependencyInfo = new VkDependencyInfo
        {
            imageMemoryBarrierCount = 1,
            pImageMemoryBarriers = &barrier,
        };

        api.vkCmdPipelineBarrier2(commandBuffer, &dependencyInfo);
        currentLayout = targetLayout;
    }
}
