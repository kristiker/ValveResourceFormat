using System.Linq;
using Microsoft.Extensions.Logging;
using OpenTK.Graphics.OpenGL;
using ValveResourceFormat.Renderer.Materials;

namespace ValveResourceFormat.Renderer.SceneEnvironment;

/// <summary>The scene-wide light probe textures every probe samples from.</summary>
public sealed class LightProbeAtlas
{
    /// <summary>Gets the irradiance atlas. Its depth is six times <see cref="GridSize"/>.</summary>
    public required RenderTexture Irradiance { get; init; }

    /// <summary>Gets the direct light index atlas, if the scene has one.</summary>
    public RenderTexture? DirectLightIndices { get; init; }

    /// <summary>Gets the direct light scalar atlas, if the scene has one.</summary>
    public RenderTexture? DirectLightScalars { get; init; }

    /// <summary>Gets the direct light shadow atlas, if the scene has one.</summary>
    public RenderTexture? DirectLightShadows { get; init; }

    /// <summary>Gets the atlas dimensions in probe grid cells.</summary>
    public Vector3 GridSize { get; init; }

    /// <summary>Gets whether the renderer created these textures rather than the material loader.</summary>
    public bool OwnsTextures { get; init; }

    /// <summary>Deletes the atlas textures, if this atlas owns them.</summary>
    public void Delete()
    {
        if (!OwnsTextures)
        {
            return;
        }

        Irradiance.Delete();
        DirectLightIndices?.Delete();
        DirectLightScalars?.Delete();
        DirectLightShadows?.Delete();
    }
}

/// <summary>Merges per-probe light probe volume textures into one atlas per texture kind.</summary>
public static class LightProbeAtlasBuilder
{
    private const int IrradianceSlices = 6;

    /// <summary>Builds one atlas covering every probe, releasing the individual probe textures.</summary>
    public static LightProbeAtlas? Merge(List<SceneLightProbe> probes, MaterialLoader materialLoader, ILogger logger)
    {
        if (probes.Count == 0)
        {
            return null;
        }

        var grids = new List<(int Width, int Height, int Depth)>(probes.Count);

        foreach (var probe in probes)
        {
            grids.Add(GridSizeOf(probe, logger));
        }

        if (grids.Any(static g => g.Width == 0))
        {
            return null;
        }

        if (!SourcesAreCompatible(probes, grids, logger))
        {
            return null;
        }

        GL.GetInteger(GetPName.Max3DTextureSize, out var maxTextureSize);

        var placed = LightProbeAtlasPacker.Pack(grids, maxTextureSize, maxTextureSize / IrradianceSlices,
            out var slots, out var atlasSize);

        if (placed == 0)
        {
            logger.LogWarning("Could not fit any of {ProbeCount} light probe volumes into a probe atlas", probes.Count);
            return null;
        }

        if (placed < probes.Count)
        {
            logger.LogWarning("Probe atlas only had room for {Placed} of {ProbeCount} light probe volumes, the rest were dropped",
                placed, probes.Count);
        }

        var first = probes[0];

        var atlas = new LightProbeAtlas
        {
            Irradiance = CreateAtlasTexture(first.Irradiance!, atlasSize.Width, atlasSize.Height, atlasSize.Depth * IrradianceSlices, "LPV_IrradianceAtlas"),
            DirectLightIndices = MaybeCreate(first.DirectLightIndices, atlasSize, "LPV_IndicesAtlas"),
            DirectLightScalars = MaybeCreate(first.DirectLightScalars, atlasSize, "LPV_ScalarsAtlas"),
            DirectLightShadows = MaybeCreate(first.DirectLightShadows, atlasSize, "LPV_ShadowsAtlas"),
            GridSize = new Vector3(atlasSize.Width, atlasSize.Height, atlasSize.Depth),
            OwnsTextures = true,
        };

        atlas.DirectLightIndices?.SetFiltering(TextureMinFilter.Nearest, TextureMagFilter.Nearest);

        var dropped = new List<SceneLightProbe>();

        for (var i = 0; i < probes.Count; i++)
        {
            var probe = probes[i];
            var slot = slots[i];

            if (slot.Width == 0)
            {
                dropped.Add(probe);
                continue;
            }

            CopyIrradiance(probe.Irradiance!, atlas.Irradiance, slot, atlasSize.Depth);
            Copy(probe.DirectLightIndices, atlas.DirectLightIndices, slot);
            Copy(probe.DirectLightScalars, atlas.DirectLightScalars, slot);
            Copy(probe.DirectLightShadows, atlas.DirectLightShadows, slot);

            probe.AtlasOffset = new Vector3(slot.X, slot.Y, slot.Z);
            probe.AtlasSize = new Vector3(slot.Width, slot.Height, slot.Depth);
        }

        foreach (var probe in probes)
        {
            Release(probe, materialLoader);
        }

        foreach (var probe in dropped)
        {
            probes.Remove(probe);
        }

        logger.LogDebug("Merged {Placed} light probe volumes into a {Width}x{Height}x{Depth} cell probe atlas",
            placed, atlasSize.Width, atlasSize.Height, atlasSize.Depth);

        return atlas;
    }

    /// <summary>Wraps the textures of a scene that already ships a prebaked atlas.</summary>
    public static LightProbeAtlas? FromPrebaked(SceneLightProbe probe)
    {
        if (probe.Irradiance == null || probe.DirectLightShadows == null)
        {
            return null;
        }

        var shadows = probe.DirectLightShadows;

        return new LightProbeAtlas
        {
            Irradiance = probe.Irradiance,
            DirectLightIndices = probe.DirectLightIndices,
            DirectLightScalars = probe.DirectLightScalars,
            DirectLightShadows = shadows,
            GridSize = new Vector3(shadows.Width, shadows.Height, shadows.Depth),
            OwnsTextures = false,
        };
    }

    /// <summary>Builds a single unlit cell, for when a scene's probes cannot be merged.</summary>
    public static LightProbeAtlas CreateEmpty()
    {
        var irradiance = new RenderTexture(TextureTarget.Texture3D, 1, 1, IrradianceSlices, 1);
        GL.TextureStorage3D(irradiance.Handle, 1, SizedInternalFormat.Rgba16f, 1, 1, IrradianceSlices);
        Clear(irradiance);

        return new LightProbeAtlas
        {
            Irradiance = irradiance,
            DirectLightIndices = CreateEmptyDirect("LPV_EmptyIndices"),
            DirectLightScalars = CreateEmptyDirect("LPV_EmptyScalars"),
            DirectLightShadows = CreateEmptyDirect("LPV_EmptyShadows"),
            GridSize = Vector3.One,
            OwnsTextures = true,
        };

        static RenderTexture CreateEmptyDirect(string label)
        {
            var texture = new RenderTexture(TextureTarget.Texture3D, 1, 1, 1, 1);
            GL.TextureStorage3D(texture.Handle, 1, SizedInternalFormat.Rgba8, 1, 1, 1);
            Clear(texture);

#if DEBUG
            texture.SetLabel(label);
#endif

            return texture;
        }

        static void Clear(RenderTexture texture)
        {
            GL.ClearTexImage(texture.Handle, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            texture.SetFiltering(TextureMinFilter.Nearest, TextureMagFilter.Nearest);
            texture.SetWrapMode(TextureWrapMode.ClampToEdge);
        }
    }

    private static (int Width, int Height, int Depth) GridSizeOf(SceneLightProbe probe, ILogger logger)
    {
        var direct = probe.DirectLightShadows ?? probe.DirectLightScalars ?? probe.DirectLightIndices;

        if (direct != null)
        {
            return (direct.Width, direct.Height, direct.Depth);
        }

        var irradiance = probe.Irradiance;

        if (irradiance == null || irradiance.Depth % IrradianceSlices != 0)
        {
            logger.LogWarning("Light probe volume has no usable irradiance grid, not merging the scene's probes");
            return default;
        }

        return (irradiance.Width, irradiance.Height, irradiance.Depth / IrradianceSlices);
    }

    private static bool SourcesAreCompatible(List<SceneLightProbe> probes, List<(int Width, int Height, int Depth)> grids, ILogger logger)
    {
        for (var i = 0; i < probes.Count; i++)
        {
            var probe = probes[i];
            var grid = grids[i];

            if (!Matches(probe.Irradiance, probes[0].Irradiance, grid, IrradianceSlices, logger)
                || !Matches(probe.DirectLightIndices, probes[0].DirectLightIndices, grid, 1, logger)
                || !Matches(probe.DirectLightScalars, probes[0].DirectLightScalars, grid, 1, logger)
                || !Matches(probe.DirectLightShadows, probes[0].DirectLightShadows, grid, 1, logger))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Matches(RenderTexture? texture, RenderTexture? reference, (int Width, int Height, int Depth) grid, int depthFactor, ILogger logger)
    {
        if (texture == null || reference == null)
        {
            if (texture != reference)
            {
                logger.LogWarning("Light probe volumes carry different texture sets, not merging the scene's probes");
                return false;
            }

            return true;
        }

        if (texture.Width != grid.Width || texture.Height != grid.Height || texture.Depth != grid.Depth * depthFactor)
        {
            logger.LogWarning("Light probe volume texture is {Width}x{Height}x{Depth}, which does not match its {GridWidth}x{GridHeight}x{GridDepth} cell grid, not merging the scene's probes",
                texture.Width, texture.Height, texture.Depth, grid.Width, grid.Height, grid.Depth * depthFactor);
            return false;
        }

        if (InternalFormatOf(texture) != InternalFormatOf(reference))
        {
            logger.LogWarning("Light probe volume textures are in mixed internal formats, not merging the scene's probes");
            return false;
        }

        return true;
    }

    private static SizedInternalFormat InternalFormatOf(RenderTexture texture)
    {
        GL.GetTextureLevelParameter(texture.Handle, 0, GetTextureParameter.TextureInternalFormat, out int format);
        return (SizedInternalFormat)format;
    }

    private static RenderTexture? MaybeCreate(RenderTexture? source, (int Width, int Height, int Depth) size, string label)
        => source == null ? null : CreateAtlasTexture(source, size.Width, size.Height, size.Depth, label);

    private static RenderTexture CreateAtlasTexture(RenderTexture source, int width, int height, int depth, string label)
    {
        var atlas = new RenderTexture(TextureTarget.Texture3D, width, height, depth, 1);
        GL.TextureStorage3D(atlas.Handle, 1, InternalFormatOf(source), width, height, depth);

        atlas.SetFiltering(TextureMinFilter.Linear, TextureMagFilter.Linear);
        atlas.SetWrapMode(TextureWrapMode.ClampToEdge);

#if DEBUG
        atlas.SetLabel(label);
#endif

        return atlas;
    }

    private static void CopyIrradiance(RenderTexture source, RenderTexture atlas, LightProbeAtlasSlot slot, int atlasDepth)
    {
        for (var slice = 0; slice < IrradianceSlices; slice++)
        {
            GL.CopyImageSubData(
                source.Handle, ImageTarget.Texture3D, 0, 0, 0, slice * slot.Depth,
                atlas.Handle, ImageTarget.Texture3D, 0, slot.X, slot.Y, slice * atlasDepth + slot.Z,
                slot.Width, slot.Height, slot.Depth);
        }
    }

    private static void Copy(RenderTexture? source, RenderTexture? atlas, LightProbeAtlasSlot slot)
    {
        if (source == null || atlas == null)
        {
            return;
        }

        GL.CopyImageSubData(
            source.Handle, ImageTarget.Texture3D, 0, 0, 0, 0,
            atlas.Handle, ImageTarget.Texture3D, 0, slot.X, slot.Y, slot.Z,
            slot.Width, slot.Height, slot.Depth);
    }

    private static void Release(SceneLightProbe probe, MaterialLoader materialLoader)
    {
        var paths = probe.TexturePaths;

        Evict(paths.Irradiance, srgbRead: true);
        Evict(paths.Indices, srgbRead: false);
        Evict(paths.Scalars, srgbRead: false);
        Evict(paths.Shadows, srgbRead: false);

        probe.Irradiance = null;
        probe.DirectLightIndices = null;
        probe.DirectLightScalars = null;
        probe.DirectLightShadows = null;
        probe.TexturePaths = default;

        void Evict(string? path, bool srgbRead)
        {
            if (path != null)
            {
                materialLoader.EvictTexture(path, srgbRead);
            }
        }
    }
}
