#version 460

#include "common/instancing.slang"
#include "common/animation.slang"

layout (location = 0) in vec3 vPOSITION;
layout (location = 3) in vec2 vTEXCOORD;

out vec2 vTexCoordOut;

#include "common/ViewConstants.slang"

void main(void) {
    vTexCoordOut = vTEXCOORD;
    mat3x4 skinTransform = GetSkinnedObjectToWorldMatrix();
    vec3 fragPosition = GetScaledPositionOs(vPOSITION) * skinTransform;
    gl_Position = g_matWorldToProjection * vec4(fragPosition, 1.0);
}
