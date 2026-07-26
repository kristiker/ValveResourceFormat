#version 460

#ifndef VRF_ANIMATION_INCLUDE
#define VRF_ANIMATION_INCLUDE

#include "instancing.slang"

layout (location = 1) in uvec4 vBLENDINDICES;
layout (location = 2) in vec4 vBLENDWEIGHT;

#define D_EIGHT_BONE_BLENDING 0

#if (D_EIGHT_BONE_BLENDING == 1)
    layout (location = 4) in uvec4 vBLENDINDICES2;
    layout (location = 5) in vec4 vBLENDWEIGHT2;
#endif

uniform uvec4 uAnimationData;

#define bAnimated (uAnimationData.x != 0u)
#define meshBoneOffset uAnimationData.y
#define meshBoneCount uAnimationData.z
#define numWeights uAnimationData.w

// The object transform is folded into every skinning matrix on the CPU, leaving out only its uniform
// scale, which a rigid matrix cannot carry. Vertex positions get that applied directly instead.
float GetObjectUniformScale()
{
    // A mesh without bones falls through the unskinned transform, which still has the scale
    if (!bAnimated || meshBoneCount == 0u)
    {
        return 1.0;
    }

    // The slot before the bones holds a Transform, where scale is a field rather than a matrix basis
    return transforms[objects[GetObjectIndex()].TransformIndex + 1u][0].z;
}

vec4 ApplyUniformScale(vec3 vPositionOs)
{
    return vec4(vPositionOs * GetObjectUniformScale(), 1.0);
}

vec3 TransformDirectionToWorld(mat3x4 objectToWorld, vec3 vDirectionOs)
{
    return normalize(vec4(vDirectionOs, 0.0) * objectToWorld);
}

mat3x4 getMatrix(uint boneIndex, uint objectBoneOffset)
{
    // Issue #705 out of bounds bone index (model needs ApplyVBIBDefaults)
    // Model:  hlvr/models/props/xen/xen_villi_medium.vmdl
    // In map: hlvr/maps/a3_distillery.vmap
    if (boneIndex >= meshBoneCount) {
        mat3x4 objectToWorld = CalculateObjectToWorldMatrix();
        float flScale = GetObjectUniformScale();

        if (flScale != 0.0)
        {
            objectToWorld[0].xyz /= flScale;
            objectToWorld[1].xyz /= flScale;
            objectToWorld[2].xyz /= flScale;
        }

        return objectToWorld;
    }

    boneIndex += objectBoneOffset + meshBoneOffset;

    return transforms[boneIndex];
}

// The full object to world matrix for this vertex, blended from the bones when animated.
mat3x4 CalculateSkinTransform()
{
    if (!bAnimated)
    {
        return CalculateObjectToWorldMatrix();
    }

    uint objectBoneOffset = GetObjectBoneOffset();

    if (numWeights == 1u)
    {
        return getMatrix(vBLENDINDICES.x, objectBoneOffset);
    }

    mat3x4 skinMatrix = mat3x4(0.0);

    skinMatrix += vBLENDWEIGHT.x * getMatrix(vBLENDINDICES.x, objectBoneOffset);
    skinMatrix += vBLENDWEIGHT.y * getMatrix(vBLENDINDICES.y, objectBoneOffset);
    skinMatrix += vBLENDWEIGHT.z * getMatrix(vBLENDINDICES.z, objectBoneOffset);
    skinMatrix += vBLENDWEIGHT.w * getMatrix(vBLENDINDICES.w, objectBoneOffset);

    #if (D_EIGHT_BONE_BLENDING == 1)
        skinMatrix += vBLENDWEIGHT2.x * getMatrix(vBLENDINDICES2.x, objectBoneOffset);
        skinMatrix += vBLENDWEIGHT2.y * getMatrix(vBLENDINDICES2.y, objectBoneOffset);
        skinMatrix += vBLENDWEIGHT2.z * getMatrix(vBLENDINDICES2.z, objectBoneOffset);
        skinMatrix += vBLENDWEIGHT2.w * getMatrix(vBLENDINDICES2.w, objectBoneOffset);
    #endif

    return skinMatrix;
}

#endif
