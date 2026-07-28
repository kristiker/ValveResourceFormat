using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.CompiledShader;

namespace ValveResourceFormat.Renderer.Shaders.Vcs;

/// <summary>
/// Creates and caches OpenGL sampler objects from the static sampler state recovered from compiled shaders.
/// </summary>
public sealed class VcsSamplerCache : IDisposable
{
    private readonly Dictionary<ShaderSpirvReflection.VcsGlSamplerState, int> Samplers = [];

    /// <summary>
    /// Gets (or creates) a GL sampler object matching the given recovered sampler state.
    /// </summary>
    public int GetSampler(ShaderSpirvReflection.VcsGlSamplerState state)
    {
        if (Samplers.TryGetValue(state, out var sampler))
        {
            return sampler;
        }

        sampler = GL.GenSampler();

        // RsFilter is a D3D-style bitfield: 0x10 = min linear, 0x04 = mag linear, 0x01 = mip linear,
        // 0x55 = anisotropic, 0x80 = comparison.
        var filter = (int)(state.Filter ?? RsFilter.Anisotropic);
        var anisotropic = state.Filter is RsFilter.Anisotropic or RsFilter.ComparisonAnisotropic or RsFilter.UserConfig;
        var minLinear = anisotropic || (filter & 0x10) != 0;
        var magLinear = anisotropic || (filter & 0x04) != 0;
        var mipLinear = anisotropic || (filter & 0x01) != 0;

        GL.SamplerParameter(sampler, SamplerParameterName.TextureMinFilter, (int)(minLinear
            ? (mipLinear ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.LinearMipmapNearest)
            : (mipLinear ? TextureMinFilter.NearestMipmapLinear : TextureMinFilter.NearestMipmapNearest)));
        GL.SamplerParameter(sampler, SamplerParameterName.TextureMagFilter, (int)(magLinear ? TextureMagFilter.Linear : TextureMagFilter.Nearest));

        if (anisotropic)
        {
            var maxAniso = state.MaxAniso is > 0 ? state.MaxAniso.Value : 8;
            GL.SamplerParameter(sampler, SamplerParameterName.TextureMaxAnisotropyExt, maxAniso);
        }

        GL.SamplerParameter(sampler, SamplerParameterName.TextureWrapS, (int)ToGlWrap(state.AddressU));
        GL.SamplerParameter(sampler, SamplerParameterName.TextureWrapT, (int)ToGlWrap(state.AddressV));
        GL.SamplerParameter(sampler, SamplerParameterName.TextureWrapR, (int)ToGlWrap(state.AddressW));

        if (state.ComparisonFunc is { } comparison && (filter & 0x80) != 0)
        {
            GL.SamplerParameter(sampler, SamplerParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
            GL.SamplerParameter(sampler, SamplerParameterName.TextureCompareFunc, (int)ToGlComparison(comparison));
        }

        if (state.AddressU is RsTextureAddressMode.Border || state.AddressV is RsTextureAddressMode.Border || state.AddressW is RsTextureAddressMode.Border)
        {
            // BorderColor 0 = transparent black, which is also the GL default. Opaque white is the only
            // other value seen in shipped data.
            var color = state.BorderColor is 1 ? 1f : 0f;
            float[] borderColor = [color, color, color, color];
            GL.SamplerParameter(sampler, SamplerParameterName.TextureBorderColor, borderColor);
        }

        if (state.MinLod is { } minLod)
        {
            GL.SamplerParameter(sampler, SamplerParameterName.TextureMinLod, (float)minLod);
        }

        if (state.MaxLod is { } maxLod and > 0)
        {
            GL.SamplerParameter(sampler, SamplerParameterName.TextureMaxLod, (float)maxLod);
        }

        Samplers[state] = sampler;
        return sampler;
    }

    private static TextureWrapMode ToGlWrap(RsTextureAddressMode? mode) => mode switch
    {
        RsTextureAddressMode.Mirror => TextureWrapMode.MirroredRepeat,
        RsTextureAddressMode.Clamp => TextureWrapMode.ClampToEdge,
        RsTextureAddressMode.Border => TextureWrapMode.ClampToBorder,
        RsTextureAddressMode.MirrorOnce => (TextureWrapMode)All.MirrorClampToEdge,
        _ => TextureWrapMode.Repeat,
    };

    internal static DepthFunction ToGlComparison(RsComparison comparison) => comparison switch
    {
        RsComparison.Never => DepthFunction.Never,
        RsComparison.Less => DepthFunction.Less,
        RsComparison.Equal => DepthFunction.Equal,
        RsComparison.LessEqual => DepthFunction.Lequal,
        RsComparison.Greater => DepthFunction.Greater,
        RsComparison.NotEqual => DepthFunction.Notequal,
        RsComparison.GreaterEqual => DepthFunction.Gequal,
        RsComparison.Always => DepthFunction.Always,
        _ => DepthFunction.Always,
    };

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var sampler in Samplers.Values)
        {
            GL.DeleteSampler(sampler);
        }

        Samplers.Clear();
    }
}
