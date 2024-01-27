#version 460

#include "common/ViewConstants.glsl"
#include "common/instancing.glsl"

layout (location = 0) in vec3 vPOSITION;

void main()
{
    vec4 fragPosition = CalculateObjectToWorldMatrix() * vec4(vPOSITION, 1.0);
    gl_Position = g_matViewToProjection * fragPosition;
}
