using System.Runtime.InteropServices;
using ValveResourceFormat.Renderer.Vulkan;
using Vortice.Vulkan;

namespace GUI.Types.GLViewers;

/// <summary>
/// Creates a <see cref="VkSurfaceKHR"/> for a Win32 window, the platform half of what a
/// <see cref="VulkanSwapchain"/> presents to. The renderer's Vulkan classes stay platform-agnostic;
/// this is the one place that has to know the app runs on Windows, same role <see cref="GLFWSurface"/>
/// plays for the OpenGL backend.
/// </summary>
static unsafe class VulkanWin32Surface
{
    /// <summary>Platform surface extension a <see cref="VulkanInstance"/> must enable to call <see cref="Create"/>.</summary>
    public const string ExtensionName = "VK_KHR_win32_surface";

    /// <summary>Creates a surface for the given window handle.</summary>
    public static VkSurfaceKHR Create(VulkanInstance instance, nint hwnd)
    {
        var createInfo = new VkWin32SurfaceCreateInfoKHR
        {
            hinstance = Marshal.GetHINSTANCE(typeof(VulkanWin32Surface).Module),
            hwnd = hwnd,
        };

        instance.Api.vkCreateWin32SurfaceKHR(&createInfo, out var surface).CheckResult();

        return surface;
    }

    /// <summary>Destroys a surface created by <see cref="Create"/>.</summary>
    public static void Destroy(VulkanInstance instance, VkSurfaceKHR surface) => instance.Api.vkDestroySurfaceKHR(surface);
}
