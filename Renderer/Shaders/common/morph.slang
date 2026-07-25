#version 460

#ifndef VRF_MORPH_INCLUDE
#define VRF_MORPH_INCLUDE

#include "animation.slang"

#define F_MORPH_SUPPORTED 0 // most shaders
#define bMorphed (morphVertexIdOffset != -1)

#if (F_MORPH_SUPPORTED == 1)
    uniform sampler2D morphCompositeTexture;
    uniform vec2 morphCompositeTextureSize;
    uniform int morphVertexIdOffset;

    vec2 getMorphUV()
    {
        int vertexId = gl_VertexID + morphVertexIdOffset;
        return vec2(
            (1.5 + mod(vertexId, morphCompositeTextureSize.x)) / 2048.0,
            1 - (1.5 + floor(vertexId / morphCompositeTextureSize.x)) / 2048.0
        );
    }
#endif

vec3 getMorphOffset()
{
    #if (F_MORPH_SUPPORTED == 1)
        if (bMorphed)
        {
            return textureLod(morphCompositeTexture, getMorphUV(), 0).xyz;
        }
    #endif

    return vec3(0, 0, 0);
}

// The morph offset is authored in object space, so it goes through the instance scale with the position.
vec4 ApplyMorphOffset(vec3 vPositionOs)
{
    return ApplyUniformScale(vPositionOs + getMorphOffset());
}

#endif
