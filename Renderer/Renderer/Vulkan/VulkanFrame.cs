using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// Records and submits one command buffer per frame: acquire, clear the image via dynamic
/// rendering (no <c>VkRenderPass</c>/<c>VkFramebuffer</c>, matching how the GL backend has no
/// default framebuffer object to create either), let the caller record whatever draws it wants in
/// between, then present. Split into <see cref="BeginFrame"/>/<see cref="EndFrame"/> rather than one
/// call precisely so a caller can record real, interleaved draw calls against
/// <see cref="CommandBuffer"/> - see <c>GraphicsContext.VulkanCommandBuffer</c>, the actual reason
/// this is two methods and not one. Fully CPU/GPU-serialized - one command buffer, one fence, no
/// frames-in-flight overlap - since proving correctness matters more than throughput at this stage.
/// </summary>
public sealed unsafe class VulkanFrame : IDisposable
{
    private readonly VulkanDevice device;
    private readonly VkFence inFlightFence;
    private readonly VkSemaphore imageAvailableSemaphore;

    private VulkanSwapchain? currentSwapchain;
    private int currentImageIndex;

    /// <summary>The command buffer the frame between <see cref="BeginFrame"/> and <see cref="EndFrame"/> is recorded into.</summary>
    public VkCommandBuffer CommandBuffer { get; }

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
        CommandBuffer = buffer;

        var fenceCreateInfo = new VkFenceCreateInfo { flags = VkFenceCreateFlags.Signaled };
        device.Api.vkCreateFence(&fenceCreateInfo, out inFlightFence).CheckResult();

        device.Api.vkCreateSemaphore(out imageAvailableSemaphore).CheckResult();
    }

    /// <summary>
    /// Acquires the next image, opens dynamic rendering over it (and <paramref name="depthImage"/>,
    /// if given one, cleared to the far plane), and sets viewport/scissor to the swapchain extent -
    /// every pipeline drawn through <see cref="VulkanPipelineCache"/> declares both as dynamic state,
    /// so this covers them without each draw repeating it. Runs <paramref name="computeDispatch"/>
    /// first, before rendering opens, since <c>vkCmdDispatch</c> is not allowed inside a
    /// <c>vkCmdBeginRendering</c>/<c>vkCmdEndRendering</c> pair, dynamic or not. Returns
    /// <see langword="false"/> if the swapchain came back out of date - nothing was recorded, and the
    /// caller should call <see cref="VulkanSwapchain.Recreate"/> and try again next frame rather than
    /// treat this as an error or call <see cref="EndFrame"/>.
    /// </summary>
    public bool BeginFrame(VulkanSwapchain swapchain, VkClearColorValue clearColor, VulkanImage? depthImage = null, Action<VkCommandBuffer>? computeDispatch = null)
    {
        device.Api.vkWaitForFences(inFlightFence, true, ulong.MaxValue).CheckResult();

        // Nothing can still be in flight now (see VulkanDeleteQueue), so anything queued for
        // deletion up to this instant - including by the caller, between last frame's EndFrame and
        // this call - is provably safe to actually destroy.
        device.DeleteQueue.Flush();

        if (!swapchain.AcquireNextImage(imageAvailableSemaphore, out var imageIndex))
        {
            return false;
        }

        currentSwapchain = swapchain;
        currentImageIndex = imageIndex;

        device.Api.vkResetFences(inFlightFence).CheckResult();
        device.Api.vkResetCommandBuffer(CommandBuffer, VkCommandBufferResetFlags.None).CheckResult();
        device.Api.vkBeginCommandBuffer(CommandBuffer, VkCommandBufferUsageFlags.OneTimeSubmit).CheckResult();

        computeDispatch?.Invoke(CommandBuffer);

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

        device.Api.vkCmdBeginRendering(CommandBuffer, &renderingInfo);

        var viewport = new VkViewport
        {
            width = swapchain.Extent.width,
            height = swapchain.Extent.height,
            minDepth = 0.0f,
            maxDepth = 1.0f,
        };
        var scissor = new VkRect2D { extent = swapchain.Extent };

        device.Api.vkCmdSetViewport(CommandBuffer, 0, viewport);
        device.Api.vkCmdSetScissor(CommandBuffer, 0, scissor);

        return true;
    }

    /// <summary>
    /// Closes rendering, submits, and presents the image <see cref="BeginFrame"/> acquired. Returns
    /// <see langword="false"/> if the swapchain came back out of date; the caller should call
    /// <see cref="VulkanSwapchain.Recreate"/> before the next <see cref="BeginFrame"/> rather than
    /// treat this as an error. Must be called exactly once for each successful <see cref="BeginFrame"/>.
    /// </summary>
    public bool EndFrame()
    {
        var swapchain = currentSwapchain ?? throw new InvalidOperationException("EndFrame was called without a matching successful BeginFrame.");
        var imageIndex = currentImageIndex;
        currentSwapchain = null;

        var image = swapchain.Image(imageIndex);

        device.Api.vkCmdEndRendering(CommandBuffer);

        TransitionImage(image, ref swapchain.ImageLayout(imageIndex), VkImageLayout.PresentSrcKHR,
            VkPipelineStageFlags2.ColorAttachmentOutput, VkAccessFlags2.ColorAttachmentWrite,
            VkPipelineStageFlags2.BottomOfPipe, VkAccessFlags2.None);

        device.Api.vkEndCommandBuffer(CommandBuffer).CheckResult();

        var renderFinishedSemaphore = swapchain.RenderFinishedSemaphore(imageIndex);
        var commandBuffer = CommandBuffer;

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
        => VulkanBarrier.TransitionImage(device.Api, CommandBuffer, image, VkImageAspectFlags.Color, ref currentLayout, targetLayout, srcStage, srcAccess, dstStage, dstAccess);

    /// <summary>Waits for the in-flight frame to finish, then destroys this frame's sync objects.</summary>
    public void Dispose()
    {
        device.Api.vkWaitForFences(inFlightFence, true, ulong.MaxValue).CheckResult();
        device.Api.vkDestroyFence(inFlightFence);
        device.Api.vkDestroySemaphore(imageAvailableSemaphore);
        device.Api.vkFreeCommandBuffers(device.CommandPool, CommandBuffer);
    }
}
