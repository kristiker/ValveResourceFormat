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

// Rotates the corner about the middle of its cell, then tiles and offsets it. The offset is wrapped
// through fract() about 0.5, which is what puts an untransformed card back at its own corner: with a zero
// offset, unit scale and no rotation this returns uvCorner unchanged.
vec2 ApplyLayerUvTransform(vec2 uvCorner, int layer)
{
    vec2 centred = uvCorner - vec2(0.5);

    float angle = uLayerUvRotation[layer];
    float c = cos(angle);
    float s = sin(angle);
    vec2 rotated = vec2((centred.x * c) - (centred.y * s), (centred.x * s) + (centred.y * c));

    vec4 xform = uLayerUvTransform[layer];

    return (rotated / xform.zw) + fract(xform.xy + vec2(0.5));
}

#endif
