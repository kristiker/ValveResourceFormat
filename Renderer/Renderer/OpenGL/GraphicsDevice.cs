using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.Vulkan;
using Vortice.Vulkan;

namespace ValveResourceFormat.Renderer;

/// <summary>
/// Creates the GPU objects that one or more <see cref="GraphicsContext"/> record against.
/// <para>
/// In <see cref="GraphicsBackend.Vulkan"/> mode, every <c>int</c> handle this hands out is a
/// synthetic ID, not a real Vulkan handle (which does not fit in 32 bits the way GL's do): the real
/// object - a <see cref="VulkanBuffer"/> today, others as this grows - lives in a table owned by
/// this device, and the handle is just its key. This keeps the whole existing GL-shaped
/// <c>int Handle</c> surface (<c>Buffer</c>, <c>RenderTexture</c>, ...) working unchanged for both
/// backends; a caller that needs the real Vulkan object for a raw Vulkan call (binding a vertex
/// buffer, say) resolves it back with <see cref="ResolveVulkanBuffer"/>.
/// </para>
/// </summary>
public sealed class GraphicsDevice
{
    internal static GraphicsDevice Current => GraphicsContext.Current.Device;

    /// <summary>Which graphics API the current context's device's handles are backed by.</summary>
    public static GraphicsBackend CurrentBackend => Current.Backend;

    /// <summary>Which graphics API this device's handles are backed by.</summary>
    public GraphicsBackend Backend { get; private set; }

    // Only set in Vulkan mode; the caller that already knows how to stand up a VulkanInstance and
    // VulkanDevice against its window's surface hands the finished device over rather than this
    // class doing it, since surface creation is inherently platform code this project keeps out of
    // the renderer proper (see VulkanWin32Surface).
    private VulkanDevice? vulkanDevice;

    private int nextVulkanHandle = 1; // 0 stays free, matching GL's "no object" convention.
    private readonly Dictionary<int, VulkanBuffer> vulkanBuffers = [];
    private readonly Dictionary<int, VulkanImage> vulkanImages = [];

    /// <summary>
    /// Creates an OpenGL-backed device. Called once per set of GPU objects that can be used with
    /// each other.
    /// </summary>
    /// <returns>The new device, with no contexts yet.</returns>
    public static GraphicsDevice Create()
    {
        return new GraphicsDevice { Backend = GraphicsBackend.OpenGL };
    }

    /// <summary>
    /// Creates a Vulkan-backed device around an already-created <see cref="VulkanDevice"/>. See the
    /// type-level remarks for what a handle means in this mode.
    /// </summary>
    /// <param name="vulkanDevice">The device to create objects against, already attached to a surface's instance.</param>
    public static GraphicsDevice Create(VulkanDevice vulkanDevice)
    {
        ArgumentNullException.ThrowIfNull(vulkanDevice);
        return new GraphicsDevice { Backend = GraphicsBackend.Vulkan, vulkanDevice = vulkanDevice };
    }

    /// <summary>
    /// Creates a context that records against this device's objects.
    /// </summary>
    /// <param name="surface">The window side of the context, opened along with it.</param>
    /// <returns>The new context, not yet current on any thread.</returns>
    public GraphicsContext CreateContext(IGraphicsSurface surface)
    {
        return new GraphicsContext(this, surface);
    }

    /// <summary>
    /// Creates a context for a surface whose currency the caller owns, such as a window made
    /// current once and never released.
    /// </summary>
    /// <returns>The new context, not yet current on any thread.</returns>
    public GraphicsContext CreateContext()
    {
        return new GraphicsContext(this, surface: null);
    }

    /// <summary>Creates a buffer object.</summary>
    public static int CreateBuffer(string name) => Current.CreateBufferCore(name);

    /// <summary>Creates a buffer object holding a copy of the given data.</summary>
    /// <typeparam name="T">Element type of the data.</typeparam>
    /// <param name="name">Debug label, visible in graphics debuggers.</param>
    /// <param name="data">Contents to upload.</param>
    /// <param name="usage">Who writes the buffer and who reads it.</param>
    /// <returns>The buffer handle.</returns>
    public static int CreateBuffer<T>(string name, ReadOnlySpan<T> data, BufferUsage usage) where T : unmanaged
        => Current.CreateBufferCore(name, data, usage);

    /// <summary>Deletes a buffer object created by this device.</summary>
    public static void DeleteBuffer(int handle) => Current.DeleteBufferCore(handle);

    /// <summary>
    /// The real <see cref="VulkanBuffer"/> behind a handle <see cref="CreateBuffer(string)"/> or
    /// <see cref="CreateBuffer{T}"/> returned in <see cref="GraphicsBackend.Vulkan"/> mode; see the
    /// type-level remarks. Throws in OpenGL mode or for a handle this device did not create.
    /// </summary>
    public static VulkanBuffer ResolveVulkanBuffer(int handle) => Current.vulkanBuffers[handle];

    /// <summary>Creates a texture object of the given target, without storage.</summary>
    public static int CreateTexture(TextureTarget target, string name) => Current.CreateTextureCore(target, name);

    /// <summary>
    /// Commits immutable 2D storage to a texture <see cref="CreateTexture"/> created, without
    /// uploading any data - <see cref="SetTextureData2D"/> does that, per mip. In Vulkan mode this is
    /// the point the real <see cref="VulkanImage"/> actually gets created, since (unlike a GL texture
    /// name) a <c>VkImage</c> needs its format and size at creation and cannot be resized after.
    /// </summary>
    public static void SetTextureStorage2D(int handle, int mipLevels, ImageFormat format, int width, int height, bool srgb = false)
        => Current.SetTextureStorage2DCore(handle, mipLevels, format, width, height, srgb);

    /// <summary>
    /// Uploads one mip level's pixel data to a texture <see cref="SetTextureStorage2D"/> already
    /// committed storage to. <paramref name="format"/> must match what that call was given -
    /// unlike Vulkan, which just copies bytes into an image already committed to a format, GL needs
    /// telling again here what the bytes mean (<c>NotImplementedException</c> for a block-compressed
    /// one for now; it needs <c>GL.CompressedTextureSubImage2D</c>, a different call shape, which
    /// nothing has exercised through this path yet).
    /// </summary>
    public static void SetTextureData2D(int handle, int mipLevel, int width, int height, ImageFormat format, ReadOnlySpan<byte> pixels)
        => Current.SetTextureData2DCore(handle, mipLevel, width, height, format, pixels);

    /// <summary>Deletes a texture object created by this device.</summary>
    public static void DeleteTexture(int handle) => Current.DeleteTextureCore(handle);

    /// <summary>
    /// The real <see cref="VulkanImage"/> behind a handle <see cref="SetTextureStorage2D"/> committed
    /// storage to in <see cref="GraphicsBackend.Vulkan"/> mode; see the type-level remarks. Throws in
    /// OpenGL mode, for a handle this device did not create, or for one <see cref="CreateTexture"/>
    /// reserved that <see cref="SetTextureStorage2D"/> has not been called for yet.
    /// </summary>
    public static VulkanImage ResolveVulkanImage(int handle) => Current.vulkanImages[handle];

    /// <summary>Creates a texture view over a subrange of another texture's storage.</summary>
    public static int CreateTextureView(int texture, TextureTarget target, ImageFormat format, int minLevel, int numLevels, int minLayer, int numLayers, string name)
        => Current.CreateTextureViewCore(texture, target, format, minLevel, numLevels, minLayer, numLayers, name);

    /// <summary>Creates a sampler object with default state.</summary>
    public static int CreateSampler(string name) => Current.CreateSamplerCore(name);

    /// <summary>Creates a framebuffer object without attachments.</summary>
    public static int CreateFramebuffer(string name) => Current.CreateFramebufferCore(name);

    /// <summary>Creates a vertex array object without attributes.</summary>
    public static int CreateVertexArray(string name) => Current.CreateVertexArrayCore(name);

    /// <summary>Creates a query object of the given target.</summary>
    public static int CreateQuery(QueryTarget target, string name) => Current.CreateQueryCore(target, name);

    /// <summary>Creates an empty shader object for one stage.</summary>
    public static int CreateShader(ShaderProgramType stage, string name) => Current.CreateShaderCore(stage, name);

    /// <summary>Creates an empty program object.</summary>
    public static int CreateProgram(string name) => Current.CreateProgramCore(name);

    // The implementations read no instance state yet, but a device holding a real API object will.
#pragma warning disable CA1822 // Mark members as static

    private int CreateBufferCore(string name)
    {
        if (Backend == GraphicsBackend.Vulkan)
        {
            // Unlike a GL buffer, a Vulkan one cannot be created without knowing its size up front
            // (VkBufferCreateInfo.size must be nonzero) and cannot be resized in place afterward, so
            // there is nothing to actually back this handle with yet - only CreateBuffer<T>, which
            // does know the size, can. A caller that never calls that overload gets a handle with no
            // real object behind it in Vulkan mode; ResolveVulkanBuffer throws for it, honestly,
            // rather than fabricating an empty buffer nothing asked for.
            return nextVulkanHandle++;
        }

        GL.CreateBuffers(1, out int handle);
        Label(ObjectLabelIdentifier.Buffer, handle, name);
        return handle;
    }

    private int CreateBufferCore<T>(string name, ReadOnlySpan<T> data, BufferUsage usage) where T : unmanaged
    {
        if (Backend == GraphicsBackend.Vulkan)
        {
            var handle = nextVulkanHandle++;

            // Every current caller of this overload wants the data uploaded once and read many
            // times by the GPU after, whatever BufferUsage it passes - none stream to a Dynamic
            // buffer through this path yet - so this always takes the staged-upload route rather
            // than branching on usage the way the GL path (which can cheaply rewrite a buffer's
            // contents in place at any point) does.
            vulkanBuffers[handle] = VulkanBuffer.CreateWithData(vulkanDevice!, name, data, ToVulkanBufferUsage());
            return handle;
        }

        var glHandle = CreateBufferCore(name);
        var size = data.Length * Unsafe.SizeOf<T>();
        ref var bytes = ref Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(data));

        // Static contents are set here and never written again, which immutable storage lets the
        // driver rely on. It has no zero sized form, so an empty buffer stays mutable.
        if (usage == BufferUsage.Static && size > 0)
        {
            GL.NamedBufferStorage(glHandle, size, ref bytes, BufferStorageFlags.None);
        }
        else
        {
            GL.NamedBufferData(glHandle, size, ref bytes, usage.ToGLBufferUsageHint());
        }

        return glHandle;
    }

    private void DeleteBufferCore(int handle)
    {
        if (Backend == GraphicsBackend.Vulkan)
        {
            // Queues the real destruction rather than doing it now; see VulkanDeleteQueue. A handle
            // CreateBufferCore(string) reserved but nothing ever backed is silently a no-op here,
            // matching GL's own tolerance of deleting an unused (but valid) name.
            if (vulkanBuffers.Remove(handle, out var buffer))
            {
                buffer.Dispose();
            }

            return;
        }

        GL.DeleteBuffer(handle);
    }

    // No GL equivalent decides a buffer's usage flags at creation - GL buffers are target-agnostic
    // until bound - so this covers every role BufferUsage's callers currently put a buffer to
    // (vertex, index, uniform, storage, transfer source) rather than plumbing a second parameter
    // through every existing call site just to narrow it. The cost is a handful of unused capability
    // bits per buffer, not a correctness problem.
    private static VkBufferUsageFlags ToVulkanBufferUsage() => VkBufferUsageFlags.VertexBuffer
        | VkBufferUsageFlags.IndexBuffer
        | VkBufferUsageFlags.UniformBuffer
        | VkBufferUsageFlags.StorageBuffer
        | VkBufferUsageFlags.TransferDst;

    private int CreateTextureCore(TextureTarget target, string name)
    {
        if (Backend == GraphicsBackend.Vulkan)
        {
            // Mirrors CreateBufferCore(string): a VkImage needs a format and size that are not known
            // yet, so this only reserves the handle. SetTextureStorage2DCore is what actually backs it.
            return nextVulkanHandle++;
        }

        GL.CreateTextures(target, 1, out int handle);
        Label(ObjectLabelIdentifier.Texture, handle, name);
        return handle;
    }

    private void SetTextureStorage2DCore(int handle, int mipLevels, ImageFormat format, int width, int height, bool srgb)
    {
        if (Backend == GraphicsBackend.Vulkan)
        {
            vulkanImages[handle] = VulkanImage.CreateWithStorage2D(vulkanDevice!, string.Empty, (uint)width, (uint)height, format.ToVkFormat(srgb), mipLevels);
            return;
        }

        GL.TextureStorage2D(handle, mipLevels, format.ToGLSizedInternalFormat(srgb), width, height);
    }

    private void SetTextureData2DCore(int handle, int mipLevel, int width, int height, ImageFormat format, ReadOnlySpan<byte> pixels)
    {
        if (Backend == GraphicsBackend.Vulkan)
        {
            vulkanImages[handle].SetData(mipLevel, (uint)width, (uint)height, pixels);
            return;
        }

        if (format.IsBlockCompressed())
        {
            throw new NotImplementedException($"Block-compressed upload ({format}) is not wired through GraphicsDevice yet.");
        }

        ref var bytes = ref MemoryMarshal.GetReference(pixels);
        GL.TextureSubImage2D(handle, mipLevel, 0, 0, width, height, format.ToGLPixelFormat(), format.ToGLPixelType(), ref bytes);
    }

    private void DeleteTextureCore(int handle)
    {
        if (Backend == GraphicsBackend.Vulkan)
        {
            // Same tolerance as DeleteBufferCore for a handle nothing ever backed; see its comment.
            if (vulkanImages.Remove(handle, out var image))
            {
                image.Dispose();
            }

            return;
        }

        GL.DeleteTexture(handle);
    }

    private int CreateTextureViewCore(int texture, TextureTarget target, ImageFormat format, int minLevel, int numLevels, int minLayer, int numLayers, string name)
    {
        // A view needs a name without a target yet, which only the non-DSA path hands out.
        var handle = GL.GenTexture();
        GL.TextureView(handle, target, texture, (PixelInternalFormat)format.ToGLSizedInternalFormat(), minLevel, numLevels, minLayer, numLayers);
        Label(ObjectLabelIdentifier.Texture, handle, name);
        return handle;
    }

    private int CreateSamplerCore(string name)
    {
        GL.CreateSamplers(1, out int handle);
        Label(ObjectLabelIdentifier.Sampler, handle, name);
        return handle;
    }

    private int CreateFramebufferCore(string name)
    {
        GL.CreateFramebuffers(1, out int handle);
        Label(ObjectLabelIdentifier.Framebuffer, handle, name);
        return handle;
    }

    private int CreateVertexArrayCore(string name)
    {
        GL.CreateVertexArrays(1, out int handle);
        Label(ObjectLabelIdentifier.VertexArray, handle, name);
        return handle;
    }

    private int CreateQueryCore(QueryTarget target, string name)
    {
        GL.CreateQueries(target, 1, out int handle);
        Label(ObjectLabelIdentifier.Query, handle, name);
        return handle;
    }

    private int CreateShaderCore(ShaderProgramType stage, string name)
    {
        var handle = GL.CreateShader(stage.ToGLShaderType());
        Label(ObjectLabelIdentifier.Shader, handle, name);
        return handle;
    }

    private int CreateProgramCore(string name)
    {
        var handle = GL.CreateProgram();
        Label(ObjectLabelIdentifier.Program, handle, name);
        return handle;
    }
    [Conditional("DEBUG")]
    private static void Label(ObjectLabelIdentifier identifier, int handle, string name)
    {
#if DEBUG
        if (name.Length == 0)
        {
            return;
        }

        var maxLength = GLEnvironment.MaxLabelLength;
        var length = maxLength > 0 ? Math.Min(maxLength, name.Length) : name.Length;

        GL.ObjectLabel(identifier, handle, length, name);
#endif
    }
}
