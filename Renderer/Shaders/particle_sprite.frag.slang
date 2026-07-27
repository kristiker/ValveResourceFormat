#version 460

#include "common/utils.slang"
#include "common/ViewConstants.slang"
#include "common/fog.slang"
#include "common/depth.slang"

// m_nFeatheringMode / m_flFeatheringMinDist / m_flFeatheringMaxDist. Fades the card out as it approaches
// whatever the opaque depth buffer holds, so an intersection with geometry reads as a soft edge instead
// of a hard cut line. Zero range disables it, which is also what an unavailable depth copy resolves to.
uniform vec2 uFeatheringRange = vec2(0.0);

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

#include "common/particle_spritecard.slang"

// One sampler per texture layer. Layer 0 is the base texture and keeps the plain name.
uniform sampler2D uTexture;
uniform sampler2D uTextureLayer1;
uniform sampler2D uTextureLayer2;
uniform sampler2D uTextureLayer3;
uniform sampler2D uTextureLayer4;

uniform float uOverbrightFactor;
uniform float uColorFactor = 1.0; // m_flDiffuseAmount + m_flSelfIllumAmount
uniform float uDesaturation = 0.0; // m_flDesaturation
// m_bSaturateColorPreAlphaBlend, which defaults on. A good deal of shipped content turns it off to let
// the brightness run past 1 into the blend.
uniform int uSaturateColorPreAlphaBlend = 1;

// The control point named by m_nHSVShiftControlPoint supplies a hue offset in turns plus saturation and
// value scales. Disabled when the offset is 0 and both scales are 1, which is what an absent control
// point resolves to.
uniform vec3 uHsvShift = vec3(0.0, 1.0, 1.0);
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
// Per-layer mask mode, blend mode and strength (m_nTextureChannels, m_nTextureBlendMode,
// m_flTextureBlend). Every entry below uLayerCount is written by the renderer each draw.
uniform int uLayerCount = 1;
uniform int uLayerChannels[MAX_TEXTURE_LAYERS];
uniform int uLayerBlendMode[MAX_TEXTURE_LAYERS];
uniform float uLayerBlend[MAX_TEXTURE_LAYERS];

#define SPRITECARD_TEXTURE_BLEND_MULTIPLY   0
#define SPRITECARD_TEXTURE_BLEND_MOD2X 1
#define SPRITECARD_TEXTURE_BLEND_REPLACE    2
#define SPRITECARD_TEXTURE_BLEND_ADD        3
#define SPRITECARD_TEXTURE_BLEND_SUBTRACT   4
#define SPRITECARD_TEXTURE_BLEND_AVERAGE    5
#define SPRITECARD_TEXTURE_BLEND_LUMINANCE  6

// The mask modes use Rec.601 weights, while the LUMINANCE blend below uses Rec.709 (GetLuma).
const vec3 LUMA_601 = vec3(0.299, 0.587, 0.114);

// m_bBlendFramesSeq0 / m_bMaxLuminanceBlendingSequence0. Gated on a uniform rather than on vFrameBlend
// so that the second fetch sits in draw-uniform control flow.
uniform int uBlendFrames = 0;
uniform int uMaxLuminanceFrameBlend = 0;

in vec2 vTexCoordOut;
in vec2 vTexCoordNextFrameOut;
in float vFrameBlend;
in vec4 vColor;
in vec3 vFragPosition;
in vec4 vLayerUv[MAX_TEXTURE_LAYERS];

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

// Shifts the hue and scales saturation and value, going through HSV and back. Ported from the spritecard
// pixel shader, which rolls its own conversion rather than using a library one.
vec3 ApplyHsvShift(vec3 rgb)
{
    float valueMax = max(rgb.r, max(rgb.g, rgb.b));
    float chroma = valueMax - min(rgb.r, min(rgb.g, rgb.b));

    vec3 hsv = vec3(0.0, 0.0, valueMax);

    if (chroma != 0.0)
    {
        vec3 distance = (vec3(valueMax) - rgb) / vec3(chroma);
        vec3 delta = distance - distance.zxy;

        // Which sixth of the hue wheel the largest channel puts us in.
        if (rgb.r >= valueMax)      { hsv = vec3(delta.z,       chroma / valueMax, valueMax); }
        else if (rgb.g >= valueMax) { hsv = vec3(delta.x + 2.0, chroma / valueMax, valueMax); }
        else                        { hsv = vec3(delta.y + 4.0, chroma / valueMax, valueMax); }

        hsv.x = fract(hsv.x * (1.0 / 6.0));
    }

    float hue = fract(hsv.x + uHsvShift.x);
    vec2 satVal = saturate(hsv.yz * uHsvShift.yz);

    if (satVal.x == 0.0)
    {
        return vec3(satVal.y);
    }

    float sector = hue * 6.0;
    float index = floor(sector);
    float f = sector - index;

    float p = satVal.y * (1.0 - satVal.x);
    float q = satVal.y * (1.0 - (satVal.x * f));
    float t = satVal.y * (1.0 - (satVal.x * (1.0 - f)));

    if (index == 0.0) { return vec3(satVal.y, t, p); }
    if (index == 1.0) { return vec3(q, satVal.y, p); }
    if (index == 2.0) { return vec3(p, satVal.y, t); }
    if (index == 3.0) { return vec3(p, q, satVal.y); }
    if (index == 4.0) { return vec3(t, p, satVal.y); }

    return vec3(satVal.y, p, q);
}

// GLSL cannot return an opaque texture, so the per-layer choice happens at the fetch itself.
vec4 SampleLayer(int layer, vec2 uv)
{
    switch (layer)
    {
        case 1: return texture(uTextureLayer1, uv);
        case 2: return texture(uTextureLayer2, uv);
        case 3: return texture(uTextureLayer3, uv);
        case 4: return texture(uTextureLayer4, uv);
    }

    return texture(uTexture, uv);
}

// SpriteCardTextureType_t, for the values that are effect modes. NORMALMAP and ANIMMOTIONVEC never reach
// here: the renderer keeps them out of the colour chain entirely.
#define SPRITECARD_TEXTURE_DIFFUSE            0
#define SPRITECARD_TEXTURE_ZOOM               1
#define SPRITECARD_TEXTURE_1D_COLOR_LOOKUP    2
#define SPRITECARD_TEXTURE_UVDISTORTION       3
#define SPRITECARD_TEXTURE_UVDISTORTION_ZOOM  4
uniform int uLayerEffectMode[MAX_TEXTURE_LAYERS];
uniform float uLayerZoomScale[MAX_TEXTURE_LAYERS];   // m_flZoomScale
uniform float uLayerDistortion[MAX_TEXTURE_LAYERS];  // m_flDistortion

// Two lookups the reference keeps as constant tables indexed by mask mode. The first says whether an
// effect's own alpha replaces the one underneath it; the second drives the gradient ramp, with .x
// premultiplying the ramp's source colour by its alpha and .y switching the lookup to use alpha as luma.
bool MaskKeepsEffectAlpha(int maskMode)
{
    return maskMode != SPRITECARD_TEXTURE_CHANNEL_MIX_RGB_A
        && maskMode != SPRITECARD_TEXTURE_CHANNEL_MIX_RGB_A_RGBALPHA;
}

const vec2 MASK_RAMP_FLAGS[15] = vec2[](
    vec2(1.0, 0.0), vec2(0.0, 0.0), vec2(0.0, 1.0), vec2(0.0, 0.0), vec2(1.0, 0.0),
    vec2(1.0, 0.0), vec2(0.0, 0.0), vec2(0.0, 1.0), vec2(0.0, 0.0), vec2(1.0, 0.0),
    vec2(1.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 0.0), vec2(0.0, 0.0), vec2(0.0, 0.0));

// Two samples of the layer's own uv, scaled about the centre by a time-driven factor and crossfaded. The
// 0.4 in the denominator eases the phase so the blur accelerates rather than sweeping linearly.
vec4 SampleZoomBlur(int layer, vec4 layerUv, vec2 uv)
{
    float phase = fract(uLayerZoomScale[layer] * layerUv.z);
    float zoom = phase / ((-0.39999997615814208984375 * (1.0 - phase)) + 1.0);

    vec2 centred = uv - vec2(0.5);
    vec2 wide = (centred * mix(4.0, 1.0, zoom)) + vec2(0.5);
    vec2 tight = (centred * mix(1.0, 0.0625, zoom)) + vec2(0.5);

    return mix(SampleLayer(layer, tight), SampleLayer(layer, wide), zoom * zoom);
}

// The red and green of a source colour, taken to gamma space, become a 2D offset. The offset is derived
// from THIS layer but the tap comes from the PREVIOUS one, so layer 1 distorts the base texture, layer 2
// distorts layer 1, and so on. Layer 0's previous is itself.
vec4 SampleDistorted(int layer, vec4 layerUv, vec2 previousUv, vec4 source, float amount)
{
    vec2 gammaSource = SrgbLinearToGamma(vec3(source.rg, 0.0)).rg;

    vec2 offset = ((gammaSource - vec2(0.5)) * 2.0)
        * (((uLayerDistortion[layer] * layerUv.w) * 0.125) * amount);

    return SampleLayer(max(layer - 1, 0), previousUv - offset);
}

// A 1D ramp, looked up by the luminance of whatever is beneath this layer. This is the most common effect
// mode in shipped content by a wide margin.
vec4 SampleGradientRamp(int layer, vec4 layerUv, vec4 previous, int maskMode)
{
    vec2 flags = MASK_RAMP_FLAGS[maskMode];

    vec4 source = mix(mix(vec4(previous.rgb * previous.aaa, 1.0), previous, flags.x),
                      previous.aaaa, flags.y);

    vec2 rampUv = vec2((GetLuma(SrgbLinearToGamma(source.rgb)) + layerUv.x) * layerUv.z, layerUv.y);
    vec4 ramp = SampleLayer(layer, rampUv);

    // With alpha as the luma the ramp also supplies the alpha; otherwise the layer below keeps its own.
    return mix(vec4(ramp.rgb, previous.a), vec4(ramp.rgb, dot(ramp.rgb, LUMA_601)), flags.y);
}

// Combine the masked layer into the accumulator.
vec4 ApplyLayerBlend(vec4 accum, vec4 masked, int blendMode)
{
    switch (blendMode)
    {
        case SPRITECARD_TEXTURE_BLEND_MOD2X: return accum * masked * 2.0;
        case SPRITECARD_TEXTURE_BLEND_REPLACE:    return masked;
        case SPRITECARD_TEXTURE_BLEND_ADD:        return accum + masked;
        case SPRITECARD_TEXTURE_BLEND_SUBTRACT:   return accum - masked;
        case SPRITECARD_TEXTURE_BLEND_AVERAGE:    return (accum + masked) * 0.5;
        case SPRITECARD_TEXTURE_BLEND_LUMINANCE:  return vec4(accum.rgb * GetLuma(masked.rgb), masked.a);
    }

    return accum * masked; // MULTIPLY, and anything unrecognised
}

void main(void) {
    // Each layer is folded into an accumulator seeded to white. Layer 0 is the base texture, so with one
    // layer and the default MULTIPLY this collapses to a straight sample.
    vec4 accum = vec4(1.0);

    for (int i = 0; i < uLayerCount; ++i)
    {
        // Layer 0's uv went through its sheet frame in the vertex shader; the extra layers have no
        // sequence of their own, so they tile the raw texture from their own transformed corner.
        vec2 uv = i == 0 ? vTexCoordOut : vLayerUv[i].xy;
        vec2 previousUv = i == 0 ? vTexCoordOut : vLayerUv[max(i - 1, 0)].xy;

        vec4 layer = SampleLayer(i, uv);

        // The sheet sequence belongs to the base texture, so only layer 0 cross-fades frames.
        if (i == 0 && uBlendFrames != 0)
        {
            vec4 nextFrame = SampleLayer(0, vTexCoordNextFrameOut);

            if (uMaxLuminanceFrameBlend != 0)
            {
                // Weighting each frame by the square root of its luminance keeps the brighter one
                // dominant through the cross-fade, so a spark does not dim as it crosses a frame.
                float weight = (1.0 - vFrameBlend) * sqrt(max(0.001, GetLuma(layer.rgb)));
                float nextWeight = vFrameBlend * sqrt(max(0.001, GetLuma(nextFrame.rgb)));
                layer = ((layer * weight) + (nextFrame * nextWeight)) / (weight + nextWeight);
            }
            else
            {
                layer = mix(layer, nextFrame, vFrameBlend);
            }
        }

        int maskMode = uLayerChannels[i];

        // The colour an effect reads for its alpha and its ramp input is the layer's own sample on layer 0
        // but the accumulator on every later layer. On layer 0 the accumulator is still white, so this is a
        // genuine special case rather than something that folds away.
        vec4 previous = i == 0 ? layer : accum;
        vec4 effect = layer;

        switch (uLayerEffectMode[i])
        {
            case SPRITECARD_TEXTURE_ZOOM:
            {
                vec4 zoomed = SampleZoomBlur(i, vLayerUv[i], uv);
                zoomed.a = MaskKeepsEffectAlpha(maskMode) ? zoomed.a : previous.a;
                effect = zoomed;
                break;
            }

            case SPRITECARD_TEXTURE_1D_COLOR_LOOKUP:
                effect = SampleGradientRamp(i, vLayerUv[i], previous, maskMode);
                break;

            case SPRITECARD_TEXTURE_UVDISTORTION:
                // Unlike the ramp and zoom inputs, the distortion strength is always the layer's own
                // sample alpha, never the accumulator's.
                effect = SampleDistorted(i, vLayerUv[i], previousUv, layer, layer.a);
                break;

            case SPRITECARD_TEXTURE_UVDISTORTION_ZOOM:
            {
                vec4 zoomed = SampleZoomBlur(i, vLayerUv[i], uv);
                float amount = MaskKeepsEffectAlpha(maskMode) ? zoomed.a : previous.a;
                effect = SampleDistorted(i, vLayerUv[i], previousUv, zoomed, amount);
                break;
            }
        }

        int blendMode = uLayerBlendMode[i];
        float sub = float(blendMode == SPRITECARD_TEXTURE_BLEND_SUBTRACT);

        vec4 masked = ApplyChannelMask(accum, effect, maskMode, sub);
        vec4 blended = ApplyLayerBlend(accum, masked, blendMode);

        // m_flTextureBlend fades the layer's contribution back toward what was already there.
        accum = max(mix(accum, blended, uLayerBlend[i]), vec4(0.0));
    }

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

    if (uHsvShift != vec3(0.0, 1.0, 1.0))
    {
        finalColor = ApplyHsvShift(finalColor);
    }

    // Pulls the whole card toward its own luminance. Sits after the outline, so the overlay desaturates
    // with everything else, and before the brightness below.
    finalColor = mix(finalColor, vec3(GetLuma(finalColor)), uDesaturation);

    // spritecard shading: brightness from diffuse+selfillum, optionally clamped before the alpha blend.
    // Applied after the outline so the overlay colour is lit the same way the sample is.
    finalColor = finalColor * (uOverbrightFactor * uColorFactor);

    if (uSaturateColorPreAlphaBlend != 0)
    {
        finalColor = saturate(finalColor);
    }

    alpha *= vColor.a;

    if (uFeatheringRange.y > uFeatheringRange.x)
    {
        vec3 cameraRay = vFragPosition - g_vCameraPositionWs;
        vec3 scenePosition = GetWorldPositionFromDepth(ivec2(gl_FragCoord.xy), cameraRay);

        alpha *= saturate((distance(vFragPosition, scenePosition) - uFeatheringRange.x)
            / (uFeatheringRange.y - uFeatheringRange.x));
    }

    // MOD2X composites like ADD. The modulate-2x in spritecard is a texture combiner inside the layer
    // chain (SPRITECARD_TEXTURE_BLEND_MOD2X above), not a framebuffer op: every blend state the
    // shader ships with is an Add with a One destination, so it never scales what is behind it.
    bool isAdditive = uBlendMode == PARTICLE_OUTPUT_BLEND_MODE_ADD
        || uBlendMode == PARTICLE_OUTPUT_BLEND_MODE_MOD2X;

    // Additive contributions fade with fog density instead of tinting toward the fog color.
    if (isAdditive)
    {
        finalColor *= GetFogAttenuation(vFragPosition);
    }
    else
    {
        ApplyFog(finalColor, vFragPosition);
    }

    // Premultiplied alpha; additive keeps the color term and zeroes the blend weight.
    fragColor = vec4(finalColor * alpha, isAdditive ? 0.0 : alpha);

    if (g_iRenderMode == renderMode_Color)
    {
        fragColor = vec4(finalColor, 1.0);
    }
}
