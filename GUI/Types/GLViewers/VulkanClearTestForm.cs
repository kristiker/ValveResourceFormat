using System.Diagnostics;
using System.Numerics;
using System.Windows.Forms;
using GUI.Controls;
using Microsoft.Extensions.Logging;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
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
    private VkSurfaceKHR surface;
    private VulkanSwapchain? swapchain;
    private VulkanFrame? frame;
    private VulkanBindlessTextures? bindlessTextures;
    private VulkanTrianglePipeline? triangle;
    private VulkanBuffer? vertexBuffer;
    private VulkanImage? checkerTexture;
    private uint checkerTextureIndex;
    private bool swapchainDirty;
    private double lastVertexBufferRebuild;
    private int vertexBufferGeneration;

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
        triangle = new VulkanTrianglePipeline(device, swapchain.Format, bindlessTextures);
        vertexBuffer = CreateVertexBuffer(device, vertexBufferGeneration);

        checkerTexture = CreateCheckerTexture(device);
        checkerTextureIndex = bindlessTextures.Register(checkerTexture.View, bindlessTextures.DefaultSampler);

        renderTimer.Start();
    }

    // 8x8 magenta/white checkerboard: visually unmistakable as "a real sampled texture", not a
    // solid fallback color, proving VulkanImage's staging upload actually reached the GPU.
    private static VulkanImage CreateCheckerTexture(VulkanDevice device)
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

        return VulkanImage.CreateRgba8(device, "Checker test texture", size, size, pixels);
    }

    // Interleaved [x, y, r, g, b] per vertex, matching VulkanTrianglePipeline.VertexStride. The
    // scale pulses with the buffer's generation so a rebuild is visible, not just a log line.
    private static VulkanBuffer CreateVertexBuffer(VulkanDevice device, int generation)
    {
        var scale = 0.6f + 0.35f * MathF.Sin(generation);

        ReadOnlySpan<float> vertices =
        [
            0.0f * scale, -0.5f * scale, 0.9f, 0.2f, 0.2f,
            0.5f * scale, 0.5f * scale, 0.2f, 0.9f, 0.3f,
            -0.5f * scale, 0.5f * scale, 0.3f, 0.4f, 0.95f,
        ];

        return VulkanBuffer.CreateWithData(device, "Triangle vertices", vertices, VkBufferUsageFlags.VertexBuffer);
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

        var old = vertexBuffer;
        vertexBuffer = CreateVertexBuffer(device!, vertexBufferGeneration);
        old?.Dispose();
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
            swapchainDirty = false;
        }

        RebuildVertexBufferIfDue();

        var mvp = Matrix4x4.CreateRotationZ((float)clock.Elapsed.TotalSeconds);

        if (!frame.RenderAndPresent(swapchain, ClearColor, triangle, bindlessTextures, vertexBuffer, mvp, checkerTextureIndex))
        {
            swapchainDirty = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        renderTimer.Stop();
        device?.WaitIdle();

        frame?.Dispose();
        vertexBuffer?.Dispose();
        checkerTexture?.Dispose();
        triangle?.Dispose();
        bindlessTextures?.Dispose();
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
