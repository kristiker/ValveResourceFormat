namespace ValveResourceFormat.CompiledShader;

/// <summary>
/// Known member layouts of Valve's external constant buffers, recovered from DXBC reflection of the
/// DirectX builds of the same shaders. VCS version 71 dropped the external constant buffer descriptions,
/// so these tables are the only source of member names (and CPU-side offsets) for those buffers.
/// </summary>
/// <remarks>
/// Recovered from <c>csgo_effects_pc_50_vs.vcs</c> (Counter-Strike 2, July 2026) with
/// <c>--shader_cbuffers</c>. Register offsets agree between the DirectX and Vulkan builds of a shader
/// because both compile the same source; only bind points differ. Other games and engine branches may
/// lay these buffers out differently, but the fallback is only consulted for files that lack the
/// external constant buffer descriptions, which today means CS2-era VCS 71.
/// </remarks>
public static class KnownBufferLayouts
{
    /// <summary>A member of a known constant buffer layout.</summary>
    /// <param name="Name">The HLSL variable name.</param>
    /// <param name="ByteOffset">The byte offset within the buffer.</param>
    /// <param name="SizeBytes">The size of the variable in bytes.</param>
    public readonly record struct BufferMember(string Name, int ByteOffset, int SizeBytes);

    /// <summary>
    /// Gets the known buffer layouts keyed by buffer name. Members are ordered by offset.
    /// </summary>
    public static IReadOnlyDictionary<string, BufferMember[]> Layouts => LayoutsByBuffer;

    private static readonly Dictionary<string, BufferMember[]> LayoutsByBuffer = new(StringComparer.Ordinal)
    {
        ["PerViewConstantBuffer_t"] =
        [
            new("g_matWorldToProjection", 0, 64),
            new("g_matProjectionToWorld", 64, 64),
            new("g_matWorldToView", 128, 64),
            new("g_matViewToProjection", 192, 64),
            new("g_vInvProjLowerRight2x2", 256, 16),
            new("g_vClipPlane0", 272, 16),
            new("g_flToneMapScalarLinear", 288, 4),
            new("g_flInvToneMapScalarLinear", 292, 4),
            new("g_flRealTime", 296, 4),
            new("g_flGameTime", 300, 4),
            new("g_vViewportToGBufferRatio", 304, 8),
            new("g_fInvViewportZRange", 312, 4),
            new("g_fMinViewportZScaled", 316, 4),
            new("g_vViewportOffset", 320, 8),
            new("g_vViewportSize", 328, 8),
            new("g_vInvViewportSize", 336, 8),
            new("g_vRenderTargetSize", 344, 8),
            new("g_vInvGBufferSize", 352, 16),
            new("g_flViewportMinZ", 368, 4),
            new("g_flViewportMaxZ", 372, 4),
            new("g_flNearPlane", 376, 4),
            new("g_flFarPlane", 380, 4),
            new("g_vWorldToCameraOffset", 384, 16),
            new("g_vCameraAngles", 400, 16),
            new("g_vCameraPositionWs", 416, 12),
            new("_PerViewPad0", 428, 4),
            new("g_vCameraUpDirWs", 432, 12),
            new("_PerViewPad1", 444, 4),
            new("g_vCameraDirWs", 448, 12),
            new("g_flInvMSAASampleCount", 460, 4),
            new("g_vDepthPsToVsConversion", 464, 12),
            new("g_nMSAASampleCount", 476, 4),
            new("g_vMorphTextureAtlasSize", 480, 8),
            new("g_tCompositeMorphAtlasTextureIndex", 488, 4),
            new("_PerViewPad4", 492, 4),
            new("g_vFogColor", 496, 12),
            new("g_flNegFogStartOverFogRange", 508, 4),
            new("g_flInvFogRange", 512, 4),
            new("g_flFogMaxDensity", 516, 4),
            new("g_flFogExponent", 520, 4),
            new("g_flFogBlendToBackground", 524, 4),
            new("g_vFrameBufferCopyInvSizeAndUvScale", 528, 16),
            new("g_vPrevWorldToCameraOffset", 544, 16),
            new("g_matPrevWorldToProjection", 560, 64),
            new("g_vWireframeColor", 624, 16),
        ],
        ["PerViewConstantBufferCsgo_t"] =
        [
            new("g_bFogTypeEnabled", 0, 16),
            new("g_bOtherFxEnabled", 16, 16),
            new("g_bOtherEnabled2", 32, 16),
            new("g_bOtherEnabled3", 48, 16),
            new("g_tBlueNoiseTextureIndex", 64, 4),
            new("g_tBRDFLookupTextureIndex", 68, 4),
            new("g_tCubemapFogTextureIndex", 72, 4),
            new("g_tDynamicAmbientOcclusionTextureIndex", 76, 4),
            new("g_tDynamicAmbientOcclusionDepthIndex", 80, 4),
            new("g_tSSAOIndex", 84, 4),
            new("g_tParticleShadowBufferIndex", 88, 4),
            new("g_tZeroth_MomentIndex", 92, 4),
            new("g_tMomentsIndex", 96, 4),
            new("g_tExtra_MomentIndex", 100, 4),
            new("g_tLowShaderQualityFallbackCubemap", 104, 4),
            new("g_tUnused0", 108, 4),
            new("g_tWetnessDropletsTextureIndex", 112, 4),
            new("g_tWetnessWavesTextureIndex", 116, 4),
            new("g_tWetnessFlowingTextureIndex", 120, 4),
            new("g_tSnowSparkleTextureIndex", 124, 4),
            new("g_tRainRipplesTextureIndex", 128, 4),
            new("g_tUnusedTextureIndex1", 132, 4),
            new("g_tUnusedTextureIndex2", 136, 4),
            new("g_tUnusedTextureIndex3", 140, 4),
            new("g_flEnvRainStrength", 144, 4),
            new("g_flEnvPuddleRippleStrength", 148, 4),
            new("g_flEnvPuddleRippleDirection", 152, 4),
            new("g_flEnvWetnessCoverage", 156, 4),
            new("g_flEnvWetnessDryingAmount", 160, 4),
            new("g_flUnusedEnvironmentEffect1", 164, 4),
            new("g_flUnusedEnvironmentEffect2", 168, 4),
            new("g_flUnusedEnvironmentEffect3", 172, 4),
            new("g_vBlueNoiseMask", 176, 8),
            new("g_tUnused1", 184, 4),
            new("g_tUnused2", 188, 4),
            new("g_matPrimaryViewWorldToProjection", 192, 64),
            new("g_vAoProxyDepthInvTextureSize", 256, 8),
            new("g_flAoProxyDownres", 264, 4),
            new("g_flMinSpecLightmapSize", 268, 4),
            new("g_vWindDirection", 272, 16),
            new("g_vWindStrengthFreqMulHighStrength", 288, 16),
            new("g_vInteractionProjectionOrigin", 304, 16),
            new("g_vInteractionVolumeInvExtents", 320, 16),
            new("g_vInteractionTriggerVolumeInvMins", 336, 16),
            new("g_vInteractionTriggerVolumeWorldToVolumeScale", 352, 16),
            new("g_vGradientFogBiasAndScale", 368, 16),
            new("m_vGradientFogExponents", 384, 16),
            new("g_vGradientFogColor_Opacity", 400, 16),
            new("g_vGradientFogCullingParams", 416, 16),
            new("g_vCubeFog_Offset_Scale_Bias_Exponent", 432, 16),
            new("g_vCubeFog_Height_Offset_Scale_Exponent_Log2Mip", 448, 16),
            new("g_matvCubeFogSkyWsToOs", 464, 64),
            new("g_vCubeFogCullingParams_MaxOpacity", 528, 16),
            new("g_vCubeFog_ExposureBias", 544, 16),
            new("g_vHighPrecisionLightingOffsetWs", 560, 16),
            new("g_flEnvMapPositionBias", 576, 4),
            new("g_flProbeClampPlaneDistance", 580, 4),
            new("g_flScopeMagnification", 584, 4),
            new("g_flMixedResolutionViewportScale", 588, 4),
            new("g_flMBOIT_Overestimation", 592, 4),
            new("g_flMBOIT_Bias", 596, 4),
            new("g_flMBOIT_Scale", 600, 4),
            new("g_flMBOIT_LogNear", 604, 4),
            new("g_flMBOIT_LogFar", 608, 4),
            new("g_bIsToolsView", 612, 4),
            new("g_flShaderFeatureTestValue", 616, 4),
            new("g_bShaderPerfTest", 620, 4),
            new("g_flToolsVisCubemapReflectionRoughness", 624, 4),
            new("g_flCablePixelRadiusScale", 628, 4),
            new("g_flBeginMixingRoughness", 632, 4),
            new("_pad0", 636, 4),
            new("g_vPlayerVisibilityParams", 640, 16),
        ],
    };

    /// <summary>
    /// Gets the member name of a known buffer at the given byte offset, or an empty string when either
    /// the buffer or the offset is unknown. Offsets inside a member (e.g. a column of a matrix or a
    /// component of a vector) resolve to that member.
    /// </summary>
    public static string GetMemberName(string bufferName, int byteOffset)
    {
        if (!LayoutsByBuffer.TryGetValue(bufferName, out var members))
        {
            return string.Empty;
        }

        foreach (var member in members)
        {
            if (byteOffset >= member.ByteOffset && byteOffset < member.ByteOffset + member.SizeBytes)
            {
                return member.Name;
            }
        }

        return string.Empty;
    }
}
