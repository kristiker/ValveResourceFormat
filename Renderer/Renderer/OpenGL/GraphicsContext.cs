using Vortice.Vulkan;

namespace ValveResourceFormat.Renderer;

/// <summary>
/// The window side of a graphics context: the surface whose commands the context drives.
/// Implemented by whoever owns the window.
/// </summary>
// End() matches the graphics vocabulary; the reserved-word clash only concerns languages this is not consumed from.
#pragma warning disable CA1716 // Identifiers should not match keywords
public interface IGraphicsSurface
{
    /// <summary>Opens the surface's command stream on the calling thread.</summary>
    void Begin();

    /// <summary>Closes the surface's command stream on the calling thread.</summary>
    void End();
}
#pragma warning restore CA1716

/// <summary>
/// Extends <see cref="IGraphicsSurface"/> for a <see cref="GraphicsBackend.Vulkan"/> surface, so
/// <see cref="GraphicsContext.VulkanCommandBuffer"/> has something to read: unlike OpenGL, where
/// "the context that is current on this thread" is enough to issue a draw call against, Vulkan needs
/// an explicit <see cref="VkCommandBuffer"/> passed to every recording call.
/// <see cref="IGraphicsSurface.Begin"/> opens it (acquiring a swapchain image, beginning dynamic
/// rendering over it); <see cref="IGraphicsSurface.End"/> closes, submits, and presents it.
/// </summary>
public interface IVulkanGraphicsSurface : IGraphicsSurface
{
    /// <summary>The command buffer the frame between <see cref="IGraphicsSurface.Begin"/> and <see cref="IGraphicsSurface.End"/> is recorded into.</summary>
    VkCommandBuffer CommandBuffer { get; }
}

/// <summary>
/// One command stream recorded against a <see cref="GraphicsDevice"/>'s objects.
///
/// A device can own several contexts; each is current on at most one thread at a time, so
/// <see cref="Current"/> is per thread and names the context the calling thread is recording into.
/// </summary>
public sealed class GraphicsContext
{
    [ThreadStatic]
    private static GraphicsContext? current;

    // Null when the caller owns the surface's currency, or the API has no current-context model.
    private readonly IGraphicsSurface? surface;

    /// <summary>The device whose objects this context records against.</summary>
    internal GraphicsDevice Device { get; }

    private readonly RenderStateTracker renderState = new();

    /// <summary>Gets the render state applied by the context the calling thread records into.
    /// State is per context, not per device.</summary>
    public static RenderStateTracker RenderState => Current.renderState;

    /// <summary>
    /// The command buffer to record draws into, between this context's <see cref="GraphicsContext.Begin"/>
    /// and <see cref="GraphicsContext.End"/>, in <see cref="GraphicsBackend.Vulkan"/> mode. Throws if
    /// the current context's surface is not <see cref="IVulkanGraphicsSurface"/> (an OpenGL surface,
    /// or none), or if called outside that scope.
    /// </summary>
    public static VkCommandBuffer VulkanCommandBuffer => ((IVulkanGraphicsSurface)(Current.surface
        ?? throw new InvalidOperationException("This context has no surface, so it has no Vulkan command buffer either.")))
        .CommandBuffer;

    internal GraphicsContext(GraphicsDevice device, IGraphicsSurface? surface)
    {
        Device = device;
        this.surface = surface;
    }

    /// <summary>
    /// Gets the context the calling thread is recording into.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no context is current on this thread, which means the caller is issuing graphics
    /// work without a context, or from a thread the context was never handed to.
    /// </exception>
    public static GraphicsContext Current => current
        ?? throw new InvalidOperationException(
            $"No graphics context is current on thread {Environment.CurrentManagedThreadId}. "
            + $"Graphics work can only be issued on a thread that has made a context current.");

    /// <summary>
    /// Opens this context for recording on the calling thread, opening its surface with it.
    /// Close it with <see cref="End"/>.
    /// </summary>
    public void Begin()
    {
        surface?.Begin();
        current = this;
    }

    /// <summary>
    /// Closes this context and its surface on the calling thread. Like the surface it drives, this
    /// is not nestable: the innermost call closes for good.
    /// </summary>
    public void End()
    {
        if (current != this)
        {
            return;
        }

        current = null;
        surface?.End();
    }
}
