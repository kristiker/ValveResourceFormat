#version 460

layout (location = 0) in vec3 vPOSITION;
layout (location = 3) in vec2 vTEXCOORD;
#include "common/compression.glsl"
//in vec2 vLightmapUV;
in vec4 vCOLOR;

out vec3 vFragPosition;
out vec2 vTexCoordOut;
out vec3 vNormalOut;
out vec4 vTangentOut;
out vec3 vBitangentOut;
out vec4 vColorBlendValues;

#include "common/instancing.glsl"
#include "common/ViewConstants.glsl"

void main()
{
    vec4 fragPosition = CalculateObjectToWorldMatrix() * vec4(vPOSITION, 1.0);
    gl_Position = g_matViewToProjection * fragPosition;
    vFragPosition = fragPosition.xyz / fragPosition.w;

    GetOptionallyCompressedNormalTangent(vNormalOut, vTangentOut);

    vTexCoordOut = vTEXCOORD;
    vColorBlendValues = vCOLOR;
}
