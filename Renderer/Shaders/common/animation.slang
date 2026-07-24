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

// Skinning matrices live in the shared transform buffer, at a per-object
// offset from object data so instanced draws can each animate independently.
mat4 getMatrix(uint boneIndex, uint objectBoneOffset)
{
    // Issue #705 out of bounds bone index (model needs ApplyVBIBDefaults)
    // Model:  hlvr/models/props/xen/xen_villi_medium.vmdl
    // In map: hlvr/maps/a3_distillery.vmap
    if (boneIndex >= meshBoneCount) {
        return mat4(1.0);
    }

    boneIndex += objectBoneOffset + meshBoneOffset;

    return UnpackMatrix4(transforms[boneIndex]);
}

mat4 getSkinMatrix()
{
    if (!bAnimated)
    {
        return mat4(1.0);
    }

    uint objectBoneOffset = GetObjectBoneOffset();

    if (numWeights == 1u)
    {
        return getMatrix(vBLENDINDICES.x, objectBoneOffset);
    }

    mat4 skinMatrix = mat4(0.0);


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
