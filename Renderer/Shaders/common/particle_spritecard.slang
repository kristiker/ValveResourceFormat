#version 460

#ifndef VRF_PARTICLE_SPRITECARD_INCLUDE
#define VRF_PARTICLE_SPRITECARD_INCLUDE

// Shared between the spritecard vertex and fragment stages: layer 0's uv is built up front so the sheet
// frame rectangles can be applied to it, while the extra layers are transformed at sample time.
#define MAX_TEXTURE_LAYERS 5

// TextureControls_t per layer: xy = m_flFinalTextureOffsetU/V, zw = m_flFinalTextureScaleU/V, which are
// tiling divisors rather than multipliers.
uniform vec4 uLayerUvTransform[MAX_TEXTURE_LAYERS];
uniform float uLayerUvRotation[MAX_TEXTURE_LAYERS];  // m_flFinalTextureUVRotation, radians

// The layer's m_nPerParticle* selectors, one SpriteCardPerParticleScale_t per nibble, packed the way the
// real shader packs its own field selectors. Bit 28 is m_bRandomizeOffsets.
uniform int uLayerPerParticleSelectors[MAX_TEXTURE_LAYERS];

#define PP_SLOT_SCALE      0
#define PP_SLOT_OFFSET_U   1
#define PP_SLOT_OFFSET_V   2
#define PP_SLOT_ROTATION   3
#define PP_SLOT_BLEND      4
#define PP_SLOT_ZOOM       5
#define PP_SLOT_DISTORTION 6
#define PP_RANDOMIZE_OFFSETS_BIT 28

int PerParticleSelector(int layer, int slot)
{
    return (uLayerPerParticleSelectors[layer] >> (slot * 4)) & 15;
}

bool LayerRandomizesOffsets(int layer)
{
    return ((uLayerPerParticleSelectors[layer] >> PP_RANDOMIZE_OFFSETS_BIT) & 1) != 0;
}

// Each call site passes its own multiplier so that two controls driven by RANDOM on the same particle do
// not move together.
#define PP_SEED_SCALE     1.0
#define PP_SEED_OFFSET_U  0.217709193
#define PP_SEED_OFFSET_V  0.272716720
#define PP_SEED_ROTATION  0.524950679
#define PP_SEED_RANDOM_U  0.303749563
#define PP_SEED_RANDOM_V  0.224818403

// SpriteCardPerParticleScale_t. Multipliers, so NONE resolves to 1; the age-driven entries add 1 for the
// same reason, leaving a newborn particle at unity.
// particleScalars = (seed, age, animation frame, radius), rotation = (roll, yaw, pitch).
float PerParticleScale(int source, float seedScale, vec4 particleScalars, vec3 rotation,
                       vec2 shaderExtraData, float particleAlpha)
{
    float randomValue = fract(particleScalars.x * seedScale);

    switch (source)
    {
        case 1:  return particleScalars.y + 1.0;                 // PARTICLE_AGE
        case 2:  return particleScalars.z + 1.0;                 // ANIMATION_FRAME
        case 3:  return shaderExtraData.x;                       // SHADER_EXTRA_DATA1
        case 4:  return shaderExtraData.y;                       // SHADER_EXTRA_DATA2
        case 5:  return particleAlpha;                           // PARTICLE_ALPHA
        case 6:  return particleScalars.w;                       // SHADER_RADIUS
        case 7:  return rotation.x;                              // ROLL
        case 8:  return rotation.y;                              // YAW
        case 9:  return rotation.z;                              // PITCH
        case 10: return randomValue;                             // RANDOM
        case 11: return (randomValue - 0.5) * 2.0;               // NEG_RANDOM
        case 12: return (particleScalars.y * randomValue) + 1.0; // RANDOM_TIME
        case 13:                                                 // NEG_RANDOM_TIME
        {
            float signedRandom = (randomValue - 0.5) * 2.0;
            return (signedRandom * particleScalars.y) + sign(signedRandom);
        }
    }

    return 1.0; // NONE
}

// Rotates the corner about the middle of its cell, then tiles and offsets it, with each control scaled by
// whichever per-particle scalar it names. The offset is wrapped through fract() about 0.5, which is what
// puts an untransformed card back at its own corner: with every control at its default and no selector
// driving anything, this returns uvCorner unchanged.
vec2 ApplyLayerUvTransform(vec2 uvCorner, int layer, vec4 particleScalars, vec3 rotation,
                           vec2 shaderExtraData, float particleAlpha)
{
    float scale = PerParticleScale(PerParticleSelector(layer, PP_SLOT_SCALE), PP_SEED_SCALE,
        particleScalars, rotation, shaderExtraData, particleAlpha);
    float offsetU = PerParticleScale(PerParticleSelector(layer, PP_SLOT_OFFSET_U), PP_SEED_OFFSET_U,
        particleScalars, rotation, shaderExtraData, particleAlpha);
    float offsetV = PerParticleScale(PerParticleSelector(layer, PP_SLOT_OFFSET_V), PP_SEED_OFFSET_V,
        particleScalars, rotation, shaderExtraData, particleAlpha);
    float rotationScale = PerParticleScale(PerParticleSelector(layer, PP_SLOT_ROTATION), PP_SEED_ROTATION,
        particleScalars, rotation, shaderExtraData, particleAlpha);

    vec2 centred = uvCorner - vec2(0.5);

    float angle = uLayerUvRotation[layer] * rotationScale;
    float c = cos(angle);
    float s = sin(angle);
    vec2 rotated = vec2((centred.x * c) - (centred.y * s), (centred.x * s) + (centred.y * c));

    vec4 xform = uLayerUvTransform[layer];

    // A zero scale would send the card to a single texel, so it resolves to no extra zoom.
    vec2 tiled = (rotated / xform.zw) / (scale == 0.0 ? 1.0 : scale);

    vec2 randomOffset = LayerRandomizesOffsets(layer)
        ? fract(vec2(particleScalars.x * PP_SEED_RANDOM_U, particleScalars.x * PP_SEED_RANDOM_V))
        : vec2(0.0);

    return tiled + fract((xform.xy * vec2(offsetU, offsetV)) + vec2(0.5)) + randomOffset;
}

#endif
