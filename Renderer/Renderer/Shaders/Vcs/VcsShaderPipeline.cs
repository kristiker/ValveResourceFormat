using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.CompiledShader;
using ValveResourceFormat.IO;

namespace ValveResourceFormat.Renderer.Shaders.Vcs;

/// <summary>
/// Per-frame view state consumed by <see cref="VcsShader.BindFrameState"/> to fill Valve's per-view
/// constant buffer members. Updated once per rendered view.
/// </summary>
public class VcsPerViewState
{
    /// <summary>Gets the change counter; shaders skip re-uploading when it has not moved.</summary>
    public int Version { get; private set; }

    /// <summary>World to projection matrix.</summary>
    public Matrix4x4 WorldToProjection { get; private set; } = Matrix4x4.Identity;
    /// <summary>World to view matrix.</summary>
    public Matrix4x4 WorldToView { get; private set; } = Matrix4x4.Identity;
    /// <summary>View to projection matrix.</summary>
    public Matrix4x4 ViewToProjection { get; private set; } = Matrix4x4.Identity;
    /// <summary>Projection to world matrix (full inverse of <see cref="WorldToProjection"/>).</summary>
    public Matrix4x4 ProjectionToWorld { get; private set; } = Matrix4x4.Identity;
    /// <summary>Camera position in world space.</summary>
    public Vector3 CameraPosition { get; private set; }
    /// <summary>Camera forward direction in world space.</summary>
    public Vector3 CameraDir { get; private set; }
    /// <summary>Camera up direction in world space.</summary>
    public Vector3 CameraUp { get; private set; }
    /// <summary>Viewport size in pixels.</summary>
    public Vector2 ViewportSize { get; private set; } = Vector2.One;
    /// <summary>Time in seconds, drives shader animation.</summary>
    public float Time { get; private set; }

    /// <summary>Fills the state from the camera and bumps <see cref="Version"/>.</summary>
    public void Update(Camera camera, float time)
    {
        WorldToProjection = camera.ViewProjectionMatrix;
        WorldToView = camera.CameraViewMatrix;
        ViewToProjection = camera.ProjectionMatrix;
        Matrix4x4.Invert(camera.ViewProjectionMatrix, out var projectionToWorld);
        ProjectionToWorld = projectionToWorld;
        CameraPosition = camera.Location;
        CameraDir = camera.Forward;
        CameraUp = camera.Up;
        ViewportSize = camera.WindowSize;
        Time = time;
        Version++;
    }
}

/// <summary>
/// Loads Valve's compiled shaders (SPIR-V from .vcs files) as OpenGL programs and serves them to the
/// renderer in place of the built-in shaders. Any failure falls back per-shader to the built-in path.
/// </summary>
public sealed class VcsShaderPipeline : IDisposable
{
    /// <summary>
    /// Shaders the pipeline will attempt. Grown deliberately as shaders are verified to work.
    /// </summary>
    private static readonly HashSet<string> AllowedShaders = new(StringComparer.Ordinal)
    {
        "csgo_effects.vfx",
    };

    private readonly RendererContext RendererContext;
    internal VcsSamplerCache SamplerCache { get; } = new();

    /// <summary>Gets the per-frame view state shared by all vcs shaders.</summary>
    public VcsPerViewState PerViewState { get; } = new();

    private readonly Dictionary<ulong, VcsShader> CachedShaders = [];
    private readonly HashSet<ulong> FailedRequests = [];
    private readonly HashSet<string> LoggedFailures = [];
    private readonly Dictionary<(string ShaderName, long VsStaticId, long PsStaticId), VcsStaticComboPrograms> StaticComboPrograms = [];

    /// <summary>Initializes a new instance of the <see cref="VcsShaderPipeline"/> class.</summary>
    public VcsShaderPipeline(RendererContext rendererContext)
    {
        RendererContext = rendererContext;
    }

    /// <summary>
    /// Attempts to load a compiled game shader for the given material shader name and combo arguments.
    /// Returns false when the shader is not allowlisted or any part of loading failed, in which case the
    /// caller should fall back to the built-in shaders.
    /// </summary>
    public bool TryLoadShader(string shaderName, IReadOnlyDictionary<string, byte> arguments, out Shader? shader)
    {
        shader = null;

        if (!AllowedShaders.Contains(shaderName))
        {
            return false;
        }

        var hash = CalculateRequestHash(shaderName, arguments);

        if (CachedShaders.TryGetValue(hash, out var cachedShader))
        {
            shader = cachedShader;
            return true;
        }

        if (FailedRequests.Contains(hash))
        {
            return false;
        }

        try
        {
            var vcsShader = Load(shaderName, arguments);
            CachedShaders[hash] = vcsShader;
            shader = vcsShader;
            return true;
        }
        catch (Exception e)
        {
            FailedRequests.Add(hash);

            if (LoggedFailures.Add(shaderName))
            {
                RendererContext.Logger.LogWarning(e, "Compiled shader pipeline failed for '{ShaderName}', falling back to built-in shaders", shaderName);
            }

            return false;
        }
    }

    private VcsShader Load(string shaderName, IReadOnlyDictionary<string, byte> arguments)
    {
        var collection = RendererContext.FileLoader.LoadShader(shaderName, VcsPlatformType.VULKAN);

        var features = collection.Features ?? throw new FileNotFoundException($"No features program found for '{shaderName}'.");
        var vsProgram = collection.Get(VcsProgramType.VertexShader) ?? throw new FileNotFoundException($"No vertex program found for '{shaderName}'.");
        var psProgram = collection.Get(VcsProgramType.PixelShader) ?? throw new FileNotFoundException($"No pixel program found for '{shaderName}'.");

        if (vsProgram.VcsPlatformType != VcsPlatformType.VULKAN)
        {
            throw new NotSupportedException($"'{shaderName}' has no Vulkan (SPIR-V) build, found {vsProgram.VcsPlatformType}.");
        }

        var featureParams = new Dictionary<string, byte>();
        var staticParams = new Dictionary<string, byte>();

        foreach (var (key, value) in arguments)
        {
            if (key.StartsWith("F_", StringComparison.Ordinal))
            {
                featureParams[key] = value;
            }
            else if (key.StartsWith("S_", StringComparison.Ordinal))
            {
                staticParams[key] = value;
            }
        }

        var vsStaticId = ResolveStaticComboId(features, vsProgram, featureParams, staticParams);
        var psStaticId = ResolveStaticComboId(features, psProgram, featureParams, staticParams);

        var comboKey = (shaderName, vsStaticId, psStaticId);

        if (!StaticComboPrograms.TryGetValue(comboKey, out var programs))
        {
            programs = new VcsStaticComboPrograms(this, RendererContext, shaderName, vsProgram, psProgram, vsStaticId, psStaticId);
            StaticComboPrograms[comboKey] = programs;
        }

        return programs.GetVariant(arguments);
    }

    private static long ResolveStaticComboId(VfxProgramData features, VfxProgramData program,
        Dictionary<string, byte> featureParams, Dictionary<string, byte> staticParams)
    {
        var (config, staticComboId) = ShaderDataProvider.GetStaticConfiguration_ForFeatureState(features, program, featureParams, staticParams);

        if (!program.StaticComboEntries.ContainsKey(staticComboId)
            && ShaderDataProvider.TryReduceStaticConfiguration(program, config, out var reducedConfig))
        {
            staticComboId = new ConfigMappingParams(program).CalcStaticComboIdFromValues(reducedConfig);
        }

        if (!program.StaticComboEntries.ContainsKey(staticComboId))
        {
            throw new InvalidDataException($"Static combo {staticComboId} does not exist in {program.FilenamePath}.");
        }

        return staticComboId;
    }

    private static readonly byte[] NewLineArray = "\n"u8.ToArray();

    private static ulong CalculateRequestHash(string shaderName, IReadOnlyDictionary<string, byte> arguments)
    {
        var hash = new XxHash3(StringToken.MURMUR2SEED);
        hash.Append(MemoryMarshal.AsBytes(shaderName.AsSpan()));

        Span<byte> valueSpan = stackalloc byte[1];

        foreach (var (key, value) in arguments.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            if (value == 0)
            {
                continue;
            }

            hash.Append(NewLineArray);
            hash.Append(MemoryMarshal.AsBytes(key.AsSpan()));
            valueSpan[0] = value;
            hash.Append(valueSpan);
        }

        return hash.GetCurrentHashAsUInt64();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var programs in StaticComboPrograms.Values)
        {
            programs.Dispose();
        }

        StaticComboPrograms.Clear();
        CachedShaders.Clear();
        SamplerCache.Dispose();
    }
}

/// <summary>
/// Owns every linked GL program of one (shader, vertex static combo, pixel static combo) selection:
/// one program per (vertex dynamic combo, pixel dynamic combo) pair, sharing compiled stage objects.
/// All dynamic combos are precompiled up front so switching between them is a dictionary lookup.
/// </summary>
internal sealed class VcsStaticComboPrograms : IDisposable
{
    private readonly VcsShaderPipeline Pipeline;
    private readonly RendererContext RendererContext;
    private readonly string ShaderName;
    private readonly VfxProgramData VsProgram;
    private readonly VfxProgramData PsProgram;
    private readonly VfxStaticComboData VsCombo;
    private readonly VfxStaticComboData PsCombo;
    private readonly Dictionary<string, VfxVariableDescription> VariableDescriptions = [];

    private readonly Dictionary<int, (int ShaderObject, ShaderSpirvReflection.VcsGlReflectionInfo Reflection)> VsStages = [];
    private readonly Dictionary<int, (int ShaderObject, ShaderSpirvReflection.VcsGlReflectionInfo Reflection)> PsStages = [];
    private readonly Dictionary<(long VsDynamicId, long PsDynamicId), VcsShader> Variants = [];

    public VcsStaticComboPrograms(VcsShaderPipeline pipeline, RendererContext rendererContext, string shaderName,
        VfxProgramData vsProgram, VfxProgramData psProgram, long vsStaticId, long psStaticId)
    {
        Pipeline = pipeline;
        RendererContext = rendererContext;
        ShaderName = shaderName;
        VsProgram = vsProgram;
        PsProgram = psProgram;

        vsProgram.StaticComboCache.EnsureCapacity(4);
        psProgram.StaticComboCache.EnsureCapacity(4);

        VsCombo = vsProgram.StaticComboCache.Get(vsStaticId);
        PsCombo = psProgram.StaticComboCache.Get(psStaticId);

        foreach (var program in (ReadOnlySpan<VfxProgramData>)[vsProgram, psProgram])
        {
            foreach (var variable in program.VariableDescriptions)
            {
                VariableDescriptions.TryAdd(variable.Name, variable);
            }
        }
    }

    public VcsShader GetVariant(IReadOnlyDictionary<string, byte> arguments)
    {
        var vsDynamicId = ResolveDynamicComboId(VsProgram, VsCombo, arguments);
        var psDynamicId = ResolveDynamicComboId(PsProgram, PsCombo, arguments);

        if (Variants.TryGetValue((vsDynamicId, psDynamicId), out var variant))
        {
            return variant;
        }

        var isFirstVariant = Variants.Count == 0;

        // The first variant links blocking so failures are caught here and fall back cleanly.
        variant = BuildVariant(vsDynamicId, psDynamicId, arguments, blocking: isFirstVariant);

        if (isFirstVariant)
        {
            PrecompileRemainingVariants(vsDynamicId, psDynamicId, arguments);
        }

        return variant;
    }

    private void PrecompileRemainingVariants(long vsDynamicId, long psDynamicId, IReadOnlyDictionary<string, byte> arguments)
    {
        // Precompile every other dynamic combo of each stage, paired with the requested combo of the
        // other stage, so runtime combo switches only ever hit the dictionary. Links are issued
        // non-blocking to let the driver compile them in parallel. A combo that fails here (e.g. a
        // skinned variant needing an engine buffer we cannot provide) is skipped; if it is ever
        // actually requested it fails that request alone and falls back to the built-in shader.
        foreach (var psEntry in PsCombo.DynamicCombos)
        {
            if (!Variants.ContainsKey((vsDynamicId, psEntry.DynamicComboId)))
            {
                TryPrecompileVariant(vsDynamicId, psEntry.DynamicComboId, arguments);
            }
        }

        foreach (var vsEntry in VsCombo.DynamicCombos)
        {
            if (!Variants.ContainsKey((vsEntry.DynamicComboId, psDynamicId)))
            {
                TryPrecompileVariant(vsEntry.DynamicComboId, psDynamicId, arguments);
            }
        }
    }

    private void TryPrecompileVariant(long vsDynamicId, long psDynamicId, IReadOnlyDictionary<string, byte> arguments)
    {
        try
        {
            BuildVariant(vsDynamicId, psDynamicId, arguments, blocking: false);
        }
        catch (Exception e)
        {
            RendererContext.Logger.LogDebug("Skipping unsupported variant vs={VsDynamicId} ps={PsDynamicId} of '{ShaderName}': {Message}", vsDynamicId, psDynamicId, ShaderName, e.Message);
        }
    }

    private static long ResolveDynamicComboId(VfxProgramData program, VfxStaticComboData combo, IReadOnlyDictionary<string, byte> arguments)
    {
        var dynamicCombos = program.DynamicComboArray;
        long dynamicComboId = 0;

        if (dynamicCombos.Length > 0)
        {
            var config = new int[dynamicCombos.Length];

            foreach (var dynamicCombo in dynamicCombos)
            {
                arguments.TryGetValue(dynamicCombo.Name, out var value);
                config[dynamicCombo.BlockIndex] = Math.Clamp(value, (byte)dynamicCombo.RangeMin, (byte)dynamicCombo.RangeMax);
            }

            dynamicComboId = program.CalcDynamicComboIdFromValues(config);
        }

        if (combo.GetDynamicComboIndex(dynamicComboId) < 0)
        {
            // Combo rules removed the requested combination; fall back to the first compiled one.
            dynamicComboId = combo.DynamicCombos[0].DynamicComboId;
        }

        return dynamicComboId;
    }

    private VcsShader BuildVariant(long vsDynamicId, long psDynamicId, IReadOnlyDictionary<string, byte> arguments, bool blocking)
    {
        var vsEntry = VsCombo.DynamicCombos[VsCombo.GetDynamicComboIndex(vsDynamicId)];
        var psEntry = PsCombo.DynamicCombos[PsCombo.GetDynamicComboIndex(psDynamicId)];

        var (vsObject, vsReflection) = GetOrCompileStage(VsCombo, vsEntry.ShaderFileId, VsStages, ShaderType.VertexShader);
        var (psObject, psReflection) = GetOrCompileStage(PsCombo, psEntry.ShaderFileId, PsStages, ShaderType.FragmentShader);

        var program = GL.CreateProgram();
        GL.AttachShader(program, vsObject);
        GL.AttachShader(program, psObject);
        GL.LinkProgram(program);

#if DEBUG
        var label = $"vcs:{ShaderName}";
        GL.ObjectLabel(ObjectLabelIdentifier.Program, program, label.Length, label);
#endif

        var uniformNames = new HashSet<string>(StringComparer.Ordinal);
        var srgbUniforms = new HashSet<string>(StringComparer.Ordinal);

        foreach (var reflection in (ReadOnlySpan<ShaderSpirvReflection.VcsGlReflectionInfo>)[vsReflection, psReflection])
        {
            foreach (var combined in reflection.CombinedSamplers)
            {
                uniformNames.Add(combined.TextureName);
                uniformNames.Add(combined.UniformName);

                if (VariableDescriptions.TryGetValue(combined.TextureName, out var textureVariable) && textureVariable.SrgbRead)
                {
                    srgbUniforms.Add(combined.TextureName);
                }
            }
        }

        foreach (var name in VariableDescriptions.Keys)
        {
            uniformNames.Add(name);
        }

        var shader = new VcsShader(ShaderName, RendererContext)
        {
#if DEBUG
            FileName = ShaderName,
#endif
            Program = program,
            ShaderObjects = [], // Stage objects are shared between variants and owned by this class
            Parameters = arguments,
            RenderModes = [],
            UniformNames = uniformNames,
            SrgbUniforms = srgbUniforms,
            SamplerUserConfigUniforms = [],
            PsRenderState = psEntry as VfxRenderStateInfoPixelShader,
            StageReflections = [vsReflection, psReflection],
            VariableDescriptions = VariableDescriptions,
            Pipeline = Pipeline,
        };

        if (blocking && !shader.EnsureLoaded())
        {
            GL.GetProgramInfoLog(program, out var log);
            GL.DeleteProgram(program);
            throw new ShaderLoader.ShaderCompilerException($"Failed to link compiled shader '{ShaderName}':\n{log}");
        }

        Variants[(vsDynamicId, psDynamicId)] = shader;
        RendererContext.Logger.LogInformation("Compiled game shader '{ShaderName}' variant vs={VsDynamicId} ps={PsDynamicId} (program={Program})", ShaderName, vsDynamicId, psDynamicId, program);

        return shader;
    }

    private (int ShaderObject, ShaderSpirvReflection.VcsGlReflectionInfo Reflection) GetOrCompileStage(
        VfxStaticComboData combo, int shaderFileId,
        Dictionary<int, (int, ShaderSpirvReflection.VcsGlReflectionInfo)> stageCache, ShaderType shaderType)
    {
        if (stageCache.TryGetValue(shaderFileId, out var cached))
        {
            return cached;
        }

        if (combo.ShaderFiles[shaderFileId] is not VfxShaderFileVulkan vulkanFile)
        {
            throw new NotSupportedException($"Shader file {shaderFileId} of '{ShaderName}' is not SPIR-V.");
        }

        if (!ShaderSpirvReflection.ReflectSpirvOpenGl(vulkanFile, out var source, out var reflection))
        {
            throw new ShaderLoader.ShaderCompilerException($"SPIRV-Cross failed for '{ShaderName}': {source}");
        }

        foreach (var block in reflection.StorageBlocks)
        {
            if (block.OriginalName is not ("g_transformBuffer" or "g_instanceBuffer"))
            {
                throw new NotSupportedException($"'{ShaderName}' uses unsupported storage buffer '{block.OriginalName}'.");
            }
        }

        var shaderObject = GL.CreateShader(shaderType);
        GL.ShaderSource(shaderObject, source);
        GL.CompileShader(shaderObject);
        GL.GetShader(shaderObject, ShaderParameter.CompileStatus, out var status);

        if (status != 1)
        {
            GL.GetShaderInfoLog(shaderObject, out var log);
            GL.DeleteShader(shaderObject);
            throw new ShaderLoader.ShaderCompilerException($"Failed to compile game shader '{ShaderName}' ({shaderType}):\n{log}");
        }

        var result = (shaderObject, reflection);
        stageCache[shaderFileId] = result;
        return result;
    }

    public void Dispose()
    {
        foreach (var (shaderObject, _) in VsStages.Values)
        {
            GL.DeleteShader(shaderObject);
        }

        foreach (var (shaderObject, _) in PsStages.Values)
        {
            GL.DeleteShader(shaderObject);
        }

        foreach (var variant in Variants.Values)
        {
            GL.DeleteProgram(variant.Program);
        }

        VsStages.Clear();
        PsStages.Clear();
        Variants.Clear();
    }
}
