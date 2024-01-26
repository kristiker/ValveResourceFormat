#version 460

#include "common/animation.glsl"

layout (location = 0) in vec3 vPOSITION;

out vec3 vFragPosition;

#include "common/instancing.glsl"
#include "common/ViewConstants.glsl"

void main()
{
    vec4 fragPosition = CalculateObjectToWorldMatrix() * getSkinMatrix() * vec4(vPOSITION, 1.0);
    gl_Position = g_matViewToProjection * fragPosition;
    vFragPosition = fragPosition.xyz / fragPosition.w;
}
