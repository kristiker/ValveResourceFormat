#version 460

#include "common/utils.slang"
#include "common/features.slang"
#include "pbr_features.slang"

#include "common/instancing.slang"
#include "common/animation.slang"

layout (location = 0) in vec3 vPOSITION;
layout (location = 3) in vec2 vTEXCOORD;
#include "common/compression.slang"

#if (F_SECONDARY_UV == 1)
    in vec2 vTEXCOORD1;
#endif

#if (F_VERTEX_COLOR == 1) || (F_PAINT_VERTEX_COLORS == 1)
    in vec4 vCOLOR;
#endif

#if D_BAKED_LIGHTING_FROM_LIGHTMAP == 1
    in vec2 vLightmapUV;
    out vec3 vLightmapUVScaled;
#elif D_BAKED_LIGHTING_FROM_VERTEX_STREAM == 1
    in vec4 vPerVertexLighting; // COLOR1
    out vec3 vPerVertexLightingOut;
#endif

out vec3 vFragPosition;
out vec3 vNormalOut;
out vec3 vTangentOut;
out vec3 vBitangentOut;
centroid out vec3 vCentroidNormalOut;
out vec4 vTexCoord; // UV1 in xy, UV2 in zw
centroid out vec4 vVertexColorOut;

uniform vec3 g_vColorTint = vec3(1.0); // SrgbRead(true)
uniform float g_flModelTintAmount = 1.0;

#include "common/ViewConstants.slang"
#include "common/LightingConstants.slang"


#if (D_GLITCH == 1)
    uniform float g_flGlitchStrength = 1.0;
    uniform float g_flGlitchFrameRate = 15.0;
    uniform float g_flGlitchSpeed = 1.0;

    // The whole effect advances in frames rather than continuously, which is what makes it
    // read as a broken signal instead of a smooth wobble.
    float GetGlitchTime()
    {
        return floor(g_flTime * g_flGlitchFrameRate) / (g_flGlitchFrameRate / g_flGlitchSpeed);
    }

    // One narrow band. Subtracting a threshold before the saturate keeps it narrow, and
    // squaring sharpens its shoulders.
    float GlitchBand(float x, float scale)
    {
        return pow(saturate(x) * scale, 2.0);
    }

    uniform int g_nGlitchQuantizeType = 2; // 0="Object", 1="World", 2="Clip"
    uniform float g_flGlitchQuantizeScale = 0.25;
    uniform float g_flGlitchQuantizeStrength = 0.95;
    uniform float g_flGlitchJumpStrength = 1.0;
    uniform float g_flGlitchDistortStrength = 0.4;

    // Snaps coordinates to a lattice, in whichever space g_nGlitchQuantizeType names.
    vec3 GlitchQuantize(vec3 position, int space)
    {
        if (g_nGlitchQuantizeType != space)
        {
            return position;
        }

        float scale = mix(1.0, g_flGlitchQuantizeScale, g_flGlitchStrength);
        return mix(position, round(position * scale) / scale, g_flGlitchQuantizeStrength * saturate(g_flGlitchStrength));
    }

    // Horizontal tearing plus a whole screen jump. The tearing is a stack of bands selected by
    // the clip space row, so only a few scanlines slip sideways at a time.
    vec4 ApplyVertexGlitch(vec4 clipPosition)
    {
        // Clip space quantization takes the raw scale, not the strength weighted one above.
        if (g_nGlitchQuantizeType == 2)
        {
            clipPosition.xy = mix(clipPosition.xy,
                round(clipPosition.xy * g_flGlitchQuantizeScale) / g_flGlitchQuantizeScale,
                g_flGlitchQuantizeStrength * saturate(g_flGlitchStrength));
        }

        float time = GetGlitchTime();
        float row = floor(clipPosition.y);
        float slow = sin(time * 2.0);
        float fast = sin(time * 7.2);
        float wave = sin(clipPosition.y * 0.2 + fast);
        float rowWave = clipPosition.y * fast;
        float phase = sin(time * 3.5) * 200.1;
        float threshold = 0.9 + sin(time * 7.9) * 0.1;

        float distort = GlitchBand(sin(row * sin(time * 4.2) * 1.8 + slow * 203.0) - (0.6 + slow * 0.1), 7.0) * wave * 0.52
            - GlitchBand(sin(row * sin(time * 4.5) * 1.7 + sin(time * 2.1) * 209.0) - (0.6 + sin(time * 2.3) * 0.1), 8.0) * wave * 0.52
            - GlitchBand(sin(clipPosition.y * sin(time * 6.2) * 0.042 + sin(time * 3.0) * 200.1) - (0.9 + sin(time * 9.0) * 0.1), 11.0)
            + GlitchBand(sin(rowWave * 0.52 + phase) - threshold, 7.0)
            - GlitchBand(sin(rowWave * 1.52 + phase) - threshold, 3.0);

        clipPosition.x += distort * g_flGlitchStrength * g_flGlitchDistortStrength;
        clipPosition.xy += saturate(sin(sin(vec2(2.0, 1.2) * time) * 200.0) - 0.9) * 20.0 * g_flGlitchStrength * g_flGlitchJumpStrength;

        return clipPosition;
    }
#endif

#if (F_JITTER_VERTICES > 0)
    uniform sampler2D g_tJitterMask;

    uniform vec3 g_vJitterFrequenciesA1 = vec3(0.02, 0.04, 0.08);
    uniform vec3 g_vJitterAmplitudesXA1 = vec3(4.0, 0.0, 0.0);
    uniform vec3 g_vJitterAmplitudesYA1 = vec3(0.0, 4.0, 0.0);
    uniform vec3 g_vJitterAmplitudesZA1 = vec3(0.0, 0.0, 4.0);
    uniform float g_flJitterSpeedA1 = 10.0;

    uniform vec3 g_vJitterFrequenciesB1 = vec3(0.05, 0.09, 0.17);
    uniform vec3 g_vJitterAmplitudesXB1 = vec3(0.0);
    uniform vec3 g_vJitterAmplitudesYB1 = vec3(0.0);
    uniform vec3 g_vJitterAmplitudesZB1 = vec3(0.0);
    uniform float g_flJitterSpeedB1 = 7.0;

    uniform float g_flJitterQuantizeTime1 = 0.0;
    uniform float g_flJitterQuantizeDisplacement1 = 0.0;
    uniform vec3 g_vJitterExpression = vec3(0.0);

    #if (F_SECONDARY_UV == 1)
        uniform bool g_bUseSecondaryUvForJitterMask = false;
    #endif

    // A smoothed triangle wave per axis, centred on zero. Driving the phase from the vertex
    // position keeps neighbours in step, so the mesh sways as a sheet rather than shattering.
    // A negative frequency runs its axis backwards; the magnitude still sets the wavelength.
    vec3 GetJitterWave(vec3 position, vec3 frequencies, float speed, float time)
    {
        vec3 phase = fract(position * abs(frequencies)) + sign(frequencies) * (time * speed);
        vec3 triangleWave = abs(fract(phase) * 2.0 - 1.0);

        return triangleWave * triangleWave * (3.0 - 2.0 * triangleWave) - 0.5;
    }

    // Two waves at different frequencies, each axis mixing all three of a wave's components,
    // so a curtain can be given a slow swing and a faster ripple at once.
    vec3 GetJitterDisplacement(vec3 noisePosition, vec2 maskTexCoord)
    {
        float time = (g_flJitterQuantizeTime1 != 0.0)
            ? round(g_flTime / g_flJitterQuantizeTime1) * g_flJitterQuantizeTime1
            : g_flTime;

        vec3 waveA = GetJitterWave(noisePosition, g_vJitterFrequenciesA1, g_flJitterSpeedA1, time);
        vec3 waveB = GetJitterWave(noisePosition, g_vJitterFrequenciesB1, g_flJitterSpeedB1, time);

        vec3 displacement = vec3(dot(g_vJitterAmplitudesXA1, waveA), dot(g_vJitterAmplitudesYA1, waveA), dot(g_vJitterAmplitudesZA1, waveA))
            + vec3(dot(g_vJitterAmplitudesXB1, waveB), dot(g_vJitterAmplitudesYB1, waveB), dot(g_vJitterAmplitudesZB1, waveB))
            + g_vJitterExpression;

        if (g_flJitterQuantizeDisplacement1 != 0.0)
        {
            displacement = round(displacement / g_flJitterQuantizeDisplacement1) * g_flJitterQuantizeDisplacement1;
        }

        return displacement * textureLod(g_tJitterMask, maskTexCoord, 0.0).x;
    }
#endif

void main()
{
    ObjectData_t object = GetObjectData();

    #if (D_GLITCH == 1)
        vec3 positionOs = GlitchQuantize(vPOSITION, 0);
    #else
        vec3 positionOs = vPOSITION;
    #endif

    mat4 skinTransform = object.transform * getSkinMatrix();
    vec4 fragPosition = skinTransform * vec4(positionOs, 1.0);

    #if (D_GLITCH == 1)
        fragPosition.xyz = GlitchQuantize(fragPosition.xyz, 1);
    #endif

    #if (F_JITTER_VERTICES > 0)
        #if (F_SECONDARY_UV == 1)
            vec2 jitterTexCoord = g_bUseSecondaryUvForJitterMask ? vTEXCOORD1.xy : vTEXCOORD.xy;
        #else
            vec2 jitterTexCoord = vTEXCOORD.xy;
        #endif

        #if (F_JITTER_VERTICES == 2)
            // Object space: the noise travels with the prop, so the displacement has to be
            // rotated into world space rather than added to it.
            fragPosition.xyz += mat3(skinTransform) * GetJitterDisplacement(vPOSITION, jitterTexCoord);
        #else
            fragPosition.xyz += GetJitterDisplacement(fragPosition.xyz, jitterTexCoord);
        #endif
    #endif

    gl_Position = g_matWorldToProjection * fragPosition;
    vFragPosition = fragPosition.xyz;

    #if (D_GLITCH == 1)
        gl_Position = ApplyVertexGlitch(gl_Position);
    #endif

    // Normals are deliberately not recomputed against the displacement.

    vec3 normal;
    vec4 tangent;
    GetOptionallyCompressedNormalTangent(normal, tangent);

    mat3 normalTransform = adjoint(skinTransform);
    vNormalOut = normalize(normalTransform * normal);
    vTangentOut = normalize(normalTransform * tangent.xyz);
    vBitangentOut = tangent.w * cross(vNormalOut, vTangentOut);
    vCentroidNormalOut = vNormalOut;

    #if D_BAKED_LIGHTING_FROM_LIGHTMAP == 1
        vLightmapUVScaled = vec3(vLightmapUV * g_vLightmapUvScale.xy, 0);
    #elif D_BAKED_LIGHTING_FROM_VERTEX_STREAM == 1
        vec3 Light = vPerVertexLighting.rgb * 6.0 * vPerVertexLighting.a;
        vPerVertexLightingOut = pow2(Light);
    #endif

    vVertexColorOut.rgb = mix(vec3(1.0), SrgbGammaToLinear(object.vTint.rgb) * g_vColorTint.rgb, g_flModelTintAmount);
    vVertexColorOut.a = object.vTint.a;

    #if (F_VERTEX_COLOR == 1) || (F_PAINT_VERTEX_COLORS == 1)
        vVertexColorOut *= SrgbGammaToLinear(vCOLOR);
    #endif

    #if (F_SECONDARY_UV == 1)
        vTexCoord = vec4(vTEXCOORD.xy, vTEXCOORD1.xy);
    #else
        vTexCoord = vTEXCOORD.xyxy;
    #endif
}
