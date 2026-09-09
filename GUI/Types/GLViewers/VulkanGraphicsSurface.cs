using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Vulkan;
using Vortice.Vulkan;

namespace GUI.Types.GLViewers;

/// <summary>
/// Presents a <see cref="VulkanSwapchain"/>/<see cref="VulkanFrame"/> pair to the renderer as the
/// surface a <see cref="GraphicsContext"/> drives, the Vulkan counterpart of <see cref="GLFWSurface"/>:
/// opening the context acquires an image and opens dynamic rendering over it, closing it presents.
/// Unlike <see cref="GLFWSurface"/>, this also has to hand the context something to record into
/// (<see cref="CommandBuffer"/>), since Vulkan has no "current context" a draw call can rely on
/// implicitly the way <c>GL.DrawElements</c> does.
/// </summary>
sealed class VulkanGraphicsSurface(VulkanFrame frame, VulkanSwapchain swapchain) : IVulkanGraphicsSurface
{
    /// <summary>Color the swapchain image is cleared to at the start of each frame.</summary>
    public VkClearColorValue ClearColor { get; set; }

    /// <summary>Depth attachment to render against, or <see langword="null"/> for none. The caller recreates and reassigns this on resize.</summary>
    public VulkanImage? DepthImage { get; set; }

    /// <summary>Recorded before rendering opens each frame; see <see cref="VulkanFrame.BeginFrame"/>.</summary>
    public Action<VkCommandBuffer>? ComputeDispatch { get; set; }

    /// <summary>
    /// Whether the last <see cref="Begin"/> or <see cref="End"/> found the swapchain out of date.
    /// The caller should call <see cref="VulkanSwapchain.Recreate"/> and clear this before the next
    /// <see cref="Begin"/>.
    /// </summary>
    public bool NeedsSwapchainRecreate { get; private set; }

    /// <summary>Clears <see cref="NeedsSwapchainRecreate"/> once the caller has recreated the swapchain.</summary>
    public void AcknowledgeSwapchainRecreated() => NeedsSwapchainRecreate = false;

    /// <inheritdoc/>
    public VkCommandBuffer CommandBuffer => frame.CommandBuffer;

    private bool frameOpen;

    /// <inheritdoc/>
    public void Begin()
    {
        frameOpen = frame.BeginFrame(swapchain, ClearColor, DepthImage, ComputeDispatch);

        if (!frameOpen)
        {
            NeedsSwapchainRecreate = true;
        }
    }

    /// <inheritdoc/>
    public void End()
    {
        if (!frameOpen)
        {
            return;
        }

        frameOpen = false;

        if (!frame.EndFrame())
        {
            NeedsSwapchainRecreate = true;
        }
    }
}
