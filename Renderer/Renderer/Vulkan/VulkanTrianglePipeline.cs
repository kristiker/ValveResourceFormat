using System.Numerics;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// Proof that a shader written as a GLSL string reaches the screen through this backend: glslang
/// compiles it to SPIR-V exactly as the plan called for. Vertex data comes from a real
/// <see cref="VulkanBuffer"/> (interleaved position/color, one binding); the texture comes from
/// <see cref="VulkanBindlessTextures"/>, looked up by an index carried in the push constants
/// alongside the MVP matrix, rather than a per-draw descriptor bind.
/// </summary>
public sealed unsafe class VulkanTrianglePipeline : IDisposable
{
    /// <summary>Byte size and layout of one <see cref="VulkanBuffer"/> vertex: position then color.</summary>
    public const int VertexStride = 5 * sizeof(float);

    /// <summary>The push-constant block every draw writes: the MVP matrix, then the bindless texture index.</summary>
    public readonly record struct PushConstants(Matrix4x4 Mvp, uint TextureIndex);

    private const string VertexSource = """
        #version 450

        layout(push_constant) uniform PushConstants
        {
            mat4 mvp;
            uint textureIndex;
        } pc;

        layout(location = 0) in vec2 inPosition;
        layout(location = 1) in vec3 inColor;

        layout(location = 0) out vec3 vtxColor;
        layout(location = 1) out vec2 vtxUV;

        void main()
        {
            gl_Position = pc.mvp * vec4(inPosition, 0.0, 1.0);
            vtxColor = inColor;
            vtxUV = inPosition + 0.5;
        }
        """;

    private const string FragmentSource = """
        #version 450
        #extension GL_EXT_nonuniform_qualifier : require

        layout(push_constant) uniform PushConstants
        {
            mat4 mvp;
            uint textureIndex;
        } pc;

        layout(set = 0, binding = 0) uniform sampler2D bindlessTextures[];

        layout(location = 0) in vec3 vtxColor;
        layout(location = 1) in vec2 vtxUV;
        layout(location = 0) out vec4 outColor;

        void main()
        {
            vec4 sampled = texture(bindlessTextures[nonuniformEXT(pc.textureIndex)], vtxUV);
            outColor = vec4(vtxColor, 1.0) * sampled;
        }
        """;

    private readonly VulkanDevice device;

    /// <summary>The pipeline layout: set 0 is the shared bindless textures set, plus the push-constant range every draw writes.</summary>
    public VkPipelineLayout Layout { get; }

    /// <summary>The graphics pipeline, built against dynamic rendering with no depth attachment.</summary>
    public VkPipeline Handle { get; }

    /// <summary>
    /// Compiles the shader strings above and builds the pipeline for the given swapchain color
    /// format. <paramref name="depthFormat"/> only has to match whatever else is drawn in the same
    /// rendering instance - this pipeline does not itself test or write depth - because Vulkan
    /// requires every pipeline used within one <c>vkCmdBeginRendering</c>/<c>vkCmdEndRendering</c>
    /// pair to agree on the attachment formats declared, not just the ones it actually reads or writes.
    /// </summary>
    public VulkanTrianglePipeline(VulkanDevice device, VkFormat colorFormat, VulkanBindlessTextures bindlessTextures, VkFormat depthFormat = VkFormat.Undefined)
    {
        this.device = device;

        var vertexSpirv = VulkanGlslang.Compile(VertexSource, VulkanGlslang.Stage.Vertex);
        var fragmentSpirv = VulkanGlslang.Compile(FragmentSource, VulkanGlslang.Stage.Fragment);

        var vertexModule = CreateShaderModule(vertexSpirv);
        var fragmentModule = CreateShaderModule(fragmentSpirv);

        try
        {
            var pushConstantRange = new VkPushConstantRange
            {
                stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment,
                offset = 0,
                size = (uint)sizeof(PushConstants),
            };

            var setLayout = bindlessTextures.Layout;

            var layoutCreateInfo = new VkPipelineLayoutCreateInfo
            {
                setLayoutCount = 1,
                pSetLayouts = &setLayout,
                pushConstantRangeCount = 1,
                pPushConstantRanges = &pushConstantRange,
            };

            device.Api.vkCreatePipelineLayout(&layoutCreateInfo, out var layout).CheckResult();
            Layout = layout;

            VkUtf8String entryPoint = "main"u8;

            var stages = stackalloc VkPipelineShaderStageCreateInfo[2]
            {
                new()
                {
                    stage = VkShaderStageFlags.Vertex,
                    module = vertexModule,
                    pName = entryPoint,
                },
                new()
                {
                    stage = VkShaderStageFlags.Fragment,
                    module = fragmentModule,
                    pName = entryPoint,
                },
            };

            var vertexBinding = new VkVertexInputBindingDescription
            {
                binding = 0,
                stride = VertexStride,
                inputRate = VkVertexInputRate.Vertex,
            };

            var vertexAttributes = stackalloc VkVertexInputAttributeDescription[2]
            {
                new() { location = 0, binding = 0, format = VkFormat.R32G32Sfloat, offset = 0 },
                new() { location = 1, binding = 0, format = VkFormat.R32G32B32Sfloat, offset = 2 * sizeof(float) },
            };

            var vertexInputState = new VkPipelineVertexInputStateCreateInfo
            {
                vertexBindingDescriptionCount = 1,
                pVertexBindingDescriptions = &vertexBinding,
                vertexAttributeDescriptionCount = 2,
                pVertexAttributeDescriptions = vertexAttributes,
            };

            var inputAssemblyState = new VkPipelineInputAssemblyStateCreateInfo
            {
                topology = VkPrimitiveTopology.TriangleList,
            };

            var viewportState = new VkPipelineViewportStateCreateInfo
            {
                viewportCount = 1,
                scissorCount = 1,
            };

            var rasterizationState = new VkPipelineRasterizationStateCreateInfo
            {
                polygonMode = VkPolygonMode.Fill,
                cullMode = VkCullModeFlags.None,
                frontFace = VkFrontFace.CounterClockwise,
                lineWidth = 1.0f,
            };

            var multisampleState = new VkPipelineMultisampleStateCreateInfo
            {
                rasterizationSamples = VkSampleCountFlags.Count1,
            };

            var colorBlendAttachment = new VkPipelineColorBlendAttachmentState
            {
                colorWriteMask = VkColorComponentFlags.R | VkColorComponentFlags.G | VkColorComponentFlags.B | VkColorComponentFlags.A,
            };

            var colorBlendState = new VkPipelineColorBlendStateCreateInfo
            {
                attachmentCount = 1,
                pAttachments = &colorBlendAttachment,
            };

            var dynamicStates = stackalloc VkDynamicState[2] { VkDynamicState.Viewport, VkDynamicState.Scissor };

            var dynamicState = new VkPipelineDynamicStateCreateInfo
            {
                dynamicStateCount = 2,
                pDynamicStates = dynamicStates,
            };

            var depthStencilState = new VkPipelineDepthStencilStateCreateInfo { sType = VkStructureType.PipelineDepthStencilStateCreateInfo };

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
                pVertexInputState = &vertexInputState,
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
            Handle = pipeline;
        }
        finally
        {
            device.Api.vkDestroyShaderModule(vertexModule);
            device.Api.vkDestroyShaderModule(fragmentModule);
        }
    }

    private VkShaderModule CreateShaderModule(byte[] spirv)
    {
        fixed (byte* pSpirv = spirv)
        {
            var createInfo = new VkShaderModuleCreateInfo
            {
                codeSize = (nuint)spirv.Length,
                pCode = (uint*)pSpirv,
            };

            device.Api.vkCreateShaderModule(&createInfo, out var module).CheckResult();
            return module;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        device.Api.vkDestroyPipeline(Handle);
        device.Api.vkDestroyPipelineLayout(Layout);
    }
}
