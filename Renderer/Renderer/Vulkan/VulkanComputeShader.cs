using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// Milestone-5 proof for compute: a shader string compiled with <see cref="VulkanGlslang"/> exactly
/// like the graphics ones, writing a procedural pattern into a <see cref="VulkanImage"/> through
/// <c>imageStore</c>, dispatched once per frame. The one storage-image binding is a small dedicated
/// descriptor set, not <see cref="VulkanBindlessTextures"/>'s array - there is exactly one resource
/// and it never changes, so bindless indexing would only add a lookup this does not need. The output
/// image is registered into the bindless array separately, once, purely so the existing textured
/// pipeline can sample it afterward without a shader of its own.
/// <para>
/// The correctness this is actually testing is the barrier between the two stages, not the shader:
/// GLSL's local size and this class's dispatch never disagree by construction (both come from here),
/// but nothing stops a caller recording the compute-write-to-fragment-read barrier with the wrong
/// stage or access mask, or forgetting the layout transition <c>imageStore</c> requires
/// (<see cref="VkImageLayout.General"/>) versus what sampling requires
/// (<see cref="VkImageLayout.ShaderReadOnlyOptimal"/>). That is on the caller to get right each frame,
/// which is exactly the risk the plan flagged compute/barriers as concentrating.
/// </para>
/// </summary>
public sealed unsafe class VulkanComputeShader : IDisposable
{
    /// <summary>Local workgroup size in X and Y, matching the shader's <c>local_size_x</c>/<c>local_size_y</c>.</summary>
    public const uint LocalSize = 8;

    /// <summary>The push-constant block every dispatch writes: elapsed time, in seconds.</summary>
    public readonly record struct PushConstants(float Time);

    private const string ComputeSource = """
        #version 450

        layout(local_size_x = 8, local_size_y = 8) in;

        layout(set = 0, binding = 0, rgba8) uniform writeonly image2D outputImage;

        layout(push_constant) uniform PushConstants
        {
            float time;
        } pc;

        void main()
        {
            ivec2 size = imageSize(outputImage);
            ivec2 pixel = ivec2(gl_GlobalInvocationID.xy);

            if (pixel.x >= size.x || pixel.y >= size.y)
            {
                return;
            }

            vec2 uv = (vec2(pixel) + 0.5) / vec2(size);

            float plasma = sin(uv.x * 12.0 + pc.time * 2.0)
                         + sin(uv.y * 12.0 - pc.time * 1.7)
                         + sin((uv.x + uv.y) * 10.0 + pc.time);

            vec3 color = 0.5 + 0.5 * vec3(
                sin(plasma * 3.14159 + 0.0),
                sin(plasma * 3.14159 + 2.094),
                sin(plasma * 3.14159 + 4.188));

            imageStore(outputImage, pixel, vec4(color, 1.0));
        }
        """;

    private readonly VulkanDevice device;
    private readonly VkDescriptorPool pool;

    /// <summary>The pipeline layout: one storage-image set plus the push-constant range every dispatch writes.</summary>
    public VkPipelineLayout Layout { get; }

    /// <summary>The compute pipeline.</summary>
    public VkPipeline Handle { get; }

    /// <summary>The descriptor set bound at dispatch time, already pointed at the constructor's output image.</summary>
    public VkDescriptorSet Set { get; }

    /// <summary>Compiles the shader, builds its dedicated descriptor set pointed at <paramref name="outputImage"/>, and builds the pipeline.</summary>
    public VulkanComputeShader(VulkanDevice device, VulkanImage outputImage)
    {
        this.device = device;

        var spirv = VulkanGlslang.Compile(ComputeSource, VulkanGlslang.Stage.Compute);
        var module = CreateShaderModule(spirv);

        try
        {
            var binding = new VkDescriptorSetLayoutBinding
            {
                binding = 0,
                descriptorType = VkDescriptorType.StorageImage,
                descriptorCount = 1,
                stageFlags = VkShaderStageFlags.Compute,
            };

            var setLayoutCreateInfo = new VkDescriptorSetLayoutCreateInfo { bindingCount = 1, pBindings = &binding };

            device.Api.vkCreateDescriptorSetLayout(&setLayoutCreateInfo, out var setLayout).CheckResult();

            var poolSize = new VkDescriptorPoolSize { type = VkDescriptorType.StorageImage, descriptorCount = 1 };
            var poolCreateInfo = new VkDescriptorPoolCreateInfo { maxSets = 1, poolSizeCount = 1, pPoolSizes = &poolSize };

            device.Api.vkCreateDescriptorPool(&poolCreateInfo, out pool).CheckResult();

            var allocateInfo = new VkDescriptorSetAllocateInfo { descriptorPool = pool, descriptorSetCount = 1, pSetLayouts = &setLayout };
            device.Api.vkAllocateDescriptorSets(allocateInfo, out var set).CheckResult();
            Set = set;

            var imageInfo = new VkDescriptorImageInfo { imageView = outputImage.View, imageLayout = VkImageLayout.General };
            var write = new VkWriteDescriptorSet
            {
                dstSet = set,
                dstBinding = 0,
                descriptorCount = 1,
                descriptorType = VkDescriptorType.StorageImage,
                pImageInfo = &imageInfo,
            };
            device.Api.vkUpdateDescriptorSets(write);

            var pushConstantRange = new VkPushConstantRange { stageFlags = VkShaderStageFlags.Compute, size = (uint)sizeof(PushConstants) };
            var layoutCreateInfo = new VkPipelineLayoutCreateInfo
            {
                setLayoutCount = 1,
                pSetLayouts = &setLayout,
                pushConstantRangeCount = 1,
                pPushConstantRanges = &pushConstantRange,
            };

            device.Api.vkCreatePipelineLayout(&layoutCreateInfo, out var layout).CheckResult();
            Layout = layout;

            // The layout does not need to outlive pipeline/set creation; only the pool and the set
            // allocated from it do.
            device.Api.vkDestroyDescriptorSetLayout(setLayout);

            VkUtf8String entryPoint = "main"u8;

            var pipelineCreateInfo = new VkComputePipelineCreateInfo
            {
                stage = new VkPipelineShaderStageCreateInfo { stage = VkShaderStageFlags.Compute, module = module, pName = entryPoint },
                layout = layout,
            };

            device.Api.vkCreateComputePipeline(pipelineCreateInfo, out var pipeline).CheckResult();
            Handle = pipeline;
        }
        finally
        {
            device.Api.vkDestroyShaderModule(module);
        }
    }

    /// <summary>
    /// Records the dispatch. The caller is responsible for the barrier transitioning
    /// <paramref name="outputWidth"/>/<paramref name="outputHeight"/>'s image to
    /// <see cref="VkImageLayout.General"/> before this and to
    /// <see cref="VkImageLayout.ShaderReadOnlyOptimal"/> (with a compute-write-to-fragment-read
    /// dependency) after - see the type-level remarks.
    /// </summary>
    public void Dispatch(VkCommandBuffer commandBuffer, float time, uint outputWidth, uint outputHeight)
    {
        device.Api.vkCmdBindPipeline(commandBuffer, VkPipelineBindPoint.Compute, Handle);

        var set = Set;
        device.Api.vkCmdBindDescriptorSets(commandBuffer, VkPipelineBindPoint.Compute, Layout, 0, 1, &set, 0, null);

        var pushConstants = new PushConstants(time);
        device.Api.vkCmdPushConstants(commandBuffer, Layout, VkShaderStageFlags.Compute, 0, (uint)sizeof(PushConstants), &pushConstants);

        var groupsX = (outputWidth + LocalSize - 1) / LocalSize;
        var groupsY = (outputHeight + LocalSize - 1) / LocalSize;
        device.Api.vkCmdDispatch(commandBuffer, groupsX, groupsY, 1);
    }

    private VkShaderModule CreateShaderModule(byte[] spirv)
    {
        fixed (byte* pSpirv = spirv)
        {
            var createInfo = new VkShaderModuleCreateInfo { codeSize = (nuint)spirv.Length, pCode = (uint*)pSpirv };
            device.Api.vkCreateShaderModule(&createInfo, out var module).CheckResult();
            return module;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        device.Api.vkDestroyPipeline(Handle);
        device.Api.vkDestroyPipelineLayout(Layout);
        device.Api.vkDestroyDescriptorPool(pool);
    }
}
