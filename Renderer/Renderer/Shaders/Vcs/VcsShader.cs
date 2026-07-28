using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.CompiledShader;

namespace ValveResourceFormat.Renderer.Shaders.Vcs;

/// <summary>
/// A shader program built from Valve's compiled SPIR-V bytecode, cross-compiled to desktop GL GLSL.
/// One instance wraps one linked program for one (static combo, dynamic combo) pair.
/// </summary>
/// <remarks>
/// Constant buffers are plain struct uniforms in the emitted GLSL (<c>_Globals_ps.g_flOpacityScale</c>).
/// Material parameter names are aliased onto those prefixed uniforms so the existing material binding
/// machinery works unchanged, and the per-view buffers are filled member-by-member from
/// <see cref="VcsPerViewState"/> when the shader is bound.
/// </remarks>
public class VcsShader : Shader
{
    /// <summary>Gets the per-dynamic-combo render state from the pixel shader, if present.</summary>
    public required VfxRenderStateInfoPixelShader? PsRenderState { get; init; }

    /// <summary>Gets the reflection info of each linked stage (vertex first).</summary>
    public required ShaderSpirvReflection.VcsGlReflectionInfo[] StageReflections { get; init; }

    /// <summary>Gets the material parameter defaults keyed by member name, from the shader's variable descriptions.</summary>
    public required Dictionary<string, VfxVariableDescription> VariableDescriptions { get; init; }

    /// <summary>Gets the pipeline that owns this shader, providing the per-view state and sampler cache.</summary>
    public required VcsShaderPipeline Pipeline { get; init; }

    private readonly Dictionary<string, List<string>> materialParamAliases = [];
    private readonly Dictionary<string, List<string>> textureAliases = [];
    private readonly Dictionary<string, int> samplerObjects = [];
    private readonly List<(int Location, ActiveUniformType Type, string Member)> perViewMembers = [];
    private readonly List<int> boundSamplerUnits = [];
    private int lastPerViewVersion = -1;

    /// <summary>Gets the attribute location of the <c>nInstanceIdx</c> vertex input, or -1 when absent.</summary>
    public int InstanceIdxAttributeLocation { get; private set; } = -1;

    /// <summary>Initializes a new instance of the <see cref="VcsShader"/> class.</summary>
    public VcsShader(string name, RendererContext rendererContext) : base(name, rendererContext)
    {
    }

    /// <inheritdoc/>
    protected override void OnProgramLoaded()
    {
        InstanceIdxAttributeLocation = Attributes.GetValueOrDefault("nInstanceIdx", -1);

        // First pass: split "<buffer>.<member>" uniforms into per-view members and material param aliases.
        foreach (var (name, _, type, _) in GetAllUniformNames())
        {
            var dot = name.IndexOf('.', StringComparison.Ordinal);

            if (dot < 0)
            {
                continue;
            }

            var prefix = name[..dot];
            var member = name[(dot + 1)..];

            // Matrices can come out as an anonymous row array: "g_matX._m0[0]".
            var memberDot = member.IndexOf('.', StringComparison.Ordinal);
            if (memberDot >= 0)
            {
                member = member[..memberDot];
            }

            var originalBuffer = FindOriginalBufferName(prefix);

            switch (originalBuffer)
            {
                case "PerViewConstantBuffer_t":
                case "PerViewConstantBufferCsgo_t":
                    perViewMembers.Add((GetUniformLocation(name), type, member));
                    break;

                case "_Globals_":
                    if (!materialParamAliases.TryGetValue(member, out var aliasList))
                    {
                        aliasList = [];
                        materialParamAliases[member] = aliasList;
                    }

                    aliasList.Add(name);
                    AddMaterialDefault(member, type);
                    break;

                default:
                    // Unknown external buffers stay zero-filled (plain uniforms default to zero).
                    break;
            }
        }

        // Note: VfxVariableDescription.SrgbRead is only meaningful for textures; on scalar and vector
        // parameters the underlying bit is repurposed, so no vector params are marked for conversion.

        // Combined sampler uniforms: create GL sampler objects and alias multi-sampler textures.
        foreach (var reflection in StageReflections)
        {
            foreach (var combined in reflection.CombinedSamplers)
            {
                if (GetUniformLocation(combined.UniformName) < 0)
                {
                    continue;
                }

                if (combined.Sampler is { } samplerState)
                {
                    if (samplerState.Filter is CompiledShader.RsFilter.UserConfig || samplerState.AddressUDynamic || samplerState.AddressVDynamic)
                    {
                        SamplerUserConfigUniforms.Add(combined.UniformName);
                    }

                    samplerObjects.TryAdd(combined.UniformName, Pipeline.SamplerCache.GetSampler(samplerState));
                }

                if (combined.UniformName != combined.TextureName)
                {
                    if (!textureAliases.TryGetValue(combined.TextureName, out var aliasList))
                    {
                        aliasList = [];
                        textureAliases[combined.TextureName] = aliasList;
                    }

                    aliasList.Add(combined.UniformName);
                }
            }

            // Storage buffers bind to the renderer's reserved slots by block name.
            foreach (var block in reflection.StorageBlocks)
            {
                var slot = block.OriginalName switch
                {
                    "g_transformBuffer" => (int)Buffers.ReservedBufferSlots.Transforms,
                    "g_instanceBuffer" => (int)Buffers.ReservedBufferSlots.VcsInstanceData,
                    _ => -1,
                };

                if (slot < 0)
                {
                    continue;
                }

                var blockIndex = GL.GetProgramResourceIndex(Program, ProgramInterface.ShaderStorageBlock, block.EmittedName);

                if (blockIndex != (int)All.InvalidIndex)
                {
                    GL.ShaderStorageBlockBinding(Program, blockIndex, slot);
                }
            }
        }
    }

    private string FindOriginalBufferName(string emittedPrefix)
    {
        foreach (var reflection in StageReflections)
        {
            foreach (var block in reflection.UniformBlocks)
            {
                if (block.EmittedName == emittedPrefix)
                {
                    return block.OriginalName;
                }
            }
        }

        return string.Empty;
    }

    private void AddMaterialDefault(string member, ActiveUniformType type)
    {
        if (!VariableDescriptions.TryGetValue(member, out var description))
        {
            return;
        }

        switch (type)
        {
            case ActiveUniformType.Float:
                Default.Material.FloatParams.TryAdd(member, description.FloatDefs[0]);
                break;

            case ActiveUniformType.FloatVec2 or ActiveUniformType.FloatVec3 or ActiveUniformType.FloatVec4:
                Default.Material.VectorParams.TryAdd(member, new Vector4(
                    description.FloatDefs[0], description.FloatDefs[1], description.FloatDefs[2], description.FloatDefs[3]));
                break;

            case ActiveUniformType.Int or ActiveUniformType.UnsignedInt or ActiveUniformType.Bool:
                Default.Material.IntParams.TryAdd(member, description.IntDefs[0]);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Called when this shader becomes the active program for a batch: uploads the per-view constant
    /// buffer members if the frame state changed since the last bind.
    /// </summary>
    public void BindFrameState()
    {
        var state = Pipeline.PerViewState;

        if (state.Version == lastPerViewVersion)
        {
            return;
        }

        lastPerViewVersion = state.Version;

        foreach (var (location, type, member) in perViewMembers)
        {
            ApplyPerViewMember(location, type, member, state);
        }
    }

    private void ApplyPerViewMember(int location, ActiveUniformType type, string member, VcsPerViewState state)
    {
        if (location < 0)
        {
            return;
        }

        switch (member)
        {
            case "g_matWorldToProjection":
            case "g_matPrimaryViewWorldToProjection":
            case "g_matPrevWorldToProjection":
                SetPerViewMatrix(location, type, state.WorldToProjection);
                break;
            case "g_matWorldToView":
                SetPerViewMatrix(location, type, state.WorldToView);
                break;
            case "g_matViewToProjection":
                SetPerViewMatrix(location, type, state.ViewToProjection);
                break;
            case "g_matProjectionToWorld":
                SetPerViewMatrix(location, type, state.ProjectionToWorld);
                break;
            case "g_vCameraPositionWs":
                GL.ProgramUniform3(Program, location, state.CameraPosition.X, state.CameraPosition.Y, state.CameraPosition.Z);
                break;
            case "g_vCameraDirWs":
                GL.ProgramUniform3(Program, location, state.CameraDir.X, state.CameraDir.Y, state.CameraDir.Z);
                break;
            case "g_vCameraUpDirWs":
                GL.ProgramUniform3(Program, location, state.CameraUp.X, state.CameraUp.Y, state.CameraUp.Z);
                break;
            case "g_flGameTime":
            case "g_flRealTime":
                GL.ProgramUniform1(Program, location, state.Time);
                break;
            case "g_flToneMapScalarLinear":
            case "g_flInvToneMapScalarLinear":
                GL.ProgramUniform1(Program, location, 1f);
                break;
            case "g_vViewportSize":
            case "g_vRenderTargetSize":
                GL.ProgramUniform2(Program, location, state.ViewportSize.X, state.ViewportSize.Y);
                break;
            case "g_vInvViewportSize":
                GL.ProgramUniform2(Program, location, 1f / state.ViewportSize.X, 1f / state.ViewportSize.Y);
                break;
            case "g_flNearPlane":
                GL.ProgramUniform1(Program, location, 1f);
                break;
            case "g_flViewportMinZ":
                GL.ProgramUniform1(Program, location, 0f);
                break;
            case "g_flViewportMaxZ":
                GL.ProgramUniform1(Program, location, 1f);
                break;
            default:
                // Everything else (fog toggles, wind, bindless indices, offsets) intentionally stays zero:
                // zero disables fog branches, and the world/camera offsets are zero because the renderer
                // does not use camera-relative rendering.
                break;
        }
    }

    private void SetPerViewMatrix(int location, ActiveUniformType type, Matrix4x4 value)
    {
        if (type == ActiveUniformType.FloatMat4)
        {
            // The emitted code multiplies row vectors (v * M) like HLSL, while our matrices are
            // System.Numerics row-vector convention; uploading transposed makes the GLSL columns
            // equal the mathematical columns.
            var matrix = value.ToOpenTK();
            GL.ProgramUniformMatrix4(Program, location, true, ref matrix);
        }
        else if (type == ActiveUniformType.FloatVec4)
        {
            // Anonymous row-major struct form: vec4 rows at consecutive locations.
            Span<float> rows =
            [
                value.M11, value.M12, value.M13, value.M14,
                value.M21, value.M22, value.M23, value.M24,
                value.M31, value.M32, value.M33, value.M34,
                value.M41, value.M42, value.M43, value.M44,
            ];

            GL.ProgramUniform4(Program, location, 4, ref rows[0]);
        }
    }

    /// <summary>Applies Valve's per-dynamic-combo render state for this program.</summary>
    public void ApplyRenderState()
    {
        if (PsRenderState is not null)
        {
            VcsRenderStateGl.Apply(PsRenderState);
        }
    }

    /// <summary>Restores the renderer's default state after <see cref="ApplyRenderState"/>.</summary>
    public void ResetRenderState()
    {
        if (PsRenderState is not null)
        {
            VcsRenderStateGl.Reset(PsRenderState);
        }
    }

    /// <inheritdoc/>
    public override bool SetTexture(int slot, string name, RenderTexture? texture)
    {
        var bound = false;

        if (base.SetTexture(slot, name, texture))
        {
            BindSamplerObject(slot, name);
            bound = true;
        }

        if (textureAliases.TryGetValue(name, out var aliases))
        {
            foreach (var alias in aliases)
            {
                if (base.SetTexture(slot, alias, texture))
                {
                    BindSamplerObject(slot, alias);
                    bound = true;
                }
            }
        }

        return bound;
    }

    private void BindSamplerObject(int slot, string uniformName)
    {
        if (samplerObjects.TryGetValue(uniformName, out var sampler))
        {
            GL.BindSampler(slot, sampler);
            boundSamplerUnits.Add(slot);
        }
    }

    /// <inheritdoc/>
    public override void PostRender()
    {
        foreach (var unit in boundSamplerUnits)
        {
            GL.BindSampler(unit, 0);
        }

        boundSamplerUnits.Clear();
    }

    /// <inheritdoc/>
    public override void SetUniform1(string name, float value)
    {
        if (materialParamAliases.TryGetValue(name, out var aliases))
        {
            foreach (var alias in aliases)
            {
                base.SetUniform1(alias, value);
            }

            return;
        }

        base.SetUniform1(name, value);
    }

    /// <inheritdoc/>
    public override void SetUniform1(string name, int value)
    {
        if (materialParamAliases.TryGetValue(name, out var aliases))
        {
            foreach (var alias in aliases)
            {
                base.SetUniform1(alias, value);
            }

            return;
        }

        base.SetUniform1(name, value);
    }

    /// <inheritdoc/>
    public override void SetMaterialVector4Uniform(string name, Vector4 value)
    {
        if (materialParamAliases.TryGetValue(name, out var aliases))
        {
            foreach (var alias in aliases)
            {
                base.SetMaterialVector4Uniform(alias, value);
            }

            return;
        }

        base.SetMaterialVector4Uniform(name, value);
    }
}
