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
    private VulkanTrianglePipeline? triangle;
    private bool swapchainDirty;

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
        triangle = new VulkanTrianglePipeline(device, swapchain.Format);

        renderTimer.Start();
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

        var mvp = Matrix4x4.CreateRotationZ((float)clock.Elapsed.TotalSeconds);

        if (!frame.RenderAndPresent(swapchain, ClearColor, triangle, mvp))
        {
            swapchainDirty = true;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        renderTimer.Stop();
        device?.WaitIdle();

        frame?.Dispose();
        triangle?.Dispose();
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
