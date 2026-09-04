using Vortice.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// Maps <see cref="ImageFormat"/>, the same engine format vocabulary <c>GLImageFormatExtensions</c>
/// maps to OpenGL, onto <see cref="VkFormat"/> instead. Covers exactly the formats that vocabulary's
/// GL mapping supports today (and no more) - the rest of <see cref="ImageFormat"/>'s ~60 members
/// are not reachable through <c>RenderTexture</c> on either backend yet, so there is nothing this
/// mapping would be tested against by adding them speculatively.
/// </summary>
public static class VulkanImageFormat
{
    /// <summary>Returns the Vulkan format for texture storage.</summary>
    public static VkFormat ToVkFormat(this ImageFormat format, bool srgb = false) => (format, srgb) switch
    {
        (ImageFormat.RGBA8888, false) => VkFormat.R8G8B8A8Unorm,
        (ImageFormat.RGBA8888, true) => VkFormat.R8G8B8A8Srgb,
        (ImageFormat.BGRA8888, false) => VkFormat.B8G8R8A8Unorm,
        (ImageFormat.BGRA8888, true) => VkFormat.B8G8R8A8Srgb,
        (ImageFormat.DXT1 or ImageFormat.DXT1_ONEBITALPHA, false) => VkFormat.Bc1RgbaUnormBlock,
        (ImageFormat.DXT1 or ImageFormat.DXT1_ONEBITALPHA, true) => VkFormat.Bc1RgbaSrgbBlock,
        (ImageFormat.DXT3, false) => VkFormat.Bc2UnormBlock,
        (ImageFormat.DXT3, true) => VkFormat.Bc2SrgbBlock,
        (ImageFormat.DXT5 or ImageFormat.DXT5_NM, false) => VkFormat.Bc3UnormBlock,
        (ImageFormat.DXT5, true) => VkFormat.Bc3SrgbBlock,
        (ImageFormat.BC7, false) => VkFormat.Bc7UnormBlock,
        (ImageFormat.BC7, true) => VkFormat.Bc7SrgbBlock,
        (ImageFormat.R8G8B8_ETC2, false) => VkFormat.Etc2R8G8B8UnormBlock,
        (ImageFormat.R8G8B8_ETC2, true) => VkFormat.Etc2R8G8B8SrgbBlock,
        (ImageFormat.R8G8B8A8_ETC2_EAC, false) => VkFormat.Etc2R8G8B8A8UnormBlock,
        (ImageFormat.R8G8B8A8_ETC2_EAC, true) => VkFormat.Etc2R8G8B8A8SrgbBlock,

        (_, true) => throw new NotImplementedException($"Format {format} has no sRGB variant"),

        (ImageFormat.I8, _) => VkFormat.R8Unorm,
        (ImageFormat.R16, _) => VkFormat.R16Unorm,
        (ImageFormat.RG1616, _) => VkFormat.R16G16Unorm,
        (ImageFormat.RGBA16161616, _) => VkFormat.R16G16B16A16Unorm,
        (ImageFormat.R16F, _) => VkFormat.R16Sfloat,
        (ImageFormat.RG1616F, _) => VkFormat.R16G16Sfloat,
        (ImageFormat.RGBA16161616F, _) => VkFormat.R16G16B16A16Sfloat,
        (ImageFormat.R32F, _) => VkFormat.R32Sfloat,
        (ImageFormat.RG3232F, _) => VkFormat.R32G32Sfloat,
        (ImageFormat.RGBA32323232F, _) => VkFormat.R32G32B32A32Sfloat,
        (ImageFormat.R32_UINT, _) => VkFormat.R32Uint,
        (ImageFormat.RGBA32323232_UINT, _) => VkFormat.R32G32B32A32Uint,
        (ImageFormat.ATI1N, _) => VkFormat.Bc4UnormBlock,
        (ImageFormat.ATI2N, _) => VkFormat.Bc5UnormBlock,
        (ImageFormat.BC6H, _) => VkFormat.Bc6hUfloatBlock,
        (ImageFormat.R11_EAC, _) => VkFormat.EacR11UnormBlock,
        (ImageFormat.RG11_EAC, _) => VkFormat.EacR11G11UnormBlock,
        (ImageFormat.D16, _) => VkFormat.D16Unorm,
        (ImageFormat.D32, _) => VkFormat.D32Sfloat,
        (ImageFormat.D32FS8, _) => VkFormat.D32SfloatS8Uint,
        _ => throw new NotImplementedException($"Unsupported format {format}"),
    };

    /// <summary>Whether the format is a depth (optionally depth/stencil) format, which needs a different image aspect and usage than a color format.</summary>
    public static bool IsDepthFormat(this ImageFormat format) => format is ImageFormat.D16 or ImageFormat.D32 or ImageFormat.D32FS8;
}
