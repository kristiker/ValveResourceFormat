using Microsoft.Extensions.Logging;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.Materials;

namespace ValveResourceFormat.Renderer.SceneEnvironment;

/// <summary>Merges per-probe environment cube maps into one cube map array.</summary>
public static class EnvMapArrayBuilder
{
    private const int CubeFaces = 6;

    // Alpha is the RGBM multiplier, so 1/6 decodes to the same white the plain fetch reads.
    private static readonly float[] White = [1f, 1f, 1f, 1f / 6f];

    /// <summary>Builds one array covering every env map, releasing the individual cube maps.</summary>
    public static RenderTexture? Merge(List<SceneEnvMap> envMaps, MaterialLoader materialLoader, ILogger logger)
    {
        if (envMaps.Count == 0)
        {
            return null;
        }

        var first = envMaps[0].EnvMapTexture;

        if (first == null || !SourcesAreCompatible(envMaps, first, logger))
        {
            return null;
        }

        GL.GetInteger(GetPName.MaxArrayTextureLayers, out var maxLayers);

        var count = Math.Min(envMaps.Count, Math.Min(maxLayers / CubeFaces, Buffers.EnvMapArray.MAX_ENVMAPS));

        if (count < envMaps.Count)
        {
            logger.LogWarning("Cube map array only had room for {Count} of {EnvMapCount} environment maps, the rest were dropped",
                count, envMaps.Count);
        }

        var array = new RenderTexture(TextureTarget.TextureCubeMapArray, first.Width, first.Height, count, first.NumMipLevels);
        GL.TextureStorage3D(array.Handle, first.NumMipLevels, InternalFormatOf(first), first.Width, first.Height, count * CubeFaces);

        array.SetFiltering(TextureMinFilter.LinearMipmapLinear, TextureMagFilter.Linear);
        array.SetWrapMode(TextureWrapMode.ClampToEdge);

#if DEBUG
        array.SetLabel("EnvMapArray");
#endif

        for (var i = 0; i < count; i++)
        {
            Copy(envMaps[i].EnvMapTexture!, array, i);
            envMaps[i].ArrayIndex = i;
        }

        Release(envMaps, materialLoader);

        envMaps.RemoveRange(count, envMaps.Count - count);

        logger.LogDebug("Merged {Count} environment maps into a {Size}x{Size} cube map array", count, first.Width, first.Height);

        return array;
    }

    /// <summary>Builds a single white cube, for scenes that ship no env maps at all.</summary>
    public static RenderTexture CreateWhite()
    {
        var array = new RenderTexture(TextureTarget.TextureCubeMapArray, 1, 1, 1, 1);
        GL.TextureStorage3D(array.Handle, 1, SizedInternalFormat.Rgba16f, 1, 1, CubeFaces);
        GL.ClearTexImage(array.Handle, 0, PixelFormat.Rgba, PixelType.Float, White);

        array.SetFiltering(TextureMinFilter.Linear, TextureMagFilter.Linear);
        array.SetWrapMode(TextureWrapMode.ClampToEdge);

#if DEBUG
        array.SetLabel("EnvMapArrayWhite");
#endif

        return array;
    }

    private static bool SourcesAreCompatible(List<SceneEnvMap> envMaps, RenderTexture first, ILogger logger)
    {
        foreach (var envMap in envMaps)
        {
            var texture = envMap.EnvMapTexture;

            if (texture == null || texture.Target != TextureTarget.TextureCubeMap)
            {
                logger.LogWarning("Environment map is not a plain cube map, not merging the scene's cube maps");
                return false;
            }

            if (texture.Width != first.Width || texture.Height != first.Height || texture.NumMipLevels != first.NumMipLevels)
            {
                logger.LogWarning("Environment map is {Width}x{Height} with {Mips} mips, which does not match the {FirstWidth}x{FirstHeight} with {FirstMips} of the first, not merging the scene's cube maps",
                    texture.Width, texture.Height, texture.NumMipLevels, first.Width, first.Height, first.NumMipLevels);
                return false;
            }

            if (InternalFormatOf(texture) != InternalFormatOf(first))
            {
                logger.LogWarning("Environment maps are in mixed internal formats, not merging the scene's cube maps");
                return false;
            }
        }

        return true;
    }

    private static SizedInternalFormat InternalFormatOf(RenderTexture texture)
    {
        GL.GetTextureLevelParameter(texture.Handle, 0, GetTextureParameter.TextureInternalFormat, out int format);
        return (SizedInternalFormat)format;
    }

    private static void Copy(RenderTexture source, RenderTexture array, int index)
    {
        for (var level = 0; level < source.NumMipLevels; level++)
        {
            var width = Math.Max(source.Width >> level, 1);
            var height = Math.Max(source.Height >> level, 1);

            GL.CopyImageSubData(
                source.Handle, ImageTarget.TextureCubeMap, level, 0, 0, 0,
                array.Handle, ImageTarget.TextureCubeMapArray, level, 0, 0, index * CubeFaces,
                width, height, CubeFaces);
        }
    }

    private static void Release(List<SceneEnvMap> envMaps, MaterialLoader materialLoader)
    {
        var released = new HashSet<int>();

        foreach (var envMap in envMaps)
        {
            var texture = envMap.EnvMapTexture;
            envMap.EnvMapTexture = null;

            if (texture == null || !released.Add(texture.Handle))
            {
                continue;
            }

            if (envMap.TexturePath != null)
            {
                materialLoader.EvictTexture(envMap.TexturePath, srgbRead: true);
            }
            else
            {
                texture.Delete();
            }
        }
    }
}
