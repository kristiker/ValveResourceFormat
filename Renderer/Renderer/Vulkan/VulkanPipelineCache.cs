using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// Bakes a <see cref="VkPipeline"/> from a shader and a <see cref="RenderState"/> the first time that
/// combination is drawn with, and hands back the cached one on every draw after - a caller just
/// describes what it wants to draw, the way <c>RenderStateTracker.Apply</c> lets OpenGL draw code set
/// state without thinking about whether that is a state change or a no-op. <see cref="RenderState"/>
/// and its nested descriptors are already <see langword="record struct"/>s with field-wise equality,
/// so they work as a dictionary key with no extra plumbing.
/// <para>
/// Two things <see cref="VulkanRenderState"/> does not translate, both because OpenGL's own
/// <c>RenderStateTracker</c> already treats them the same way: depth bias
/// (<see cref="RsRasterizerStateDesc.DepthBias"/> and friends) and the stencil reference value are
/// both dynamic state, not baked into a pipeline - the reference already has to be, since this
/// schema documents it as "not part of the descriptor", and depth bias follows for the same reason
/// GL sets it outside <c>Apply</c>'s diffed block. Vulkan agrees: <c>VK_DYNAMIC_STATE_DEPTH_BIAS</c>
/// and <c>VK_DYNAMIC_STATE_STENCIL_REFERENCE</c> exist for exactly this. Neither is wired up yet since
/// nothing drawn through this cache so far uses either; the dynamic-state list below is where they
/// would join <c>Viewport</c>/<c>Scissor</c>.
/// </para>
/// <para>
/// Vertex format is not part of the cache key yet - every pipeline built through this cache so far
/// shares one vertex layout. A real integration needs it added once more than one does.
/// </para>
/// </summary>
public sealed unsafe class VulkanPipelineCache : IDisposable
{
    private readonly VulkanDevice device;
    private readonly Dictionary<Key, VkPipeline> pipelines = [];

    /// <summary>Number of draws so far that found an existing pipeline rather than baking one.</summary>
    public int CacheHits { get; private set; }

    /// <summary>Number of pipelines actually baked so far.</summary>
    public int CacheMisses { get; private set; }

    private readonly record struct Key(VkShaderModule Vertex, VkShaderModule Fragment, VkPipelineLayout Layout,
        RenderState RenderState, VkPrimitiveTopology Topology, VkFormat ColorFormat, VkFormat DepthFormat);

    /// <summary>Creates an empty cache. One per device is enough; nothing here is per-frame state.</summary>
    public VulkanPipelineCache(VulkanDevice device)
    {
        this.device = device;
    }

    /// <summary>
    /// Returns the pipeline for this exact (shader, layout, render state, topology, attachment format)
    /// combination, baking one on first request and reusing it after.
    /// </summary>
    public VkPipeline GetOrCreate(VkShaderModule vertex, VkShaderModule fragment, VkPipelineLayout layout,
        in VkPipelineVertexInputStateCreateInfo vertexInput, in RenderState renderState, VkPrimitiveTopology topology,
        VkFormat colorFormat, VkFormat depthFormat = VkFormat.Undefined)
    {
        var key = new Key(vertex, fragment, layout, renderState, topology, colorFormat, depthFormat);

        if (pipelines.TryGetValue(key, out var existing))
        {
            CacheHits++;
            return existing;
        }

        CacheMisses++;

        var pipeline = Bake(vertex, fragment, layout, in vertexInput, in renderState, topology, colorFormat, depthFormat);
        pipelines.Add(key, pipeline);
        return pipeline;
    }

    private VkPipeline Bake(VkShaderModule vertex, VkShaderModule fragment, VkPipelineLayout layout,
        in VkPipelineVertexInputStateCreateInfo vertexInput, in RenderState renderState, VkPrimitiveTopology topology,
        VkFormat colorFormat, VkFormat depthFormat)
    {
        VkUtf8String entryPoint = "main"u8;

        var stages = stackalloc VkPipelineShaderStageCreateInfo[2]
        {
            new() { stage = VkShaderStageFlags.Vertex, module = vertex, pName = entryPoint },
            new() { stage = VkShaderStageFlags.Fragment, module = fragment, pName = entryPoint },
        };

        var inputAssemblyState = new VkPipelineInputAssemblyStateCreateInfo { topology = topology };

        var viewportState = new VkPipelineViewportStateCreateInfo { viewportCount = 1, scissorCount = 1 };

        var rasterizationState = VulkanRenderState.ToRasterizationState(renderState.Rasterizer);

        var multisampleState = new VkPipelineMultisampleStateCreateInfo { rasterizationSamples = VkSampleCountFlags.Count1 };

        var depthStencilState = VulkanRenderState.ToDepthStencilState(renderState.DepthStencil);

        var colorBlendAttachment = VulkanRenderState.ToColorBlendAttachment(renderState.Blend);

        var colorBlendState = new VkPipelineColorBlendStateCreateInfo { attachmentCount = 1, pAttachments = &colorBlendAttachment };

        var dynamicStates = stackalloc VkDynamicState[2] { VkDynamicState.Viewport, VkDynamicState.Scissor };

        var dynamicState = new VkPipelineDynamicStateCreateInfo { dynamicStateCount = 2, pDynamicStates = dynamicStates };

        var vertexInputCopy = vertexInput;

        var renderingCreateInfo = new VkPipelineRenderingCreateInfo
        {
            colorAttachmentCount = 1,
            pColorAttachmentFormats = &colorFormat,
            depthAttachmentFormat = depthFormat,
        };

        var pipelineCreateInfo = new VkGraphicsPipelineCreateInfo
        {
            pNext = &renderingCreateInfo,
            stageCount = 2,
            pStages = stages,
            pVertexInputState = &vertexInputCopy,
            pInputAssemblyState = &inputAssemblyState,
            pViewportState = &viewportState,
            pRasterizationState = &rasterizationState,
            pMultisampleState = &multisampleState,
            pDepthStencilState = &depthStencilState,
            pColorBlendState = &colorBlendState,
            pDynamicState = &dynamicState,
            layout = layout,
        };

        device.Api.vkCreateGraphicsPipeline(pipelineCreateInfo, out var pipeline).CheckResult();
        return pipeline;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var pipeline in pipelines.Values)
        {
            device.Api.vkDestroyPipeline(pipeline);
        }
    }
}
