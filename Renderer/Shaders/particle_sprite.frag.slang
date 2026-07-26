#version 460

#include "common/utils.slang"
#include "common/ViewConstants.slang"
#include "common/fog.slang"

// Render modes -- Switched on/off by code
#define renderMode_Color 0

// ParticleBlendMode_t, mirroring Renderer/ParticleRenderer/Enums/ParticleBlendMode.cs. Only ALPHA, ADD
// and MOD2X get distinct treatment here; the remaining modes fall back to ALPHA, exactly as they do on
// the CPU side when picking the GL blend function. This is a runtime uniform rather than a static combo
// because the branch is uniform across the draw and the real spritecard drives it from a control word
// too -- one compiled variant instead of three.
#define PARTICLE_OUTPUT_BLEND_MODE_ADD   1
#define PARTICLE_OUTPUT_BLEND_MODE_MOD2X 5
uniform int uBlendMode = 0; // PARTICLE_OUTPUT_BLEND_MODE_ALPHA

uniform sampler2D uTexture;
uniform float uOverbrightFactor;
uniform float uColorFactor = 1.0; // m_flDiffuseAmount + m_flSelfIllumAmount
// m_flSourceAlphaValueToMapToZero and m_flSourceAlphaValueToMapToOne. The real spritecard smoothsteps
// between the two bounds rather than applying a linear scale and bias, and skips the remap unless the
// range increases -- so x >= y disables it, which is the default here.
uniform vec2 uAlphaRemapRange = vec2(1.0, 0.0);

// m_bOutline: ramps an overlay colour in over a band of alpha values, so that the soft edge of a sprite
// reads as an outline. uOutlineRanges packs (m_flOutlineStart0, m_flOutlineEnd0, m_flOutlineStart1,
// m_flOutlineEnd1): the overlay feathers in from Start0, is solid between Start1 and End0, and feathers
// back out by End1.
uniform int uOutline = 0;
uniform vec4 uOutlineColor = vec4(1.0);
uniform vec4 uOutlineRanges = vec4(0.5, 0.7, 0.6, 0.8);

// The spritecard layer chain. Each texture layer is folded into an accumulator that starts at
// vec4(1.0): the layer is first reduced to a masked colour/alpha pair by SpriteCardTextureChannel_t,
// then combined with the accumulator by SpriteCardTextureBlendMode_t. The base texture IS layer 0, so
// with a single layer MULTIPLY collapses to a straight sample -- which is why the mask modes read as
// plain channel swizzles here even though they are really binary operators.
//
// The *_RGBALPHA and *MASK modes do not need a second texture: their "other" operand is the
// accumulated result of the previous layers.
#define SPRITECARD_TEXTURE_CHANNEL_MIX_RGB            0
#define SPRITECARD_TEXTURE_CHANNEL_MIX_RGBA           1
#define SPRITECARD_TEXTURE_CHANNEL_MIX_A              2
#define SPRITECARD_TEXTURE_CHANNEL_MIX_RGB_A          3
#define SPRITECARD_TEXTURE_CHANNEL_MIX_RGB_ALPHAMASK  4
#define SPRITECARD_TEXTURE_CHANNEL_MIX_RGB_RGBMASK    5
#define SPRITECARD_TEXTURE_CHANNEL_MIX_RGBA_RGBALPHA  6
#define SPRITECARD_TEXTURE_CHANNEL_MIX_A_RGBALPHA     7
#define SPRITECARD_TEXTURE_CHANNEL_MIX_RGB_A_RGBALPHA 8
#define SPRITECARD_TEXTURE_CHANNEL_MIX_R              9
#define SPRITECARD_TEXTURE_CHANNEL_MIX_G              10
#define SPRITECARD_TEXTURE_CHANNEL_MIX_B              11
#define SPRITECARD_TEXTURE_CHANNEL_MIX_RALPHA         12
#define SPRITECARD_TEXTURE_CHANNEL_MIX_GALPHA         13
#define SPRITECARD_TEXTURE_CHANNEL_MIX_BALPHA         14
uniform int uTextureChannels = 1; // MIX_RGBA

#define SPRITECARD_TEXTURE_BLEND_MULTIPLY   0
#define SPRITECARD_TEXTURE_BLEND_MULTIPLY2X 1
#define SPRITECARD_TEXTURE_BLEND_REPLACE    2
#define SPRITECARD_TEXTURE_BLEND_ADD        3
#define SPRITECARD_TEXTURE_BLEND_SUBTRACT   4
#define SPRITECARD_TEXTURE_BLEND_AVERAGE    5
#define SPRITECARD_TEXTURE_BLEND_LUMINANCE  6
// Left at MULTIPLY until the per-layer texture inputs are parsed; with one layer that is the identity.
uniform int uTextureBlendMode = 0; // MULTIPLY

// The mask modes use Rec.601 weights, while the LUMINANCE blend below uses Rec.709 (GetLuma).
const vec3 LUMA_601 = vec3(0.299, 0.587, 0.114);

in vec2 vTexCoordOut;
in vec4 vColor;
in vec3 vFragPosition;

out vec4 fragColor;

// Reduce (accumulator, layer) to a single colour/alpha pair. SUBTRACT zeroes the accumulator's
// contribution in the modes that would otherwise pass it straight through, so that subtracting a
// colour-only layer does not also subtract the alpha it never supplied.
vec4 ApplyChannelMask(vec4 accum, vec4 layer, int maskMode, float sub)
{
    switch (maskMode)
    {
        case SPRITECARD_TEXTURE_CHANNEL_MIX_RGB:
            return vec4(layer.rgb, mix(accum.a, 0.0, sub));
        case SPRITECARD_TEXTURE_CHANNEL_MIX_A:
            return vec4(mix(accum.rgb, vec3(0.0), sub), layer.a);
        case SPRITECARD_TEXTURE_CHANNEL_MIX_RGB_A:
            return vec4(layer.rgb, accum.a * layer.a);
        case SPRITECARD_TEXTURE_CHANNEL_MIX_RGB_ALPHAMASK:
            return vec4(mix(accum.rgb, layer.rgb, layer.a), accum.a);
        case SPRITECARD_TEXTURE_CHANNEL_MIX_RGB_RGBMASK:
            return vec4(mix(accum.rgb, layer.rgb, dot(layer.rgb, LUMA_601)), accum.a);
        case SPRITECARD_TEXTURE_CHANNEL_MIX_RGBA_RGBALPHA:
            return vec4(layer.rgb, dot(layer.rgb, LUMA_601));
        case SPRITECARD_TEXTURE_CHANNEL_MIX_A_RGBALPHA:
            return vec4(mix(accum.rgb, vec3(0.0), sub), dot(layer.rgb, LUMA_601));
        case SPRITECARD_TEXTURE_CHANNEL_MIX_RGB_A_RGBALPHA:
            return vec4(accum.rgb, accum.a * dot(layer.rgb, LUMA_601));

        // The single-channel modes keep their own channel and drop the rest under SUBTRACT.
        case SPRITECARD_TEXTURE_CHANNEL_MIX_R:
            return vec4(layer.r, mix(vec3(layer.r, layer.r, accum.a), vec3(0.0), sub));
        case SPRITECARD_TEXTURE_CHANNEL_MIX_G:
            return vec4(mix(layer.g, 0.0, sub), layer.g, mix(vec2(layer.g, accum.a), vec2(0.0), sub));
        case SPRITECARD_TEXTURE_CHANNEL_MIX_B:
            return vec4(mix(vec2(layer.b), vec2(0.0), sub), layer.b, mix(accum.a, 0.0, sub));

        // ...and the *ALPHA variants broadcast their channel into the alpha as well.
        case SPRITECARD_TEXTURE_CHANNEL_MIX_RALPHA:
            return vec4(layer.r, mix(vec2(layer.r), vec2(0.0), sub), layer.r);
        case SPRITECARD_TEXTURE_CHANNEL_MIX_GALPHA:
            return vec4(mix(layer.g, 0.0, sub), layer.g, mix(layer.g, 0.0, sub), layer.g);
        case SPRITECARD_TEXTURE_CHANNEL_MIX_BALPHA:
            return vec4(mix(vec2(layer.b), vec2(0.0), sub), layer.b, layer.b);
    }

    return layer; // MIX_RGBA, and anything unrecognised
}

// Combine the masked layer into the accumulator.
vec4 ApplyLayerBlend(vec4 accum, vec4 masked, int blendMode)
{
    switch (blendMode)
    {
        case SPRITECARD_TEXTURE_BLEND_MULTIPLY2X: return accum * masked * 2.0;
        case SPRITECARD_TEXTURE_BLEND_REPLACE:    return masked;
        case SPRITECARD_TEXTURE_BLEND_ADD:        return accum + masked;
        case SPRITECARD_TEXTURE_BLEND_SUBTRACT:   return accum - masked;
        case SPRITECARD_TEXTURE_BLEND_AVERAGE:    return (accum + masked) * 0.5;
        case SPRITECARD_TEXTURE_BLEND_LUMINANCE:  return vec4(accum.rgb * GetLuma(masked.rgb), masked.a);
    }

    return accum * masked; // MULTIPLY, and anything unrecognised
}

void main(void) {
    // Layer 0 is the base texture, folded into an accumulator of vec4(1.0). Extra layers will repeat
    // this block with their own mask, blend and uv.
    vec4 accum = vec4(1.0);

    float sub = float(uTextureBlendMode == SPRITECARD_TEXTURE_BLEND_SUBTRACT);
    vec4 layer = texture(uTexture, vTexCoordOut);
    vec4 masked = ApplyChannelMask(accum, layer, uTextureChannels, sub);
    accum = max(ApplyLayerBlend(accum, masked, uTextureBlendMode), vec4(0.0));

    vec4 color = accum;

    vec3 finalColor = vColor.rgb * color.rgb;

    // Texture alpha remap.
    float alpha = color.a;

    if (uAlphaRemapRange.x < uAlphaRemapRange.y)
    {
        alpha = smoothstep(uAlphaRemapRange.x, uAlphaRemapRange.y, alpha);
    }

    // Overlay the outline colour across its alpha band. Solid between Start1 and End0, feathered outside;
    // the second smoothstep runs with its edges swapped so that it falls rather than rises.
    if (uOutline != 0 && alpha >= uOutlineRanges.x && alpha <= uOutlineRanges.w)
    {
        float outlineRamp = alpha <= uOutlineRanges.z
            ? smoothstep(uOutlineRanges.x, uOutlineRanges.z, alpha)
            : smoothstep(uOutlineRanges.w, uOutlineRanges.y, alpha);

        finalColor = mix(finalColor, uOutlineColor.rgb, outlineRamp);
        alpha = mix(alpha, uOutlineColor.a, outlineRamp);
    }

    // spritecard shading: brightness from diffuse+selfillum, saturated before the alpha blend. Applied
    // after the outline so the overlay colour is lit the same way the sample is.
    finalColor = saturate(finalColor * (uOverbrightFactor * uColorFactor));

    alpha *= vColor.a;

    bool isAdditive = uBlendMode == PARTICLE_OUTPUT_BLEND_MODE_ADD;
    bool isMod2x = uBlendMode == PARTICLE_OUTPUT_BLEND_MODE_MOD2X;

    // Additive and mod2x contributions fade with fog density instead of tinting toward the fog color.
    if (isMod2x)
    {
        alpha *= GetFogAttenuation(vFragPosition); // fades the output toward the blend-neutral 0.5
    }
    else if (isAdditive)
    {
        finalColor *= GetFogAttenuation(vFragPosition);
    }
    else
    {
        ApplyFog(finalColor, vFragPosition);
    }

    if (isMod2x)
    {
        fragColor = vec4(mix(vec3(0.5), finalColor, vec3(alpha)), alpha);
    }
    else
    {
        // Premultiplied alpha; additive keeps the color term and zeroes the blend weight.
        fragColor = vec4(finalColor * alpha, isAdditive ? 0.0 : alpha);
    }

    if (g_iRenderMode == renderMode_Color)
    {
        fragColor = vec4(finalColor, 1.0);
    }
}
