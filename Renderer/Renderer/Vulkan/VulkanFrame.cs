using System.Numerics;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// Records and submits one command buffer per frame: acquire, clear the image via dynamic
/// rendering (no <c>VkRenderPass</c>/<c>VkFramebuffer</c>, matching how the GL backend has no
/// default framebuffer object to create either), optionally draw <see cref="VulkanTrianglePipeline"/>,
/// then present. Fully CPU/GPU-serialized - one command buffer, one fence, no frames-in-flight
/// overlap - since proving correctness matters more than throughput at this stage; that comes with
/// the delete-queue and multi-frame-in-flight milestone.
/// </summary>
public sealed unsafe class VulkanFrame : IDisposable
{
    private readonly VulkanDevice device;
    private readonly VkCommandBuffer commandBuffer;
    private readonly VkFence inFlightFence;
    private readonly VkSemaphore imageAvailableSemaphore;

    /// <summary>Allocates this frame's command buffer and sync objects against <paramref name="device"/>.</summary>
    public VulkanFrame(VulkanDevice device)
    {
        this.device = device;

        var allocateInfo = new VkCommandBufferAllocateInfo
        {
            commandPool = device.CommandPool,
            level = VkCommandBufferLevel.Primary,
            commandBufferCount = 1,
        };

        VkCommandBuffer buffer;
        device.Api.vkAllocateCommandBuffers(&allocateInfo, &buffer).CheckResult();
        commandBuffer = buffer;

        var fenceCreateInfo = new VkFenceCreateInfo { flags = VkFenceCreateFlags.Signaled };
        device.Api.vkCreateFence(&fenceCreateInfo, out inFlightFence).CheckResult();

        device.Api.vkCreateSemaphore(out imageAvailableSemaphore).CheckResult();
    }

    /// <summary>
    /// Acquires the next image, runs <paramref name="computeDispatch"/> if given one (before
    /// rendering starts - <c>vkCmdDispatch</c> is not allowed inside a
    /// <c>vkCmdBeginRendering</c>/<c>vkCmdEndRendering</c> pair, dynamic or not), clears the image
    /// (and draws <paramref name="triangle"/> over the clear if given one, then runs
    /// <paramref name="extraDraws"/> if given one - viewport and scissor are already set to the
    /// swapchain extent by the time it runs, since every pipeline drawn through
    /// <see cref="VulkanPipelineCache"/> declares both as dynamic state), and presents it. Returns
    /// <see langword="false"/> if the swapchain came back out of date; the caller should call
    /// <see cref="VulkanSwapchain.Recreate"/> and try again next frame rather than treat this as an
    /// error. Depth-tests against <paramref name="depthImage"/> when one is given, clearing it to the
    /// far plane (1.0) first.
    /// </summary>
    public bool RenderAndPresent(VulkanSwapchain swapchain, VkClearColorValue clearColor, VulkanTrianglePipeline? triangle = null,
        VulkanBindlessTextures? bindlessTextures = null, VulkanBuffer? vertexBuffer = null, Matrix4x4 mvp = default, uint textureIndex = 0,
        VulkanImage? depthImage = null, Action<VkCommandBuffer>? extraDraws = null, Action<VkCommandBuffer>? computeDispatch = null)
    {
        device.Api.vkWaitForFences(inFlightFence, true, ulong.MaxValue).CheckResult();

        // Nothing can still be in flight now (see VulkanDeleteQueue), so anything queued for
        // deletion up to this instant - including by the caller, between last frame's return and
        // this call - is provably safe to actually destroy.
        device.DeleteQueue.Flush();

        if (!swapchain.AcquireNextImage(imageAvailableSemaphore, out var imageIndex))
        {
            return false;
        }

        device.Api.vkResetFences(inFlightFence).CheckResult();
        device.Api.vkResetCommandBuffer(commandBuffer, VkCommandBufferResetFlags.None).CheckResult();
        device.Api.vkBeginCommandBuffer(commandBuffer, VkCommandBufferUsageFlags.OneTimeSubmit).CheckResult();

        computeDispatch?.Invoke(commandBuffer);

        var image = swapchain.Image(imageIndex);

        TransitionImage(image, ref swapchain.ImageLayout(imageIndex), VkImageLayout.ColorAttachmentOptimal,
            VkPipelineStageFlags2.TopOfPipe, VkAccessFlags2.None,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite);

        var colorAttachment = new VkRenderingAttachmentInfo
        {
            imageView = swapchain.ImageView(imageIndex),
            imageLayout = VkImageLayout.ColorAttachmentOptimal,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.Store,
            clearValue = new VkClearValue { color = clearColor },
        };

        var depthAttachment = new VkRenderingAttachmentInfo
        {
            imageView = depthImage?.View ?? default,
            imageLayout = VkImageLayout.DepthAttachmentOptimal,
            loadOp = VkAttachmentLoadOp.Clear,
            storeOp = VkAttachmentStoreOp.DontCare,
            clearValue = new VkClearValue { depthStencil = new VkClearDepthStencilValue(1.0f, 0) },
        };

        var renderingInfo = new VkRenderingInfo
        {
            renderArea = new VkRect2D { extent = swapchain.Extent },
            layerCount = 1,
            colorAttachmentCount = 1,
            pColorAttachments = &colorAttachment,
            pDepthAttachment = depthImage != null ? &depthAttachment : null,
        };

        device.Api.vkCmdBeginRendering(commandBuffer, &renderingInfo);

        var viewport = new VkViewport
        {
            width = swapchain.Extent.width,
            height = swapchain.Extent.height,
            minDepth = 0.0f,
            maxDepth = 1.0f,
        };
        var scissor = new VkRect2D { extent = swapchain.Extent };

        device.Api.vkCmdSetViewport(commandBuffer, 0, viewport);
        device.Api.vkCmdSetScissor(commandBuffer, 0, scissor);

        if (triangle != null)
        {
            device.Api.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Graphics, triangle.Handle);

            if (bindlessTextures != null)
            {
                var set = bindlessTextures.Set;
                device.Api.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Graphics, triangle.Layout, 0, 1, &set, 0, null);
            }

            var pushConstants = new VulkanTrianglePipeline.PushConstants(mvp, textureIndex);
            device.Api.vkCmdPushConstants(commandBuffer, triangle.Layout, VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment, 0, (uint)sizeof(VulkanTrianglePipeline.PushConstants), &pushConstants);

            if (vertexBuffer != null)
            {
                device.Api.vkCmdBindVertexBuffer(commandBuffer, 0, vertexBuffer.Handle);
                device.Api.vkCmdDraw(commandBuffer, 3, 1, 0, 0);
            }
        }

        extraDraws?.Invoke(commandBuffer);

        device.Api.vkCmdEndRendering(commandBuffer);

        TransitionImage(image, ref swapchain.ImageLayout(imageIndex), VkImageLayout.PresentSrcKHR,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite,
            VkPipelineStageFlags2.BottomOfPipe, VkAccessFlags2.None);

        device.Api.vkEndCommandBuffer(commandBuffer).CheckResult();

        var renderFinishedSemaphore = swapchain.RenderFinishedSemaphore(imageIndex);

        var commandBufferInfo = new VkCommandBufferSubmitInfo { commandBuffer = commandBuffer };
        var waitSemaphoreInfo = new VkSemaphoreSubmitInfo { semaphore = imageAvailableSemaphore, stageMask = VkPipelineStageFlags2.ColorAttachmentOutput };
        var signalSemaphoreInfo = new VkSemaphoreSubmitInfo { semaphore = renderFinishedSemaphore, stageMask = VkPipelineStageFlags2.ColorAttachmentOutput };

        var submitInfo = new VkSubmitInfo2
        {
            waitSemaphoreInfoCount = 1,
            pWaitSemaphoreInfos = &waitSemaphoreInfo,
            commandBufferInfoCount = 1,
            pCommandBufferInfos = &commandBufferInfo,
            signalSemaphoreInfoCount = 1,
            pSignalSemaphoreInfos = &signalSemaphoreInfo,
        };

        device.Api.vkQueueSubmit2(device.GraphicsQueue, submitInfo, inFlightFence).CheckResult();

        return swapchain.Present(imageIndex);
    }

    private void TransitionImage(VkImage image, ref VkImageLayout currentLayout, VkImageLayout targetLayout,
        VkPipelineStageFlags2 srcStage, VkAccessFlags2 srcAccess, VkPipelineStageFlags2 dstStage, VkAccessFlags2 dstAccess)
        => VulkanBarrier.TransitionImage(device.Api, commandBuffer, image, VkImageAspectFlags.Color, ref currentLayout, targetLayout, srcStage, srcAccess, dstStage, dstAccess);

    /// <summary>Waits for the in-flight frame to finish, then destroys this frame's sync objects.</summary>
    public void Dispose()
    {
        device.Api.vkWaitForFences(inFlightFence, true, ulong.MaxValue).CheckResult();
        device.Api.vkDestroyFence(inFlightFence);
        device.Api.vkDestroySemaphore(imageAvailableSemaphore);
        device.Api.vkFreeCommandBuffers(device.CommandPool, commandBuffer);
    }
}
