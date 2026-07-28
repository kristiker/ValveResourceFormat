using Microsoft.Extensions.Logging;
using ValveResourceFormat.IO;

namespace ValveResourceFormat.Renderer;

/// <summary>
/// Shared context containing loaders and caches used by the renderer.
/// </summary>
public class RendererContext : IDisposable
{
    /// <summary>
    /// Logger for diagnostic messages.
    /// </summary>
    public ILogger Logger { get; }

    /// <summary>
    /// Game file loader for loading resources from packages.
    /// </summary>
    public GameFileLoader FileLoader { get; }

    /// <summary>
    /// Material and texture loader and cache.
    /// </summary>
    public MaterialLoader MaterialLoader { get; }

    /// <summary>
    /// Shader compiler and cache.
    /// </summary>
    public ShaderLoader ShaderLoader { get; }

    /// <summary>
    /// Pipeline that loads Valve's compiled shaders (SPIR-V from .vcs files) as OpenGL programs.
    /// Only consulted when <see cref="UseGameShaders"/> is enabled.
    /// </summary>
    public Shaders.Vcs.VcsShaderPipeline VcsShaderPipeline { get; }

    /// <summary>
    /// When enabled, allowlisted material shaders render with Valve's own compiled shaders instead of
    /// the built-in ones, falling back per-shader on any failure.
    /// </summary>
    public bool UseGameShaders { get; set; }

    /// <summary>
    /// GPU mesh buffer and vertex array object cache.
    /// </summary>
    public GPUMeshBufferCache MeshBufferCache { get; }

    /// <summary>
    /// Maximum texture mip size to load in <see cref="MaterialLoader"/>.
    /// </summary>
    public int MaxTextureSize { get; set; } = 1024;

    /// <summary>
    /// Main camera field of view, in horizontal degrees at a 4:3 aspect ratio.
    /// See <see cref="Camera.FieldOfView"/>.
    /// </summary>
    public float FieldOfView { get; set; } = 90.0f;

    /// <summary>
    /// First-person viewmodel field of view, in horizontal degrees at a 4:3 aspect ratio.
    /// </summary>
    public float ViewmodelFieldOfView { get; set; } = 64.0f;

    /// <summary>
    /// Initializes a new renderer context.
    /// </summary>
    /// <param name="fileLoader">Game file loader for resource access.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public RendererContext(GameFileLoader fileLoader, ILogger logger)
    {
        FileLoader = fileLoader;
        Logger = logger;

        MaterialLoader = new MaterialLoader(this);
        ShaderLoader = new ShaderLoader(this);
        MeshBufferCache = new GPUMeshBufferCache(this);
        VcsShaderPipeline = new Shaders.Vcs.VcsShaderPipeline(this);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases resources owned by the context.
    /// </summary>
    /// <param name="disposing">True to release managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        ShaderLoader?.Dispose();
        VcsShaderPipeline?.Dispose();
    }
}
