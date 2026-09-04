using Vortice.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// Translates the same <see cref="RenderState"/> descriptors the OpenGL backend diffs and applies
/// per draw (<see cref="RenderStateTracker.Apply"/>) into the Vulkan pipeline-state structs a
/// <see cref="VulkanPipelineCache"/> bakes once and reuses. Nothing here is Vulkan-specific renderer
/// policy - it is a one-to-one mapping of already-existing, backend-neutral enums, most of which
/// share Vulkan's own bit layout by construction (this schema is D3D11-shaped, and Vulkan followed
/// D3D11 for these particular enums) and are cast directly; only <see cref="RsComparison"/> (which
/// carries an extra reverse-Z flag bit the base compare op does not) and <see cref="RsBlendMode"/>
/// (D3D and Vulkan order color/alpha factors differently) need an actual switch.
/// </summary>
public static class VulkanRenderState
{
    /// <summary>Translates the rasterizer descriptor. Depth bias is left disabled here; see the type-level remarks in <see cref="VulkanPipelineCache"/> for why.</summary>
    public static VkPipelineRasterizationStateCreateInfo ToRasterizationState(in RsRasterizerStateDesc rasterizer) => new()
    {
        polygonMode = rasterizer.FillMode == RsFillMode.Wireframe ? VkPolygonMode.Line : VkPolygonMode.Fill,
        cullMode = ToCullMode(rasterizer.CullMode),
        frontFace = VkFrontFace.CounterClockwise,
        depthClampEnable = !rasterizer.DepthClipEnable,
        lineWidth = 1.0f,
    };

    /// <summary>Translates the depth/stencil descriptor. The stencil reference is dynamic state in both this schema and Vulkan; see <see cref="VulkanPipelineCache"/>.</summary>
    public static VkPipelineDepthStencilStateCreateInfo ToDepthStencilState(in RsDepthStencilStateDesc depthStencil) => new()
    {
        sType = VkStructureType.PipelineDepthStencilStateCreateInfo,
        depthTestEnable = depthStencil.DepthTestEnable,
        depthWriteEnable = depthStencil.DepthWriteEnable,
        depthCompareOp = ToCompareOp(depthStencil.DepthFunc),
        stencilTestEnable = depthStencil.StencilEnable,
        front = ToStencilOpState(depthStencil, front: true),
        back = ToStencilOpState(depthStencil, front: false),
    };

    private static VkStencilOpState ToStencilOpState(in RsDepthStencilStateDesc depthStencil, bool front) => new()
    {
        failOp = ToStencilOp(front ? depthStencil.FrontStencilFailOp : depthStencil.BackStencilFailOp),
        passOp = ToStencilOp(front ? depthStencil.FrontStencilPassOp : depthStencil.BackStencilPassOp),
        depthFailOp = ToStencilOp(front ? depthStencil.FrontStencilDepthFailOp : depthStencil.BackStencilDepthFailOp),
        compareOp = ToCompareOp(front ? depthStencil.FrontStencilFunc : depthStencil.BackStencilFunc),
        compareMask = depthStencil.StencilReadMask,
        writeMask = depthStencil.StencilWriteMask,
    };

    /// <summary>Translates render target 0's blend descriptor - the only one this renderer draws; see <c>RenderState.cs</c>'s own note on that.</summary>
    public static VkPipelineColorBlendAttachmentState ToColorBlendAttachment(in RsBlendStateDesc blend, int renderTarget = 0) => new()
    {
        blendEnable = blend.BlendEnable[renderTarget],
        srcColorBlendFactor = ToBlendFactor(blend.SrcBlend[renderTarget]),
        dstColorBlendFactor = ToBlendFactor(blend.DestBlend[renderTarget]),
        colorBlendOp = ToBlendOp(blend.BlendOp[renderTarget]),
        srcAlphaBlendFactor = ToBlendFactor(blend.SrcBlendAlpha[renderTarget]),
        dstAlphaBlendFactor = ToBlendFactor(blend.DestBlendAlpha[renderTarget]),
        alphaBlendOp = ToBlendOp(blend.BlendOpAlpha[renderTarget]),
        colorWriteMask = (VkColorComponentFlags)(byte)blend.RenderTargetWriteMask[renderTarget],
    };

    // RsCullMode, RsStencilOp, RsBlendOp, and RsColorWriteEnableBits all share Vulkan's own values or
    // bit positions, so these are plain casts; kept as named functions for the doc comments and so a
    // future divergence has one place to fix.

    private static VkCullModeFlags ToCullMode(RsCullMode mode) => mode switch
    {
        RsCullMode.None => VkCullModeFlags.None,
        RsCullMode.Back => VkCullModeFlags.Back,
        RsCullMode.Front => VkCullModeFlags.Front,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    private static VkStencilOp ToStencilOp(RsStencilOp op) => (VkStencilOp)(byte)op;

    private static VkBlendOp ToBlendOp(RsBlendOp op) => (VkBlendOp)(byte)op;

    /// <summary>
    /// Translates a comparison function. Bit 3 (<see cref="RsComparison.CloserFartherFlag"/>) is this
    /// schema's own reverse-Z annotation, not part of the operator Vulkan takes - <c>Closer</c> and
    /// <c>Greater</c> compare identically, they just read differently depending which convention the
    /// depth buffer was written with. Masking it off leaves the base 0-7 value, which is
    /// <see cref="VkCompareOp"/>'s own ordering (Never..Always) exactly.
    /// </summary>
    public static VkCompareOp ToCompareOp(RsComparison comparison) => (VkCompareOp)((byte)comparison & 0x7);

    /// <summary>
    /// Translates a blend factor. D3D11 (which this schema follows) and Vulkan both have every one of
    /// these factors, just in a different order and with color before alpha grouped differently, so
    /// this needs an actual switch rather than a cast.
    /// </summary>
    public static VkBlendFactor ToBlendFactor(RsBlendMode mode) => mode switch
    {
        RsBlendMode.Zero => VkBlendFactor.Zero,
        RsBlendMode.One => VkBlendFactor.One,
        RsBlendMode.SrcColor => VkBlendFactor.SrcColor,
        RsBlendMode.InvSrcColor => VkBlendFactor.OneMinusSrcColor,
        RsBlendMode.SrcAlpha => VkBlendFactor.SrcAlpha,
        RsBlendMode.InvSrcAlpha => VkBlendFactor.OneMinusSrcAlpha,
        RsBlendMode.DestAlpha => VkBlendFactor.DstAlpha,
        RsBlendMode.InvDestAlpha => VkBlendFactor.OneMinusDstAlpha,
        RsBlendMode.DestColor => VkBlendFactor.DstColor,
        RsBlendMode.InvDestColor => VkBlendFactor.OneMinusDstColor,
        RsBlendMode.SrcAlphaSat => VkBlendFactor.SrcAlphaSaturate,
        // D3D's BLEND_FACTOR is one constant used as either a color or alpha factor depending on
        // where it appears; Vulkan splits that into ConstantColor/ConstantAlpha. Neither this
        // renderer's GL backend nor any shader here uses it yet, so this is an approximation to
        // revisit against a real one once something does.
        RsBlendMode.BlendFactor => VkBlendFactor.ConstantColor,
        RsBlendMode.InvBlendFactor => VkBlendFactor.OneMinusConstantColor,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };
}
