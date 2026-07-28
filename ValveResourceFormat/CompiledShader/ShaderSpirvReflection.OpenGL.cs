using System.Linq;
using ValveResourceFormat.ResourceTypes;
using Vortice.SPIRV;
using Vortice.SpirvCross;
using SpirvResourceType = Vortice.SpirvCross.ResourceType;

namespace ValveResourceFormat.CompiledShader;

public static partial class ShaderSpirvReflection
{
    /// <summary>
    /// Sampler state recovered from the shader's static sampler configuration, for building GL sampler objects.
    /// Null members were not specified by the shader. Dynamic address modes come from material data at runtime.
    /// </summary>
    public sealed record VcsGlSamplerState(
        RsFilter? Filter,
        RsTextureAddressMode? AddressU,
        RsTextureAddressMode? AddressV,
        RsTextureAddressMode? AddressW,
        bool AddressUDynamic,
        bool AddressVDynamic,
        int? MaxAniso,
        RsComparison? ComparisonFunc,
        int? BorderColor,
        int? MipBias,
        int? MinLod,
        int? MaxLod,
        bool? AllowGlobalMipBiasOverride);

    /// <summary>
    /// A combined image sampler uniform synthesized for desktop OpenGL from a Vulkan separate texture/sampler pair.
    /// </summary>
    /// <param name="UniformName">The emitted GLSL uniform name (usually the texture name).</param>
    /// <param name="TextureName">The Valve texture variable name (<c>g_tColor</c> style).</param>
    /// <param name="SamplerName">The sampler name (<c>g_sAniso</c> style), or null when only the internal dummy sampler is used.</param>
    /// <param name="Sampler">The recovered sampler state, or null when unknown.</param>
    public sealed record VcsGlCombinedSampler(string UniformName, string TextureName, string? SamplerName, VcsGlSamplerState? Sampler);

    /// <summary>
    /// A constant buffer or storage buffer in the emitted OpenGL GLSL.
    /// </summary>
    /// <remarks>
    /// Constant buffers are emitted as plain struct uniforms (Valve's HLSL packing, e.g. a <c>float2</c> at
    /// <c>c8.z</c>, cannot be expressed in std140 even with enhanced layouts), so their members are addressed
    /// as <c>&lt;EmittedName&gt;.&lt;member&gt;</c> GL uniforms. Storage buffers stay real std430 blocks and
    /// <c>EmittedName</c> is the block name for <c>glGetProgramResourceIndex</c>.
    /// </remarks>
    /// <param name="EmittedName">The per-stage suffixed name in the emitted GLSL.</param>
    /// <param name="OriginalName">The Valve buffer name (<c>PerViewConstantBuffer_t</c>, <c>_Globals_</c>, or <c>undetermined</c>).</param>
    public sealed record VcsGlBufferBlock(string EmittedName, string OriginalName);

    /// <summary>
    /// Reflection results from <see cref="ReflectSpirvOpenGl"/> that the renderer needs to bind resources by name.
    /// </summary>
    public sealed class VcsGlReflectionInfo
    {
        /// <summary>Gets the combined image sampler uniforms present in the emitted source.</summary>
        public List<VcsGlCombinedSampler> CombinedSamplers { get; } = [];

        /// <summary>Gets the uniform buffer blocks present in the emitted source.</summary>
        public List<VcsGlBufferBlock> UniformBlocks { get; } = [];

        /// <summary>Gets the shader storage buffer blocks present in the emitted source.</summary>
        public List<VcsGlBufferBlock> StorageBlocks { get; } = [];

        /// <summary>Gets whether the static combo declares bindless resources, which the OpenGL path does not support.</summary>
        public bool HasBindlessResources { get; internal set; }
    }

    /// <summary>
    /// Reflects and decompiles SPIR-V bytecode into GLSL that desktop OpenGL 4.6 can compile directly:
    /// combined image samplers, real uniform buffer blocks, no Vulkan descriptor set/binding layout qualifiers
    /// (all resources are expected to be bound by name at runtime), and the baked Vulkan Y-flip countered on
    /// vertex stages. Buffer block names are suffixed per stage so same-named blocks with differently pruned
    /// member sets do not collide at link time.
    /// </summary>
    /// <param name="vulkanSource">The Vulkan shader source containing SPIR-V bytecode.</param>
    /// <param name="code">The emitted GLSL on success, or an error description on failure.</param>
    /// <param name="info">Reflection results the renderer needs to bind resources by name.</param>
    /// <returns>True if a GL-compilable source was produced, false otherwise.</returns>
    public static bool ReflectSpirvOpenGl(VfxShaderFileVulkan vulkanSource, out string code, out VcsGlReflectionInfo info)
    {
        var reflectionInfo = new VcsGlReflectionInfo();
        info = reflectionInfo;

        var staticComboData = vulkanSource.ParentCombo;
        var program = staticComboData?.ParentProgramData;

        if (program is null || staticComboData is null)
        {
            code = "SPIR-V source has no parent program data.";
            return false;
        }

        var programType = program.VcsProgramType;

        if (programType is not (VcsProgramType.VertexShader or VcsProgramType.PixelShader or VcsProgramType.ComputeShader))
        {
            code = $"Program type {programType} is not supported by the OpenGL path.";
            return false;
        }

        if (staticComboData.Attributes.FirstOrDefault(a => a.Name0 == "BindlessResources")?.ConstValue is true)
        {
            reflectionInfo.HasBindlessResources = true;
            code = "Bindless resources are not supported by the OpenGL path.";
            return false;
        }

        var stageSuffix = programType switch
        {
            VcsProgramType.VertexShader => "_vs",
            VcsProgramType.PixelShader => "_ps",
            _ => "_cs",
        };

        var result = SpirvCrossApi.spvc_context_create(out var context);

        if (result != Result.Success)
        {
            code = "Failed to create SPIR-V context";
            return false;
        }

        try
        {
            result = SpirvCrossApi.spvc_context_parse_spirv(context, vulkanSource.Bytecode, out var parsedIr);

            if (result != Result.Success)
            {
                return GlError(out code, context);
            }

            result = SpirvCrossApi.spvc_context_create_compiler(context, Backend.GLSL, parsedIr, CaptureMode.TakeOwnership, out var compiler);

            if (result != Result.Success)
            {
                return GlError(out code, context);
            }

            result = SpirvCrossApi.spvc_compiler_create_compiler_options(compiler, out var options);

            if (result != Result.Success)
            {
                return GlError(out code, context);
            }

            SpirvCrossApi.spvc_compiler_options_set_uint(options, CompilerOption.GLSLVersion, 460);
            SpirvCrossApi.spvc_compiler_options_set_bool(options, CompilerOption.GLSLES, SpirvCrossApi.SPVC_FALSE);
            SpirvCrossApi.spvc_compiler_options_set_bool(options, CompilerOption.GLSLVulkanSemantics, SpirvCrossApi.SPVC_FALSE);
            // Valve's HLSL cbuffer packing is not std140-expressible (e.g. float2 at c8.z), so constant
            // buffers become plain struct uniforms whose members are set individually by name at runtime.
            SpirvCrossApi.spvc_compiler_options_set_bool(options, CompilerOption.GLSLEmitUniformBufferAsPlainUniforms, SpirvCrossApi.SPVC_TRUE);

            if (programType is VcsProgramType.VertexShader)
            {
                // Valve's SPIR-V bakes the Vulkan Y-flip into gl_Position; a second negation restores GL clip space.
                SpirvCrossApi.spvc_compiler_options_set_bool(options, CompilerOption.FlipVertexY, SpirvCrossApi.SPVC_TRUE);
            }

            result = SpirvCrossApi.spvc_compiler_install_compiler_options(compiler, options);

            if (result != Result.Success)
            {
                return GlError(out code, context);
            }

            result = SpirvCrossApi.spvc_compiler_create_shader_resources(compiler, out var resources);

            if (result != Result.Success)
            {
                return GlError(out code, context);
            }

            // Same preamble as RenameResource: locate this shader file's dynamic combo and write sequence.
            var dynamicComboIndex = Array.FindIndex(staticComboData.DynamicCombos, r => r.ShaderFileId == vulkanSource.ShaderFileId);
            var dynamicComboId = dynamicComboIndex >= 0 ? staticComboData.DynamicCombos[dynamicComboIndex].DynamicComboId : 0;
            var writeSequence = staticComboData.DynamicComboVariables[Math.Max(staticComboData.GetDynamicComboIndex(dynamicComboId), 0)];
            var bindingConfig = GetBindingConfiguration(program.VcsVersion, programType);

            var isVertexShader = programType is VcsProgramType.VertexShader;
            var vertexLayout = isVertexShader && vulkanSource.AttribMap is not null ? vulkanSource : null;
            Material.InputSignatureElement[] vsInputSignature = [];

            if (vertexLayout is not null && dynamicComboIndex >= 0 && dynamicComboIndex < staticComboData.VShaderInputs.Length)
            {
                vsInputSignature = program.VSInputSignatures[staticComboData.VShaderInputs[dynamicComboIndex]].SymbolsDefinition;
            }

            var globalsBufferBinding = programType is VcsProgramType.VertexShader or VcsProgramType.GeometryShader
                ? (uint)bindingConfig.VsGsBufferBindingOffset
                : 0u;
            var globalsBufferSet = bindingConfig.VsGsBufferBindingOffset == 0 && programType is VcsProgramType.PixelShader
                ? 1u
                : 0u;

            var textureNames = new Dictionary<uint, string>();
            var samplerStates = new Dictionary<uint, VcsGlSamplerState>();
            var samplerNames = new Dictionary<uint, string>();

            void Rename(SpirvResourceType resourceType)
            {
                var reflectedResources = SpirvCrossApi.spvc_resources_get_resource_list_for_type(resources, resourceType);

                foreach (var resource in reflectedResources)
                {
                    var binding = SpirvCrossApi.spvc_compiler_get_decoration(compiler, resource.id, SpvDecoration.Binding);
                    var set = SpirvCrossApi.spvc_compiler_get_decoration(compiler, resource.id, SpvDecoration.DescriptorSet);
                    var location = SpirvCrossApi.spvc_compiler_get_decoration(compiler, resource.id, SpvDecoration.Location);

                    var imageVfxType = resourceType is SpirvResourceType.SeparateImage
                        ? GetImageVfxType(compiler, resource.base_type_id)
                        : VfxVariableType.Void;

                    var name = resourceType switch
                    {
                        SpirvResourceType.SeparateImage => GetNameForTexture(program, writeSequence, binding, set, imageVfxType, bindingConfig),
                        SpirvResourceType.SeparateSamplers => GetNameForSampler(program, writeSequence, binding, set, bindingConfig),
                        SpirvResourceType.StorageBuffer => GetNameForStorageBuffer(program, writeSequence, binding, set, bindingConfig),
                        SpirvResourceType.UniformBuffer => GetNameForUniformBuffer(program, writeSequence, binding, set)
                            ?? (binding == globalsBufferBinding && set == globalsBufferSet ? "_Globals_" : "undetermined"),
                        SpirvResourceType.StageInput when vertexLayout is not null
                            => GetVertexInputName(vertexLayout, vsInputSignature, location),
                        SpirvResourceType.StageInput => GetStageAttributeName(location, input: true),
                        SpirvResourceType.StageOutput => GetStageAttributeName(location, input: false),
                        _ => string.Empty
                    };

                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    switch (resourceType)
                    {
                        case SpirvResourceType.SeparateImage:
                            textureNames[resource.id] = name;
                            SpirvCrossApi.spvc_compiler_set_name(compiler, resource.id, name);
                            SpirvCrossApi.spvc_compiler_unset_decoration(compiler, resource.id, SpvDecoration.Binding);
                            SpirvCrossApi.spvc_compiler_unset_decoration(compiler, resource.id, SpvDecoration.DescriptorSet);
                            break;

                        case SpirvResourceType.SeparateSamplers:
                            samplerNames[resource.id] = name;
                            samplerStates[resource.id] = ResolveGlSamplerState(program, writeSequence, binding, set, bindingConfig);
                            SpirvCrossApi.spvc_compiler_set_name(compiler, resource.id, name);
                            SpirvCrossApi.spvc_compiler_unset_decoration(compiler, resource.id, SpvDecoration.Binding);
                            SpirvCrossApi.spvc_compiler_unset_decoration(compiler, resource.id, SpvDecoration.DescriptorSet);
                            break;

                        case SpirvResourceType.StorageBuffer:
                        case SpirvResourceType.UniformBuffer:
                        {
                            // Both stages may declare the same buffer with differently pruned member sets;
                            // a per-stage suffix keeps the two from colliding when the program links.
                            // Consecutive underscores are reserved in GLSL and SPIRV-Cross would collapse
                            // them anyway ("_Globals_" + "_vs"), so collapse them up front to keep the
                            // recorded name identical to the emitted one.
                            var emittedName = CollapseUnderscores(name + stageSuffix);
                            SpirvCrossApi.spvc_compiler_set_name(compiler, resource.id, emittedName);
                            SpirvCrossApi.spvc_compiler_unset_decoration(compiler, resource.id, SpvDecoration.Binding);
                            SpirvCrossApi.spvc_compiler_unset_decoration(compiler, resource.id, SpvDecoration.DescriptorSet);

                            if (resourceType is SpirvResourceType.UniformBuffer)
                            {
                                reflectionInfo.UniformBlocks.Add(new VcsGlBufferBlock(emittedName, name));
                                RenameGlBufferMembers(compiler, resource, program, writeSequence, name);
                            }
                            else
                            {
                                // Storage buffers stay real std430 blocks; the block name comes from the type.
                                SpirvCrossApi.spvc_compiler_set_name(compiler, resource.base_type_id, emittedName);
                                reflectionInfo.StorageBlocks.Add(new VcsGlBufferBlock(emittedName, name));
                            }

                            break;
                        }

                        case SpirvResourceType.StageInput:
                        case SpirvResourceType.StageOutput:
                            SpirvCrossApi.spvc_compiler_set_name(compiler, resource.id, name);
                            break;
                    }
                }
            }

            Rename(SpirvResourceType.SeparateImage);
            Rename(SpirvResourceType.SeparateSamplers);
            Rename(SpirvResourceType.StorageBuffer);
            Rename(SpirvResourceType.UniformBuffer);
            Rename(SpirvResourceType.StageInput);
            Rename(SpirvResourceType.StageOutput);

            // Desktop GL has no separate texture/sampler objects; synthesize combined image samplers.
            uint dummySamplerId;
            unsafe
            {
                uint dummy = 0;
                result = SpirvCrossApi.spvc_compiler_build_dummy_sampler_for_combined_images(compiler, &dummy);
                dummySamplerId = dummy;
            }

            if (result != Result.Success)
            {
                return GlError(out code, context);
            }

            result = SpirvCrossApi.spvc_compiler_build_combined_image_samplers(compiler);

            if (result != Result.Success)
            {
                return GlError(out code, context);
            }

            var combinedSamplers = SpirvCrossApi.spvc_compiler_get_combined_image_samplers(compiler);

            // A texture paired with exactly one sampler keeps its plain name so the renderer's
            // bind-by-texture-name machinery works untouched; only multi-sampler textures get a suffix.
            var imageUseCounts = new Dictionary<uint, int>();
            foreach (var combined in combinedSamplers)
            {
                imageUseCounts[combined.image_id] = imageUseCounts.GetValueOrDefault(combined.image_id) + 1;
            }

            foreach (var combined in combinedSamplers)
            {
                var textureName = textureNames.GetValueOrDefault(combined.image_id, $"texture_{combined.image_id}");
                var isDummy = combined.sampler_id == dummySamplerId || !samplerNames.ContainsKey(combined.sampler_id);
                var samplerName = isDummy ? null : samplerNames[combined.sampler_id];
                var samplerState = isDummy ? null : samplerStates.GetValueOrDefault(combined.sampler_id);

                var uniformName = imageUseCounts[combined.image_id] > 1 && samplerName is not null
                    ? CollapseUnderscores($"{textureName}_{samplerName}")
                    : textureName;

                SpirvCrossApi.spvc_compiler_set_name(compiler, combined.combined_id, uniformName);
                SpirvCrossApi.spvc_compiler_unset_decoration(compiler, combined.combined_id, SpvDecoration.Binding);
                SpirvCrossApi.spvc_compiler_unset_decoration(compiler, combined.combined_id, SpvDecoration.DescriptorSet);

                reflectionInfo.CombinedSamplers.Add(new VcsGlCombinedSampler(uniformName, textureName, samplerName, samplerState));
            }

            result = SpirvCrossApi.spvc_compiler_compile(compiler, out var compiledCode);

            if (result != Result.Success)
            {
                return GlError(out code, context);
            }

            // No textual post-processing: saturate() is not GLSL, and the output must stay compilable as-is.
            code = compiledCode ?? string.Empty;
        }
        finally
        {
            SpirvCrossApi.spvc_context_release_allocations(context);
            SpirvCrossApi.spvc_context_destroy(context);
        }

        return result == Result.Success;
    }

    private static string CollapseUnderscores(string name)
    {
        while (name.Contains("__", StringComparison.Ordinal))
        {
            name = name.Replace("__", "_", StringComparison.Ordinal);
        }

        return name;
    }

    private static bool GlError(out string code, spvc_context context)
    {
        var lastError = SpirvCrossApi.spvc_context_get_last_error_string(context);
        code = lastError ?? string.Empty;
        return false;
    }

    private static void RenameGlBufferMembers(spvc_compiler compiler, spvc_reflected_resource resource, VfxProgramData program,
        VfxVariableIndexArray writeSequence, string bufferName)
    {
        var bufferRanges = SpirvCrossApi.spvc_compiler_get_active_buffer_ranges(compiler, resource.id);

        foreach (var bufferRange in bufferRanges)
        {
            var memberName = bufferName == "_Globals_"
                ? GetGlobalBufferMemberName(program, writeSequence, (int)bufferRange.offset / 4)
                : GetBufferMemberName(program, bufferName, offset: (int)bufferRange.offset / 4);

            if (string.IsNullOrEmpty(memberName))
            {
                // VCS 71 dropped the external constant buffer descriptions; fall back to layouts
                // recovered from DXBC reflection of the same shaders.
                memberName = KnownBufferLayouts.GetMemberName(bufferName, (int)bufferRange.offset);
            }

            if (string.IsNullOrEmpty(memberName))
            {
                continue;
            }

            unsafe
            {
                fixed (byte* memberNameBytes = memberName.GetUtf8Span())
                {
                    SpirvCrossApi.spvc_compiler_set_member_name(compiler, resource.base_type_id, bufferRange.index, memberNameBytes);
                }
            }
        }
    }

    private static VcsGlSamplerState ResolveGlSamplerState(VfxProgramData program, VfxVariableIndexArray writeSequence,
        uint samplerBinding, uint set, BindingPointConfiguration config)
    {
        var definition = new SamplerDefinition();

        foreach (var field in writeSequence.RenderState)
        {
            if (field.LayoutSet != set)
            {
                continue;
            }

            var param = program.VariableDescriptions[field.VariableIndex];

            if (param.RegisterType is not VfxRegisterType.SamplerState || field.Dest != samplerBinding - config.SamplerStartingPoint)
            {
                continue;
            }

            if (param.HasDynamicExpression)
            {
                definition.SetDynamic(param.Name);
            }
            else
            {
                definition.SetStatic(param.Name, param.IntDefs[0]);
            }
        }

        return new VcsGlSamplerState(
            Filter: definition.Filter,
            AddressU: definition.AddressU.Value,
            AddressV: definition.AddressV.Value,
            AddressW: definition.AddressW.Value,
            AddressUDynamic: definition.AddressU.IsDynamic,
            AddressVDynamic: definition.AddressV.IsDynamic,
            MaxAniso: definition.MaxAniso,
            ComparisonFunc: definition.ComparisonFunc,
            BorderColor: definition.BorderColor,
            MipBias: definition.MipBias,
            MinLod: definition.MinLod,
            MaxLod: definition.MaxLod,
            AllowGlobalMipBiasOverride: definition.AllowGlobalMipBiasOverride);
    }
}
