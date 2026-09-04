using System.Numerics;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// Proof that a shader written as a GLSL string reaches the screen through this backend: glslang
/// compiles it to SPIR-V exactly as the plan called for. Vertex data comes from a real
/// <see cref="VulkanBuffer"/> (interleaved position/color, one binding), one push constant carries
/// the MVP matrix; still no descriptor sets, which is the bindless-resource-binding step this only
/// has to prove the way in for.
/// </summary>
public sealed unsafe class VulkanTrianglePipeline : IDisposable
{
    /// <summary>Byte size and layout of one <see cref="VulkanBuffer"/> vertex: position then color.</summary>
    public const int VertexStride = 5 * sizeof(float);

    private const string VertexSource = """
        #version 450

        layout(push_constant) uniform PushConstants
        {
            mat4 mvp;
        } pc;

        layout(location = 0) in vec2 inPosition;
        layout(location = 1) in vec3 inColor;

        layout(location = 0) out vec3 vtxColor;

        void main()
        {
            gl_Position = pc.mvp * vec4(inPosition, 0.0, 1.0);
            vtxColor = inColor;
        }
        """;

    private const string FragmentSource = """
        #version 450

        layout(location = 0) in vec3 vtxColor;
        layout(location = 0) out vec4 outColor;

        void main()
        {
            outColor = vec4(vtxColor, 1.0);
        }
        """;

    private readonly VulkanDevice device;

    /// <summary>The pipeline layout, holding the one push-constant range every draw writes.</summary>
    public VkPipelineLayout Layout { get; }

    /// <summary>The graphics pipeline, built against dynamic rendering with no depth attachment.</summary>
    public VkPipeline Handle { get; }

    /// <summary>Compiles the shader strings above and builds the pipeline for the given swapchain color format.</summary>
    public VulkanTrianglePipeline(VulkanDevice device, VkFormat colorFormat)
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
                stageFlags = VkShaderStageFlags.Vertex,
                offset = 0,
                size = (uint)sizeof(Matrix4x4),
            };

            var layoutCreateInfo = new VkPipelineLayoutCreateInfo
            {
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

            var renderingCreateInfo = new VkPipelineRenderingCreateInfo
            {
                colorAttachmentCount = 1,
                pColorAttachmentFormats = &colorFormat,
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
