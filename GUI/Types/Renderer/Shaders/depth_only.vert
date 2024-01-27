#version 460

#include "common/ViewConstants.glsl"

layout (location = 0) in vec3 vPOSITION;

uniform mat4 transform;

void main()
{
    vec4 fragPosition = transform * vec4(vPOSITION, 1.0);
    gl_Position = g_matViewToProjection * fragPosition;
}
