using System.Numerics;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace ValveResourceFormat.Renderer.Vulkan;

/// <summary>
/// Milestone-4 proof for <see cref="VulkanPipelineCache"/>: one compiled shader (position in, a flat
/// push-constant color out) drawn several times with different <see cref="RenderState"/> values, so
/// the cache has to actually differentiate rather than trivially always missing or always hitting.
/// Owns only the shader modules and pipeline layout - unlike <see cref="VulkanTrianglePipeline"/>, it
/// builds no <see cref="VkPipeline"/> itself, since which one to use depends on the render state a
/// draw asks for, decided at draw time by <see cref="VulkanPipelineCache"/>.
/// </summary>
public sealed unsafe class VulkanFlatColorShader : IDisposable
{
    /// <summary>Byte layout of one vertex: position only.</summary>
    public const int VertexStride = 3 * sizeof(float);

    /// <summary>The push-constant block every draw writes: the MVP matrix, then a flat RGBA color.</summary>
    public readonly record struct PushConstants(Matrix4x4 Mvp, Vector4 Color);

    private const string VertexSource = """
        #version 450

        layout(push_constant) uniform PushConstants
        {
            mat4 mvp;
            vec4 color;
        } pc;

        layout(location = 0) in vec3 inPosition;

        void main()
        {
            gl_Position = pc.mvp * vec4(inPosition, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 450

        layout(push_constant) uniform PushConstants
        {
            mat4 mvp;
            vec4 color;
        } pc;

        layout(location = 0) out vec4 outColor;

        void main()
        {
            outColor = pc.color;
        }
        """;

    private readonly VulkanDevice device;

    /// <summary>The pipeline layout every state variant of this shader shares.</summary>
    public VkPipelineLayout Layout { get; }

    /// <summary>The compiled vertex module, kept alive for <see cref="VulkanPipelineCache"/> to bake more pipelines from later.</summary>
    public VkShaderModule VertexModule { get; }

    /// <summary>The compiled fragment module, kept alive for the same reason.</summary>
    public VkShaderModule FragmentModule { get; }

    /// <summary>
    /// The single vertex binding this shader reads. <see cref="VkPipelineVertexInputStateCreateInfo"/>
    /// carries raw pointers, so unlike everything else here it is not precomputed and stored - a
    /// caller builds one from this and <see cref="VertexAttribute"/> in its own stack frame, right
    /// before passing it to <see cref="VulkanPipelineCache.GetOrCreate"/>, which is the only place
    /// that actually reads it (synchronously, inside <c>vkCreateGraphicsPipeline</c>).
    /// </summary>
    public static VkVertexInputBindingDescription VertexBinding { get; } = new() { binding = 0, stride = VertexStride, inputRate = VkVertexInputRate.Vertex };

    /// <summary>The single vertex attribute this shader reads; see <see cref="VertexBinding"/>.</summary>
    public static VkVertexInputAttributeDescription VertexAttribute { get; } = new() { location = 0, binding = 0, format = VkFormat.R32G32B32Sfloat, offset = 0 };

    /// <summary>Compiles the shader strings above and builds the layout.</summary>
    public VulkanFlatColorShader(VulkanDevice device)
    {
        this.device = device;

        var vertexSpirv = VulkanGlslang.Compile(VertexSource, VulkanGlslang.Stage.Vertex);
        var fragmentSpirv = VulkanGlslang.Compile(FragmentSource, VulkanGlslang.Stage.Fragment);

        VertexModule = CreateShaderModule(vertexSpirv);
        FragmentModule = CreateShaderModule(fragmentSpirv);

        var pushConstantRange = new VkPushConstantRange
        {
            stageFlags = VkShaderStageFlags.Vertex | VkShaderStageFlags.Fragment,
            size = (uint)sizeof(PushConstants),
        };

        var layoutCreateInfo = new VkPipelineLayoutCreateInfo
        {
            pushConstantRangeCount = 1,
            pPushConstantRanges = &pushConstantRange,
        };

        device.Api.vkCreatePipelineLayout(&layoutCreateInfo, out var layout).CheckResult();
        Layout = layout;
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
        device.Api.vkDestroyShaderModule(VertexModule);
        device.Api.vkDestroyShaderModule(FragmentModule);
        device.Api.vkDestroyPipelineLayout(Layout);
    }
}
