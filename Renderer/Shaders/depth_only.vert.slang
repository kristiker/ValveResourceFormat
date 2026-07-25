#version 460

#include "common/ViewConstants.slang"

#define D_ANIMATED 0
#define D_MORPHED 0
#define F_ALPHA_TEST 0 // most shaders

layout (location = 0) in vec3 vPOSITION;
#if (F_ALPHA_TEST == 1)
    layout (location = 3) in vec2 vTEXCOORD;
    layout (location = 0) out vec2 texCoord;
#endif

#include "common/instancing.slang"
#include "common/animation.slang"
#include "common/morph.slang"

void main()
{
    // Falls back to the plain object transform when the draw is not animated, so the matrix and the
    // scale in GetScaledPositionOs below always agree on which path this vertex is taking
    mat3x4 vertexTransform = GetSkinnedObjectToWorldMatrix();
    vec3 vertexPosition = vPOSITION;

    #if (D_MORPHED == 1)
        vertexPosition += getMorphOffset();
    #endif

    #if (F_ALPHA_TEST == 1)
        texCoord = vTEXCOORD;
    #endif

    vec3 fragPosition = GetScaledPositionOs(vertexPosition) * vertexTransform;
    gl_Position = g_matWorldToProjection * vec4(fragPosition, 1.0);
}
