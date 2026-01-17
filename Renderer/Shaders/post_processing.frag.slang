#version 460

#include "common/utils.slang"
#include "common/ViewConstants.slang"
// Bloom might eventually be supported
//uniform sampler2D g_tBloom; // t13
//uniform vec4 g_vBloomUvScaleClamp;
//uniform vec3 g_vNormalizedBloomStrengths;
//uniform vec3 g_vUnNormalizedBloomStrengths;

uniform int g_nNumSamplesMSAA = 1;
uniform bool g_bFlipY = false;
uniform float g_flResolutionScale = 1.0;
uniform bool g_bPostProcessEnabled = true;

float InvNumSamples = 1.0 / float(g_nNumSamplesMSAA);

uniform float g_flToneMapScalarLinear;
uniform float g_flExposureBiasScaleFactor;
uniform float g_flShoulderStrength;
uniform float g_flLinearStrength;
uniform float g_flLinearAngle;
uniform float g_flToeStrength;
uniform float g_flToeNum;
uniform float g_flToeDenom;
uniform float g_flWhitePointScale;

uniform float g_flColorCorrectionDefaultWeight = 1.0;
uniform vec2 g_vColorCorrectionColorRange = vec2(0.96875, 0.015625);

uniform vec4 g_vBlueNoiseDitherParams;

layout (location = 0) uniform sampler2DMS g_tColorBuffer;
layout (location = 1) uniform sampler3D g_tColorCorrection;
layout (location = 2) uniform sampler2D g_tBlueNoise;
layout (location = 3) uniform usampler2DMS g_tStencilBuffer;

layout (location = 0) out vec4 outputColor;

vec3 TonemapColor(vec3 vColor)
{
    vColor *= (g_flToneMapScalarLinear * g_flExposureBiasScaleFactor);

    // Uncharted tonemapper
    vec3 tonemapNumerator = vColor * (g_flShoulderStrength * vColor + g_flLinearStrength * g_flLinearAngle) + g_flToeNum * g_flToeStrength;
    vec3 tonemapDenominator = vColor * (g_flShoulderStrength * vColor + g_flLinearStrength) + g_flToeDenom * g_flToeStrength;

    vec3 vTonemappedColor = tonemapNumerator / tonemapDenominator - g_flToeNum / g_flToeDenom;

    // Divide by tonemapped white point (pre-calculated on CPU)
    vTonemappedColor *= g_flWhitePointScale; // This is actually 1/TonemapColor(WhitePoint)

    return vTonemappedColor;
}

vec3 ApplyColorCorrection(vec3 vColor)
{
    vec3 scaledColor = saturate(vColor) * g_vColorCorrectionColorRange.x + g_vColorCorrectionColorRange.y;
    vec3 ColorCorrectedColor = textureLod(g_tColorCorrection, scaledColor, 0.0).rgb;
    return mix(vColor, ColorCorrectedColor, g_flColorCorrectionDefaultWeight); // Probably for blending
}

vec3 DitherColor(vec3 vColor)
{
    vec2 blueNoiseCoords = gl_FragCoord.xy * g_vBlueNoiseDitherParams.z + g_vBlueNoiseDitherParams.xy;
    vec3 blueNoise = textureLod(g_tBlueNoise, blueNoiseCoords, 0.0).rgb;

    // At this point in the shader we still have floating-point precision, which we lose when we return as uint.
    // So, we apply a 1-color-value dither to break up color banding.
    // This is part of the original code and actually works extremely well.
    vec3 subPrecisionDither = (blueNoise - 0.5) * g_vBlueNoiseDitherParams.w;
    return vColor + subPrecisionDither;
}

void SampleStencilMSAA(ivec2 pixelCoords, out uint insideCount, out int totalSamples)
{
    for (int i = 0; i < g_nNumSamplesMSAA; i++)
    {
        uint stencilValue = texelFetch(g_tStencilBuffer, pixelCoords, i).r;
        insideCount += stencilValue;
    }

    totalSamples += g_nNumSamplesMSAA;
}

vec3 DrawOutline(ivec2 pixelCoords, vec3 vColor)
{
    const int width = 2;
    const int shadingRate = 1;

    int totalSamples = 0;
    uint insideCountCenter = 0;

    SampleStencilMSAA(pixelCoords, insideCountCenter, totalSamples);

    if (insideCountCenter == g_nNumSamplesMSAA)
    {
        // do not draw outline inside the object
        return vColor;
    }

    uint insideCount = insideCountCenter;
    uvec2 stencilTextureSize = textureSize(g_tStencilBuffer);

    for(int x = -width; x <= width;  x += shadingRate)
    {
        for(int y = -width; y <= width; y += shadingRate)
        {
            ivec2 sampleCoords = pixelCoords + ivec2(x, y);

            if (sampleCoords.x < 0 || sampleCoords.x >= stencilTextureSize.x ||
                sampleCoords.y < 0 || sampleCoords.y >= stencilTextureSize.y)
            {
                continue;
            }

            SampleStencilMSAA(sampleCoords, insideCount, totalSamples);
        }
    }

    if (totalSamples == 0) return vColor;

    float inside = insideCount / float(totalSamples);

    float edge = RemapVal(inside, 0.001, 0.999, 0.0, 1.0);

    if (edge < 0.0 || edge > 1.0)
    {
        return vColor; // No outline
    }

    float centerContribution = insideCountCenter * InvNumSamples;
    float outlineAlpha =  RemapValClamped(edge, 0, 0.05, 0.0, 1.0) * (1.0 - centerContribution);

    const vec3 vOutlineColor = vec3(1.0, 1.0, 0.2);

    return mix(vColor.rgb, vOutlineColor, outlineAlpha);
}

vec4 SampleColorBuffer(ivec2 pixelCoords)
{
    vec4 vColorMSAA = vec4(0.0);

    for (int i = 0; i < g_nNumSamplesMSAA; i++)
    {
        vec4 sampleColor = texelFetch(g_tColorBuffer, pixelCoords, i);
        sampleColor = clamp(sampleColor, vec4(0), vec4(65504));

        vColorMSAA += sampleColor.rgba * InvNumSamples;
    }

    return vColorMSAA;
}

vec4 SampleColorBufferBilinear(vec2 vColorUv, vec2 size)
{
    // Manual bilinear filtering
    vec2 pixelCoord = vColorUv * size - 0.5;
    ivec2 pixelCoords = ivec2(floor(pixelCoord));
    vec2 frac = fract(pixelCoord);

    vec4 sample00 = SampleColorBuffer(pixelCoords + ivec2(0, 0));
    vec4 sample10 = SampleColorBuffer(pixelCoords + ivec2(1, 0));
    vec4 sample01 = SampleColorBuffer(pixelCoords + ivec2(0, 1));
    vec4 sample11 = SampleColorBuffer(pixelCoords + ivec2(1, 1));

    return mix(
        mix(sample00, sample10, frac.x),
        mix(sample01, sample11, frac.x),
        frac.y
    );
}

vec2 CylindricalProjection(vec2 vTexCoords, float flDistance)
{
    if (flDistance == 0.0)
    {
        return vTexCoords;
    }

    vTexCoords = UnpackFromColor(vTexCoords);
    float flViewDistance = 1.0f + flDistance;
    float flHypotenuse = vTexCoords.x * vTexCoords.x + flViewDistance * flViewDistance;

    float flIntersectionDistance = vTexCoords.x * flDistance;
    float flIntersectionDiscriminator = sqrt( flHypotenuse - flIntersectionDistance * flIntersectionDistance );

    float flCylindricalDistanceNoD = ( -flIntersectionDistance * vTexCoords.x + flViewDistance * flIntersectionDiscriminator ) / flHypotenuse;
    float flCylindricalDistance = flCylindricalDistanceNoD + flDistance;

    vec2 vPosition = vTexCoords * (flCylindricalDistance / flViewDistance);
    vec2 vProjected = vPosition / flCylindricalDistanceNoD;
    
    float d = flDistance;
    float vd = 1.0 + d;
    float S;
    
    if (d >= 0.0) {
        // Positive distance: scale so edge (x=1) reaches 1.0
        float h_edge = 1.0 + vd * vd;
        float s_edge = sqrt(h_edge - d * d);
        float cylNoD_edge = (-d + vd * s_edge) / h_edge;
        float cylD_edge = cylNoD_edge + d;
        float projected_edge = (cylD_edge / vd) / cylNoD_edge;
        S = 1.0 / projected_edge;
    } else {
        // Negative distance: scale so center (x=0) reaches maximum
        float h_center = vd * vd; // x=0
        float s_center = sqrt(h_center - 0.0);
        float cylNoD_center = (vd * s_center) / h_center;
        float cylD_center = cylNoD_center + d;
        float projected_center = (cylD_center / vd) / cylNoD_center;
        S = 1.0 / projected_center;
    }

    return PackToColor(vProjected * S);
}

void main()
{
    vec2 size = textureSize(g_tColorBuffer);
    vec2 vInvTextureCoords = 1.0 / size;

    vec2 vColorUv = gl_FragCoord.xy * vInvTextureCoords * g_flResolutionScale;
    vColorUv.y = g_bFlipY ? 1.0 - vColorUv.y : vColorUv.y;
    
    //vColorUv = CylindricalProjection(vColorUv, blink() ?  0.2 : 0.0 /*mix(0.0, 0.5, sin(g_flTime * 0.5) + 0.3)*/);

    ivec2 pixelCoords = ivec2(vColorUv * size);

    vec4 vColor = blink()
        ? SampleColorBufferBilinear(vColorUv, size)
        : SampleColorBuffer(pixelCoords);

    if (g_bPostProcessEnabled)
    {
        vColor.rgb = TonemapColor(vColor.rgb);
        vColor.rgb = SrgbLinearToGamma(vColor.rgb);

        if (g_flColorCorrectionDefaultWeight > 0.0)
        {
            vColor.rgb = ApplyColorCorrection(vColor.rgb);
        }

        vColor.rgb = DitherColor(vColor.rgb);
    }

    vColor.rgb = DrawOutline(pixelCoords, vColor.rgb);
    outputColor = vColor;
}
