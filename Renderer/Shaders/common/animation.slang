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

// The object transform is folded into every skinning matrix on the CPU, so these are already object
// to world. Its uniform scale is the one part left out, because a rigid matrix cannot carry it
// (conjugating by the scale leaves it on the translation alone). Vertex positions get it applied
// directly instead, the way Source 2 does.
float GetObjectUniformScale()
{
    // A mesh without bones falls through getMatrix to the unskinned transform, which still carries the
    // scale, so it must not be applied here as well
    if (!bAnimated || meshBoneCount == 0u)
    {
        return 1.0;
    }

    // Read out of the Transform in the slot before the bones, where scale is a field of its own rather
    // than smeared across a matrix basis. Its x and y go unread, non uniformity is ignored here the way
    // Source 2 ignores it. Source 2 reads the same float out of the slot two before its bones instead,
    // ours is one before because our object transform slot stays a matrix for the draws that read it
    // unskinned.
    return transforms[objects[gl_BaseInstance].TransformIndex + 1u][0].z;
}

// Object space position with the instance's uniform scale applied, ready for a skinning matrix.
vec4 GetScaledPositionOs(vec3 vPositionOs)
{
    return vec4(vPositionOs * GetObjectUniformScale(), 1.0);
}

mat4 getMatrix(uint boneIndex, uint objectBoneOffset)
{
    // Issue #705 out of bounds bone index (model needs ApplyVBIBDefaults)
    // Model:  hlvr/models/props/xen/xen_villi_medium.vmdl
    // In map: hlvr/maps/a3_distillery.vmap
    // Falls back to the unskinned transform, identity would put the model at the world origin now
    // that the object transform lives in the bones. Scaled models get it applied twice here.
    if (boneIndex >= meshBoneCount) {
        return CalculateObjectToWorldMatrix();
    }

    boneIndex += objectBoneOffset + meshBoneOffset;

    return UnpackMatrix4(transforms[boneIndex]);
}

// The full object to world matrix for this vertex: a blend of the object's skinning matrices when
// animated, the plain object transform otherwise. There is no second matrix to compose with, the
// CPU already folded the object transform into the bones.
mat4 GetSkinnedObjectToWorldMatrix()
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
