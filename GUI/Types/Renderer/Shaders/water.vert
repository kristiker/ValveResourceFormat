#version 460

layout (location = 0) in vec3 vPOSITION;
layout (location = 3) in vec2 vTEXCOORD;
#include "common/compression.glsl"

out vec3 vFragPosition;

out vec3 vNormalOut;
out vec4 vTangentOut;
out vec3 vBitangentOut;

out vec2 vTexCoordOut;

#include "common/instancing.glsl"
#include "common/ViewConstants.glsl"

void main()
{
    vec4 fragPosition = CalculateObjectToWorldMatrix() * vec4(vPOSITION, 1.0);
    gl_Position = g_matViewToProjection * fragPosition;
    vFragPosition = fragPosition.xyz / fragPosition.w;

    GetOptionallyCompressedNormalTangent(vNormalOut, vTangentOut);

    vTexCoordOut = vTEXCOORD;
}
