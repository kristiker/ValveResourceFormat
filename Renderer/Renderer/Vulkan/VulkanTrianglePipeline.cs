using System.Numerics;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// Milestone-2 proof that a shader written as a GLSL string reaches the screen through this
/// backend: glslang compiles it to SPIR-V exactly as the plan called for, no descriptor sets or
/// vertex buffers yet (the triangle's positions and colors are baked into the vertex shader), one
/// push constant carrying the MVP matrix. Real materials get real vertex/resource binding in a
/// later milestone; this only has to prove the shader-string-to-draw-call path.
/// </summary>
public sealed unsafe class VulkanTrianglePipeline : IDisposable
{
    private const string VertexSource = """
        #version 450

        layout(push_constant) uniform PushConstants
        {
            mat4 mvp;
        } pc;

        vec2 positions[3] = vec2[](
            vec2(0.0, -0.5),
            vec2(0.5, 0.5),
            vec2(-0.5, 0.5)
        );

        vec3 colors[3] = vec3[](
            vec3(0.9, 0.2, 0.2),
            vec3(0.2, 0.9, 0.3),
            vec3(0.3, 0.4, 0.95)
        );

        layout(location = 0) out vec3 vtxColor;

        void main()
        {
            gl_Position = pc.mvp * vec4(positions[gl_VertexIndex], 0.0, 1.0);
            vtxColor = colors[gl_VertexIndex];
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

            var vertexInputState = new VkPipelineVertexInputStateCreateInfo();

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
