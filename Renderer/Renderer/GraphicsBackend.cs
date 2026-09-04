namespace ValveResourceFormat.Renderer;

/// <summary>
/// Which graphics API a <see cref="GraphicsDevice"/> creates its objects through.
/// </summary>
public enum GraphicsBackend
{
    /// <summary>OpenGL, driven directly - handles returned by <see cref="GraphicsDevice"/> are the real GL object names.</summary>
    OpenGL,

    /// <summary>
    /// Vulkan. Handles returned by <see cref="GraphicsDevice"/> are synthetic IDs backed by a table
    /// of real Vulkan objects internal to the device, since Vulkan's handles do not fit in an
    /// <see cref="int"/> the way GL's do; resolve one back with
    /// <see cref="GraphicsDevice.ResolveVulkanBuffer"/> (and its siblings, as they are added) when
    /// the real object is needed rather than just a handle to pass back into
    /// <see cref="GraphicsDevice"/> itself.
    /// </summary>
    Vulkan,
}
