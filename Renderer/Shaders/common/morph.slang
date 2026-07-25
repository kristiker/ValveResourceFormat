#version 460

#ifndef VRF_MORPH_INCLUDE
#define VRF_MORPH_INCLUDE

#define F_MORPH_SUPPORTED 0 // most shaders
#define bMorphed (morphBufferOffset != -1)

#if (F_MORPH_SUPPORTED == 1)
    // Composited by morph_composite.comp.slang, one entry per vertex of every morphed mesh in the scene
    layout(binding = 13, std430) readonly buffer MorphOffsets
    {
        vec4 morphOffsets[];
    };

    // Index of this draw call's first vertex in the buffer above, or -1 when the mesh has no morphs
    uniform int morphBufferOffset;
#endif

vec3 getMorphOffset()
{
    #if (F_MORPH_SUPPORTED == 1)
        if (bMorphed)
        {
            return morphOffsets[morphBufferOffset + gl_VertexID].xyz;
        }
    #endif

    return vec3(0, 0, 0);
}

#endif
