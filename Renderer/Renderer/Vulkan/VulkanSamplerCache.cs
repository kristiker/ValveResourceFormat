using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// Deduplicates <see cref="VkSampler"/> objects by their creation state, the same way
/// <c>MaterialLoader.GetOrCreateSampler</c> deduplicates OpenGL sampler objects - and for Vulkan it
/// is not just an optimization: <c>VkPhysicalDeviceLimits.maxSamplerAllocationCount</c> is a hard
/// cap (as low as a few thousand on some drivers), and hundreds of materials each creating their own
/// sampler would burn through it for no benefit, since wrap/filter/anisotropy combinations number in
/// the dozens at most.
/// </summary>
public sealed unsafe class VulkanSamplerCache : IDisposable
{
    private readonly VulkanDevice device;
    private readonly Dictionary<(RsTextureAddressMode U, RsTextureAddressMode V, bool Mipmaps, bool Anisotropic), VkSampler> cache = [];

    /// <summary>Creates an empty cache. One per device.</summary>
    public VulkanSamplerCache(VulkanDevice device)
    {
        this.device = device;
    }

    /// <summary>Returns a sampler matching the given state, creating and caching one on first request.</summary>
    public VkSampler GetOrCreate(RsTextureAddressMode addressModeU, RsTextureAddressMode addressModeV, bool mipmaps = true, bool anisotropicFiltering = true)
    {
        var key = (addressModeU, addressModeV, mipmaps, anisotropicFiltering);

        if (cache.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var useAnisotropy = anisotropicFiltering && device.MaxSamplerAnisotropy >= 4;

        var createInfo = new VkSamplerCreateInfo
        {
            magFilter = VkFilter.Linear,
            minFilter = VkFilter.Linear,
            mipmapMode = mipmaps ? VkSamplerMipmapMode.Linear : VkSamplerMipmapMode.Nearest,
            addressModeU = ToVulkan(addressModeU),
            addressModeV = ToVulkan(addressModeV),
            addressModeW = ToVulkan(addressModeU),
            maxLod = mipmaps ? VK_LOD_CLAMP_NONE : 0f,
            anisotropyEnable = useAnisotropy,
            maxAnisotropy = useAnisotropy ? device.MaxSamplerAnisotropy : 0f,
        };

        device.Api.vkCreateSampler(&createInfo, out var sampler).CheckResult();
        cache[key] = sampler;
        return sampler;
    }

    private static VkSamplerAddressMode ToVulkan(RsTextureAddressMode mode) => mode switch
    {
        RsTextureAddressMode.Wrap => VkSamplerAddressMode.Repeat,
        RsTextureAddressMode.Mirror => VkSamplerAddressMode.MirroredRepeat,
        RsTextureAddressMode.Clamp => VkSamplerAddressMode.ClampToEdge,
        RsTextureAddressMode.Border => VkSamplerAddressMode.ClampToBorder,
        RsTextureAddressMode.MirrorOnce => VkSamplerAddressMode.MirrorClampToEdge,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var sampler in cache.Values)
        {
            device.Api.vkDestroySampler(sampler);
        }
    }
}
