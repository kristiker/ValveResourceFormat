#version 460

#include "common/instancing.slang"
#include "common/animation.slang"

layout (location = 0) in vec3 vPOSITION;

out vec3 vFragPosition;

#include "common/ViewConstants.slang"

void main()
{
    vec4 fragPosition = GetSkinnedObjectToWorldMatrix() * GetScaledPositionOs(vPOSITION);
    gl_Position = g_matWorldToProjection * fragPosition;
    vFragPosition = fragPosition.xyz;
}
