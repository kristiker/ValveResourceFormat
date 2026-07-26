#version 460

#include "common/utils.slang"
#include "common/ViewConstants.slang"
#include "common/fog.slang"

// Render modes -- Switched on/off by code
#define renderMode_Color 0

// ParticleBlendMode_t, mirroring Renderer/ParticleRenderer/Enums/ParticleBlendMode.cs. See the same
// uniform in particle_sprite.frag.slang: a runtime branch keeps this at one compiled variant.
#define PARTICLE_OUTPUT_BLEND_MODE_ADD   1
#define PARTICLE_OUTPUT_BLEND_MODE_MOD2X 5
uniform int uBlendMode = 0; // PARTICLE_OUTPUT_BLEND_MODE_ALPHA

uniform vec3 uColor;
uniform sampler2D uTexture;

in vec2 uv;
in vec3 vFragPosition;

uniform vec2 uUvOffset;
uniform vec2 uUvScale;

uniform float uAlpha;
uniform float uOverbrightFactor;

out vec4 fragColor;

void main(void) {
    vec4 color = texture(uTexture, uUvOffset + uv * uUvScale);

    vec3 finalColor = uColor * color.rgb;
    float blendingFactor = uOverbrightFactor * GetLuma(finalColor.rgb);

    // MOD2X composites like ADD -- see the note in particle_sprite.frag.slang.
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

    fragColor = vec4(finalColor, uAlpha * color.a * blendingFactor);

    if (g_iRenderMode == renderMode_Color)
    {
        fragColor = vec4(finalColor, 1.0);
    }
}
