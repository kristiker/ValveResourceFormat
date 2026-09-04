using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// One shared, update-after-bind combined-image-sampler array, so every pipeline can bind the same
/// descriptor set and just look a texture up by index instead of needing its own per-material
/// descriptor set. A shader receives the index (as a push constant for now) rather than a
/// descriptor binding; see the design note on <see cref="VulkanTrianglePipeline"/>'s push constants.
/// <para>
/// <see cref="Vortice.Vulkan.VkDescriptorBindingFlags.PartiallyBound"/> lets some array slots stay
/// unwritten, but sampling one of those is still undefined - it does not mean "reads as black". The
/// OpenGL backend's own bindless-texture work (<c>bindless-material-textures</c>) hit exactly this:
/// its <c>SceneTextures</c>/<c>RenderMaterial.FillSamplerHandles</c> never leave a packed handle
/// unwritten, falling back to a small per-sampler-kind "null texture" instead
/// (<c>MaterialLoader.GetNullTexture</c>). This mirrors that: slot <see cref="NullTextureIndex"/> is
/// written at construction, before anything else, so any index that has not been registered yet -
/// or has been forgotten to be - is always safe to sample rather than merely usually safe.
/// </para>
/// <para>
/// Slots are otherwise allocated monotonically and never freed yet - a registered texture is
/// expected to outlive the registry for now. Recycling freed slots needs the same "safe once nothing
/// in flight could still read it" reasoning <see cref="VulkanDeleteQueue"/> gives buffer/image
/// destruction, which is a later step once something actually unregisters a texture at runtime.
/// </para>
/// </summary>
public sealed unsafe class VulkanBindlessTextures : IDisposable
{
    /// <summary>Binding index the shared array is declared at, in every pipeline's set 0.</summary>
    public const uint Binding = 0;

    /// <summary>How many textures can ever be registered; sized generously since the cost is address space, not memory.</summary>
    private const uint Capacity = 4096;

    private readonly VulkanDevice device;
    private uint nextSlot;

    /// <summary>The set layout every pipeline using bindless textures includes as set 0.</summary>
    public VkDescriptorSetLayout Layout { get; }

    /// <summary>The one descriptor set every draw binds; textures are looked up in it by index.</summary>
    public VkDescriptorSet Set { get; }

    /// <summary>Deduplicated samplers by wrap/filter/anisotropy state; see <see cref="VulkanSamplerCache"/>.</summary>
    public VulkanSamplerCache Samplers { get; }

    /// <summary>
    /// Index of a 1x1 white placeholder, safe to sample as a stand-in for anything not registered
    /// yet. Always slot 0; see the type-level remarks.
    /// </summary>
    public uint NullTextureIndex { get; }

    private readonly VkDescriptorPool pool;
    private readonly VulkanImage nullTexture;

    /// <summary>Creates the shared layout, pool, set, default sampler, and null texture. One per device.</summary>
    public VulkanBindlessTextures(VulkanDevice device)
    {
        this.device = device;

        var bindingFlags = VkDescriptorBindingFlags.PartiallyBound | VkDescriptorBindingFlags.UpdateAfterBind;

        var binding = new VkDescriptorSetLayoutBinding
        {
            binding = Binding,
            descriptorType = VkDescriptorType.CombinedImageSampler,
            descriptorCount = Capacity,
            stageFlags = VkShaderStageFlags.All,
        };

        var bindingFlagsInfo = new VkDescriptorSetLayoutBindingFlagsCreateInfo
        {
            bindingCount = 1,
            pBindingFlags = &bindingFlags,
        };

        var layoutCreateInfo = new VkDescriptorSetLayoutCreateInfo
        {
            pNext = &bindingFlagsInfo,
            flags = VkDescriptorSetLayoutCreateFlags.UpdateAfterBindPool,
            bindingCount = 1,
            pBindings = &binding,
        };

        device.Api.vkCreateDescriptorSetLayout(&layoutCreateInfo, out var layout).CheckResult();
        Layout = layout;

        var poolSize = new VkDescriptorPoolSize { type = VkDescriptorType.CombinedImageSampler, descriptorCount = Capacity };

        var poolCreateInfo = new VkDescriptorPoolCreateInfo
        {
            flags = VkDescriptorPoolCreateFlags.UpdateAfterBind,
            maxSets = 1,
            poolSizeCount = 1,
            pPoolSizes = &poolSize,
        };

        device.Api.vkCreateDescriptorPool(&poolCreateInfo, out pool).CheckResult();

        var allocateInfo = new VkDescriptorSetAllocateInfo
        {
            descriptorPool = pool,
            descriptorSetCount = 1,
            pSetLayouts = &layout,
        };

        device.Api.vkAllocateDescriptorSets(allocateInfo, out var set).CheckResult();
        Set = set;

        Samplers = new VulkanSamplerCache(device);

        ReadOnlySpan<byte> whitePixel = [255, 255, 255, 255];
        nullTexture = VulkanImage.CreateRgba8(device, "Bindless null texture", 1, 1, whitePixel);
        var nullSampler = Samplers.GetOrCreate(RsTextureAddressMode.Wrap, RsTextureAddressMode.Wrap, mipmaps: false, anisotropicFiltering: false);
        NullTextureIndex = Register(nullTexture.View, nullSampler);
    }

    /// <summary>
    /// Writes <paramref name="view"/>+<paramref name="sampler"/> into a new slot and returns its
    /// index, which a shader can be given (e.g. through a push constant) to sample it via the shared
    /// array bound at <see cref="Binding"/>.
    /// </summary>
    public uint Register(VkImageView view, VkSampler sampler)
    {
        var slot = nextSlot++;

        if (slot >= Capacity)
        {
            throw new InvalidOperationException($"Ran out of bindless texture slots ({Capacity}).");
        }

        var imageInfo = new VkDescriptorImageInfo
        {
            sampler = sampler,
            imageView = view,
            imageLayout = VkImageLayout.ShaderReadOnlyOptimal,
        };

        var write = new VkWriteDescriptorSet
        {
            dstSet = Set,
            dstBinding = Binding,
            dstArrayElement = slot,
            descriptorCount = 1,
            descriptorType = VkDescriptorType.CombinedImageSampler,
            pImageInfo = &imageInfo,
        };

        device.Api.vkUpdateDescriptorSets(write);

        return slot;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        nullTexture.Dispose();
        Samplers.Dispose();
        device.Api.vkDestroyDescriptorPool(pool);
        device.Api.vkDestroyDescriptorSetLayout(Layout);
    }
}
