#version 460

#ifndef VRF_MORPH_INCLUDE
#define VRF_MORPH_INCLUDE

#define F_MORPH_SUPPORTED 0 // most shaders
#define bMorphed (morphVertexIdOffset != -1)

#if (F_MORPH_SUPPORTED == 1)
    // Composited by morph_composite.comp.slang, indexed by mesh vertex id
    layout(binding = 13, std430) readonly buffer MorphOffsets
    {
        vec4 morphOffsets[];
    };

    uniform int morphVertexIdOffset;
#endif

vec3 getMorphOffset()
{
    #if (F_MORPH_SUPPORTED == 1)
        if (bMorphed)
        {
            return morphOffsets[gl_VertexID + morphVertexIdOffset].xyz;
        }
    #endif

    return vec3(0, 0, 0);
}

#endif
