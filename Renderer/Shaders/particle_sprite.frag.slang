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
uniform vec2 uAlphaRemapScaleBias = vec2(1.0, 0.0);

// SpriteCardTextureChannel_t: how the sampled texel maps into colour/alpha. Defaults to MIX_RGBA
// (colour from RGB, alpha from A). Colour-only modes take colour from a channel (or RGB) and drop the
// texture alpha; coverage-only modes take colour from the particle and pull alpha from one channel. The
// dual-texture MASK / *_RGBALPHA modes need a second texture the renderer does not bind, so they fall
// through to straight RGBA.
#define SPRITECARD_TEXTURE_CHANNEL_MIX_RGB    0
#define SPRITECARD_TEXTURE_CHANNEL_MIX_A      2
#define SPRITECARD_TEXTURE_CHANNEL_MIX_R      9
#define SPRITECARD_TEXTURE_CHANNEL_MIX_G      10
#define SPRITECARD_TEXTURE_CHANNEL_MIX_B      11
#define SPRITECARD_TEXTURE_CHANNEL_MIX_RALPHA 12
#define SPRITECARD_TEXTURE_CHANNEL_MIX_GALPHA 13
#define SPRITECARD_TEXTURE_CHANNEL_MIX_BALPHA 14
uniform int uTextureChannels = 1; // MIX_RGBA

in vec2 vTexCoordOut;
in vec4 vColor;
in vec3 vFragPosition;

out vec4 fragColor;

void main(void) {
    vec4 color = texture(uTexture, vTexCoordOut);

    switch (uTextureChannels)
    {
        // todo: is this right?
        // todo: support more modes
        case SPRITECARD_TEXTURE_CHANNEL_MIX_RGB: color = vec4(color.rgb, 1.0); break;
        case SPRITECARD_TEXTURE_CHANNEL_MIX_R:   color = vec4(color.rrr, 1.0); break;
        case SPRITECARD_TEXTURE_CHANNEL_MIX_G:   color = vec4(color.ggg, 1.0); break;
        case SPRITECARD_TEXTURE_CHANNEL_MIX_B:   color = vec4(color.bbb, 1.0); break;
        case SPRITECARD_TEXTURE_CHANNEL_MIX_A:      color = vec4(1.0, 1.0, 1.0, color.a); break;
        case SPRITECARD_TEXTURE_CHANNEL_MIX_RALPHA: color = vec4(1.0, 1.0, 1.0, color.r); break;
        case SPRITECARD_TEXTURE_CHANNEL_MIX_GALPHA: color = vec4(1.0, 1.0, 1.0, color.g); break;
        case SPRITECARD_TEXTURE_CHANNEL_MIX_BALPHA: color = vec4(1.0, 1.0, 1.0, color.b); break;
        default: break;
    }

    // spritecard shading: brightness from diffuse+selfillum, saturated before the alpha blend.
    vec3 finalColor = vColor.rgb * color.rgb;
    finalColor = saturate(finalColor * (uOverbrightFactor * uColorFactor));

    // Texture alpha remap, then the per-particle alpha.
    float alpha = saturate(color.a * uAlphaRemapScaleBias.x + uAlphaRemapScaleBias.y);
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
