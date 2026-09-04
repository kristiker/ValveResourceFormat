using System.Diagnostics;
using System.Numerics;
using System.Windows.Forms;
using GUI.Controls;
using Microsoft.Extensions.Logging;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using ValveResourceFormat.Renderer;
using ValveResourceFormat.Renderer.Buffers;
using ValveResourceFormat.Renderer.Shaders;
using ValveResourceFormat.Renderer.Vulkan;
using Vortice.Vulkan;
using NativeWindow = OpenTK.Windowing.Desktop.NativeWindow;

namespace GUI.Types.GLViewers;

/// <summary>
/// Vulkan backend scaffolding: a real WinForms window, embedding a Vulkan swapchain the same way
/// <see cref="GLBaseControl"/> embeds an OpenGL context - a hidden GLFW window reparented as a
/// native child - proving the surface can be created against the actual GUI, not a standalone
/// test window. Draws one triangle from a GLSL string compiled through glslang, proving the
/// shader-string-to-draw-call path the plan called for. Opened via a debug entry point, not the
/// main menu; see <c>GUI/Program.cs</c>.
/// </summary>
public sealed class VulkanClearTestForm : Form
{
    private static readonly VkClearColorValue ClearColor = new(0.1f, 0.2f, 0.4f, 1.0f);

    private readonly GLControl hostControl;
    private readonly Timer renderTimer;
    private readonly Stopwatch clock = Stopwatch.StartNew();

    private NativeWindow? nativeWindow;
    private VulkanInstance? instance;
    private VulkanDevice? device;
    private GraphicsDevice? graphicsDevice;
    private VkSurfaceKHR surface;
    private VulkanSwapchain? swapchain;
    private VulkanFrame? frame;
    private VulkanBindlessTextures? bindlessTextures;
    private VulkanTrianglePipeline? triangle;
    private int vertexBufferHandle;
    private RenderTexture? checkerTexture;
    private uint checkerTextureIndex;
    private bool swapchainDirty;
    private double lastVertexBufferRebuild;
    private int vertexBufferGeneration;

    // Milestone-4: render state -> PSO cache.
    private VulkanImage? depthImage;
    private VulkanFlatColorShader? flatColorShader;
    private VulkanPipelineCache? pipelineCache;
    private int sceneVertexBufferHandle;
    private double lastCacheStatsLog;

    // Milestone-5: compute.
    private const uint PlasmaSize = 256;
    private VulkanImage? plasmaImage;
    private VulkanComputeShader? plasmaCompute;
    private int plasmaQuadVertexBufferHandle;
    private uint plasmaTextureIndex;
    private VkImageLayout plasmaLayout;

    public VulkanClearTestForm()
    {
        Text = "Source 2 Viewer - Vulkan Test";
        ClientSize = new System.Drawing.Size(1024, 768);

        hostControl = new GLControl(new System.Threading.Lock())
        {
            Dock = DockStyle.Fill,
        };
        hostControl.Load += OnHostControlLoad;
        hostControl.Resize += (_, _) => swapchainDirty = true;
        Controls.Add(hostControl);

        nativeWindow = NativeWindowFactory.Create(new NativeWindowSettings
        {
            API = ContextAPI.NoAPI,
            StartFocused = false,
            StartVisible = false,
            ClientSize = new(4, 4),
            AutoLoadBindings = false,
            AutoIconify = false,
            WindowBorder = WindowBorder.Hidden,
            WindowState = OpenTK.Windowing.Common.WindowState.Normal,
            Title = "Source 2 Viewer Vulkan",
        });
        hostControl.AttachNativeWindow(nativeWindow);

        renderTimer = new Timer { Interval = 16 };
        renderTimer.Tick += (_, _) => RenderFrame();
    }

    private unsafe void OnHostControlLoad(object? sender, EventArgs e)
    {
        var hwnd = (nint)GLFW.GetWin32Window(nativeWindow!.WindowPtr);

        var logger = new ConsoleLogger();

        instance = VulkanInstance.Create(logger, VulkanWin32Surface.ExtensionName);
        surface = VulkanWin32Surface.Create(instance, hwnd);
        device = VulkanDevice.Create(instance, surface, logger);
        swapchain = new VulkanSwapchain(device, surface, (uint)hostControl.Width, (uint)hostControl.Height);
        frame = new VulkanFrame(device);
        bindlessTextures = new VulkanBindlessTextures(device);
        triangle = new VulkanTrianglePipeline(device, swapchain.Format, bindlessTextures, VkFormat.D32Sfloat);

        // Connects this test form's object creation to the real GraphicsDevice/GraphicsContext the
        // rest of the renderer already calls, rather than creating VulkanBuffer directly: proves the
        // GL-shaped int-handle API genuinely dispatches to Vulkan and hands back something usable in
        // a real Vulkan call. Kept current for the form's whole lifetime - there is no per-frame
        // Begin/End cycle here yet (see the type remarks on what this milestone does not cover), so a
        // single standing surface-less context is the honest way to use the static creation API.
        graphicsDevice = GraphicsDevice.Create(device);
        graphicsDevice.CreateContext().Begin();

        vertexBufferHandle = CreateVertexBuffer(vertexBufferGeneration);

        checkerTexture = CreateCheckerTexture();
        var checkerSampler = bindlessTextures.Samplers.GetOrCreate(RsTextureAddressMode.Wrap, RsTextureAddressMode.Wrap, mipmaps: false);
        checkerTextureIndex = bindlessTextures.Register(GraphicsDevice.ResolveVulkanImage(checkerTexture.Handle).View, checkerSampler);

        RunPushConstantPackingSmokeTest();

        depthImage = VulkanImage.CreateDepth(device, "Test depth buffer", (uint)hostControl.Width, (uint)hostControl.Height);
        flatColorShader = new VulkanFlatColorShader(device);
        pipelineCache = new VulkanPipelineCache(device);
        sceneVertexBufferHandle = GraphicsDevice.CreateBuffer("Render state demo triangles", SceneVertices.AsSpan(), BufferUsage.Static);

        plasmaImage = VulkanImage.CreateStorage(device, "Compute plasma target", PlasmaSize, PlasmaSize, VkFormat.R8G8B8A8Unorm);
        plasmaCompute = new VulkanComputeShader(device, plasmaImage);
        var plasmaSampler = bindlessTextures.Samplers.GetOrCreate(RsTextureAddressMode.Clamp, RsTextureAddressMode.Clamp, mipmaps: false);
        plasmaTextureIndex = bindlessTextures.Register(plasmaImage.View, plasmaSampler);
        plasmaQuadVertexBufferHandle = GraphicsDevice.CreateBuffer("Plasma quad", PlasmaQuadVertices.AsSpan(), BufferUsage.Static);

        renderTimer.Start();
    }

    // A unit quad centered on the origin, white so the sampled plasma texture shows untinted. Same
    // [x, y, r, g, b] layout and -0.5..0.5 coordinate convention VulkanTrianglePipeline's fragment
    // shader assumes for its "uv = position + 0.5" trick; DrawPlasmaQuad's mvp scales and moves it
    // into the corner rather than authoring off-center positions the UV math was not built for.
    private static readonly float[] PlasmaQuadVertices =
    [
        -0.5f, -0.5f, 1f, 1f, 1f,
         0.5f, -0.5f, 1f, 1f, 1f,
         0.5f,  0.5f, 1f, 1f, 1f,

        -0.5f, -0.5f, 1f, 1f, 1f,
         0.5f,  0.5f, 1f, 1f, 1f,
        -0.5f,  0.5f, 1f, 1f, 1f,
    ];

    // Dispatches the plasma compute shader, then transitions its output for sampling. Runs before
    // vkCmdBeginRendering (see VulkanFrame.RenderAndPresent's computeDispatch parameter), since
    // vkCmdDispatch is not legal inside a dynamic rendering instance.
    private void DispatchPlasmaCompute(VkCommandBuffer commandBuffer)
    {
        var image = plasmaImage!;

        VulkanBarrier.TransitionImage(device!.Api, commandBuffer, image.Handle, VkImageAspectFlags.Color, ref plasmaLayout, VkImageLayout.General,
            VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderRead,
            VkPipelineStageFlags2.ComputeShader, VkAccessFlags2.ShaderStorageWrite);

        plasmaCompute!.Dispatch(commandBuffer, (float)clock.Elapsed.TotalSeconds, PlasmaSize, PlasmaSize);

        VulkanBarrier.TransitionImage(device.Api, commandBuffer, image.Handle, VkImageAspectFlags.Color, ref plasmaLayout, VkImageLayout.ShaderReadOnlyOptimal,
            VkPipelineStageFlags2.ComputeShader, VkAccessFlags2.ShaderStorageWrite,
            VkPipelineStageFlags2.FragmentShader, VkAccessFlags2.ShaderRead);
    }

    // Draws the plasma quad with the same VulkanTrianglePipeline/bindless set the checkerboard
    // triangle uses, just a different vertex buffer and bindless texture index - proving the
    // compute-written image is a completely ordinary bindless texture to everything downstream of it.
    private unsafe void DrawPlasmaQuad(VkCommandBuffer commandBuffer)
    {
        device!.Api.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Graphics, triangle!.Handle);

        var set = bindlessTextures!.Set;
        device.Api.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Graphics, triangle.Layout, 0, 1, &set, 0, null);

        // Shrink the unit quad to a third size and push it into the bottom-right corner.
        var mvp = Matrix4x4.CreateScale(0.35f) * Matrix4x4.CreateTranslation(0.6f, 0.6f, 0f);
        var pushConstants = new VulkanTrianglePipeline.PushConstants(mvp, plasmaTextureIndex);
        device.Api.vkCmdPushConstants(commandBuffer, triangle.Layout, VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment,
            0, (uint)sizeof(VulkanTrianglePipeline.PushConstants), &pushConstants);

        device.Api.vkCmdBindVertexBuffer(commandBuffer, 0, GraphicsDevice.ResolveVulkanBuffer(plasmaQuadVertexBufferHandle).Handle);
        device.Api.vkCmdDraw(commandBuffer, 6, 1, 0, 0);
    }

    // Three overlapping triangles in NDC-ish space (x, y, z), positioned so B occludes A where they
    // overlap under a standard (not reverse-Z) Less depth test, and C - drawn last, alpha-blended,
    // depth-write disabled - washes over both. A and B share one RenderState (opaque, cull-back), so
    // baking A's pipeline should make B's draw a cache hit; C's RenderState (blended, no cull) is
    // the one guaranteed miss besides A's.
    //
    // Winding is deliberately v0, v2, v1 (not v0, v1, v2): Vulkan's NDC Y axis points down by
    // default, unlike OpenGL's, so a triangle that is counter-clockwise by the usual Y-up convention
    // rasterizes clockwise here - and with FrontFace.CounterClockwise + CullMode.Back, that reads as
    // a back face and vanishes. C has CullMode.None, so its winding never mattered; A and B's did.
    private static readonly float[] SceneVertices =
    [
        // Triangle A: opaque red, back, left-weighted
        -0.9f, -0.6f, 0.6f,
        -0.3f,  0.6f, 0.6f,
         0.3f, -0.6f, 0.6f,

        // Triangle B: opaque blue, closer, right-weighted, overlaps A in the middle
        -0.3f, -0.6f, 0.2f,
         0.3f,  0.6f, 0.2f,
         0.9f, -0.6f, 0.2f,

        // Triangle C: translucent green, closest, covers both
        -1.2f, -1.0f, 0.05f,
         1.2f, -1.0f, 0.05f,
         0.0f,  1.4f, 0.05f,
    ];

    private static readonly RenderState OpaqueCullBackState = BuildOpaqueCullBackState();
    private static readonly RenderState BlendedNoCullState = BuildBlendedNoCullState();

    private static RenderState BuildOpaqueCullBackState()
    {
        var state = new RenderState
        {
            Rasterizer = new() { FillMode = RsFillMode.Solid, CullMode = RsCullMode.Back, DepthClipEnable = true, MultisampleEnable = true },
            DepthStencil = new() { DepthTestEnable = true, DepthWriteEnable = true, DepthFunc = RsComparison.Less },
        };
        state.ColorWriteMask = RsColorWriteEnableBits.All;
        return state;
    }

    private static RenderState BuildBlendedNoCullState()
    {
        var state = new RenderState
        {
            Rasterizer = new() { FillMode = RsFillMode.Solid, CullMode = RsCullMode.None, DepthClipEnable = true, MultisampleEnable = true },
            DepthStencil = new() { DepthTestEnable = true, DepthWriteEnable = false, DepthFunc = RsComparison.Less },
        };
        state.BlendEnable = true;
        state.SetBlend(RsBlendMode.SrcAlpha, RsBlendMode.InvSrcAlpha);
        state.ColorWriteMask = RsColorWriteEnableBits.All;
        return state;
    }

    // Binds VulkanPipelineCache's baked pipeline for each triangle's RenderState and draws it -
    // called from VulkanFrame between BeginRendering and EndRendering, with viewport/scissor already
    // set. Logs cache hit/miss counts periodically so the reuse (or lack of it) is visible, not just
    // assumed from the code.
    private unsafe void DrawRenderStateDemo(VkCommandBuffer commandBuffer, Matrix4x4 mvp)
    {
        var shader = flatColorShader!;
        var cache = pipelineCache!;

        device!.Api.vkCmdBindVertexBuffer(commandBuffer, 0, GraphicsDevice.ResolveVulkanBuffer(sceneVertexBufferHandle).Handle);

        var vertexBinding = VulkanFlatColorShader.VertexBinding;
        var vertexAttribute = VulkanFlatColorShader.VertexAttribute;
        var vertexInputState = new VkPipelineVertexInputStateCreateInfo
        {
            vertexBindingDescriptionCount = 1,
            pVertexBindingDescriptions = &vertexBinding,
            vertexAttributeDescriptionCount = 1,
            pVertexAttributeDescriptions = &vertexAttribute,
        };

        void DrawTriangle(int firstVertex, Vector4 color, in RenderState state)
        {
            var pipeline = cache.GetOrCreate(shader.VertexModule, shader.FragmentModule, shader.Layout, in vertexInputState,
                in state, VkPrimitiveTopology.TriangleList, swapchain!.Format, VkFormat.D32Sfloat);

            device.Api.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Graphics, pipeline);

            var pushConstants = new VulkanFlatColorShader.PushConstants(mvp, color);
            device.Api.vkCmdPushConstants(commandBuffer, shader.Layout, VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment,
                0, (uint)sizeof(VulkanFlatColorShader.PushConstants), &pushConstants);

            device.Api.vkCmdDraw(commandBuffer, 3, 1, (uint)firstVertex, 0);
        }

        DrawTriangle(0, new Vector4(0.85f, 0.15f, 0.15f, 1.0f), in OpaqueCullBackState);
        DrawTriangle(3, new Vector4(0.15f, 0.35f, 0.9f, 1.0f), in OpaqueCullBackState);
        DrawTriangle(6, new Vector4(0.15f, 0.8f, 0.3f, 0.5f), in BlendedNoCullState);

        if (clock.Elapsed.TotalSeconds - lastCacheStatsLog >= 2.0)
        {
            lastCacheStatsLog = clock.Elapsed.TotalSeconds;
            Console.WriteLine($"[PipelineCache] {cache.CacheHits} hit(s), {cache.CacheMisses} miss(es) (expect 2 misses total, then all hits)");
        }
    }

    // Proves VulkanPushConstantLayout against a real renderer shader, not a hand-written test string:
    // crosshair.vert.slang declares "uniform mat4 transform;", a loose per-draw uniform exactly like
    // the ones this is meant to pack. Parses it with the real ShaderParser, packs the result, rewrites
    // the loose declaration into a push-constant member, and compiles that through glslang - logging
    // rather than asserting, since this is scaffolding proof, not the real integration point yet.
    private static void RunPushConstantPackingSmokeTest()
    {
        const string shaderFile = "crosshair.vert.slang";

        var parser = new ShaderParser();
        var parsedData = new ShaderLoader.ParsedShaderData();
        var source = parser.PreprocessShader(shaderFile, parsedData);
        parser.ClearBuilder();

        var layout = VulkanPushConstantLayout.Build(parsedData.PushConstantDeclarations);

        Console.WriteLine($"[PushConstantSmokeTest] {shaderFile}: {layout.Members.Count} push-constant member(s), {layout.Size} bytes");

        foreach (var member in layout.Members.Values)
        {
            Console.WriteLine($"[PushConstantSmokeTest]   {member.Name} : {member.Type} @ offset {member.Offset}");
        }

        var vulkanSource = layout.ApplyToSource(source);
        var spirv = VulkanGlslang.Compile(vulkanSource, VulkanGlslang.Stage.Vertex);

        Console.WriteLine($"[PushConstantSmokeTest] Compiled to {spirv.Length} bytes of SPIR-V");
    }

    // 8x8 magenta/white checkerboard: visually unmistakable as "a real sampled texture", not a
    // solid fallback color, proving a RenderTexture created through GraphicsDevice - not VulkanImage
    // directly - actually reaches the GPU. This is the same RenderTexture.Create/SetData path real
    // material textures use; see the comment where graphicsDevice is created in OnHostControlLoad.
    private static RenderTexture CreateCheckerTexture()
    {
        const int size = 8;
        var pixels = new byte[size * size * 4];

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var isMagenta = ((x + y) & 1) == 0;
                var offset = (y * size + x) * 4;

                pixels[offset + 0] = isMagenta ? (byte)230 : (byte)255;
                pixels[offset + 1] = isMagenta ? (byte)40 : (byte)255;
                pixels[offset + 2] = isMagenta ? (byte)200 : (byte)255;
                pixels[offset + 3] = 255;
            }
        }

        var texture = RenderTexture.Create(size, size, ImageFormat.RGBA8888, 1, "Checker test texture");
        texture.SetData(0, size, size, ImageFormat.RGBA8888, pixels);
        return texture;
    }

    // Interleaved [x, y, r, g, b] per vertex, matching VulkanTrianglePipeline.VertexStride. The
    // scale pulses with the buffer's generation so a rebuild is visible, not just a log line.
    // Created through GraphicsDevice rather than VulkanBuffer directly - see the comment where
    // graphicsDevice is created in OnHostControlLoad.
    private static int CreateVertexBuffer(int generation)
    {
        var scale = 0.6f + 0.35f * MathF.Sin(generation);

        ReadOnlySpan<float> vertices =
        [
            0.0f * scale, -0.5f * scale, 0.9f, 0.2f, 0.2f,
            0.5f * scale, 0.5f * scale, 0.2f, 0.9f, 0.3f,
            -0.5f * scale, 0.5f * scale, 0.3f, 0.4f, 0.95f,
        ];

        return GraphicsDevice.CreateBuffer("Triangle vertices", vertices, BufferUsage.Static);
    }

    // Rebuilds the vertex buffer every couple of seconds and disposes the old one immediately -
    // exercising the delete queue under real frame-in-flight timing, not just at shutdown.
    private void RebuildVertexBufferIfDue()
    {
        if (clock.Elapsed.TotalSeconds - lastVertexBufferRebuild < 2.0)
        {
            return;
        }

        lastVertexBufferRebuild = clock.Elapsed.TotalSeconds;
        vertexBufferGeneration++;

        var old = vertexBufferHandle;
        vertexBufferHandle = CreateVertexBuffer(vertexBufferGeneration);
        GraphicsDevice.DeleteBuffer(old);
    }

    private void RenderFrame()
    {
        if (swapchain is null || frame is null || hostControl.Width <= 0 || hostControl.Height <= 0)
        {
            return;
        }

        if (swapchainDirty)
        {
            swapchain.Recreate((uint)hostControl.Width, (uint)hostControl.Height);

            var oldDepth = depthImage;
            depthImage = VulkanImage.CreateDepth(device!, "Test depth buffer", (uint)hostControl.Width, (uint)hostControl.Height);
            oldDepth?.Dispose();

            swapchainDirty = false;
        }

        RebuildVertexBufferIfDue();

        var mvp = Matrix4x4.CreateRotationZ((float)clock.Elapsed.TotalSeconds);

        // Kept static (no rotation): without a real projection matrix, spinning these around Y would
        // skew flat clip-space triangles in a way that reads as broken rather than "3D", and the point
        // here is to see the depth/blend/cull result clearly, not to fake a camera.
        if (!frame.RenderAndPresent(swapchain, ClearColor, triangle, bindlessTextures, GraphicsDevice.ResolveVulkanBuffer(vertexBufferHandle), mvp, checkerTextureIndex,
            depthImage,
            extraDraws: commandBuffer =>
            {
                DrawRenderStateDemo(commandBuffer, Matrix4x4.Identity);
                DrawPlasmaQuad(commandBuffer);
            },
            computeDispatch: DispatchPlasmaCompute))
        {
            swapchainDirty = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        renderTimer.Stop();
        device?.WaitIdle();

        frame?.Dispose();

        if (graphicsDevice != null)
        {
            GraphicsDevice.DeleteBuffer(vertexBufferHandle);
            GraphicsDevice.DeleteBuffer(sceneVertexBufferHandle);
            GraphicsDevice.DeleteBuffer(plasmaQuadVertexBufferHandle);
        }

        checkerTexture?.Delete();
        triangle?.Dispose();
        bindlessTextures?.Dispose();
        pipelineCache?.Dispose();
        flatColorShader?.Dispose();
        depthImage?.Dispose();
        plasmaCompute?.Dispose();
        plasmaImage?.Dispose();
        swapchain?.Dispose();

        if (instance != null && surface.IsNotNull)
        {
            VulkanWin32Surface.Destroy(instance, surface);
        }

        device?.Dispose();
        instance?.Dispose();

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            renderTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    // Writes to the inherited console handle instead of the real logging pipeline (which needs
    // MainForm's console tab), since this form is reachable without MainForm existing at all.
    private sealed class ConsoleLogger : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Console.WriteLine($"[{logLevel}] {formatter(state, exception)}");
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
