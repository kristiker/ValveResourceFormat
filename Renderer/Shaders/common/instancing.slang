#version 460

#ifndef VRF_INSTANCING_INCLUDE
#define VRF_INSTANCING_INCLUDE

uniform mat3x4 transform;
uniform uint vTint;

// Must match MeshBatchRenderer.InstancingMode
#define INSTANCING_MODE_NONE 0
#define INSTANCING_MODE_AGGREGATE 1
#define INSTANCING_MODE_OBJECT_LIST 2

uniform int nInstancingMode = INSTANCING_MODE_NONE;

#if (S_SCENE_CUBEMAP_TYPE > 0)
    flat out uvec4 g_nEnvMapVisibility;
#endif

#if (D_BAKED_LIGHTING_FROM_PROBE > 0)
    flat out uint g_nVisibleLPV;
#endif

struct ObjectDataStandard
{
    uint TintAlpha;
    uint TransformIndex;
    uint VisibleLPV;
    uint Identification;
    uvec4 EnvMapVisibility;
};

layout(std430, binding = 0) readonly buffer g_objectBuffer
{
    ObjectDataStandard objects[];
};

layout(std430, binding = 1) readonly buffer g_transformBuffer
{
    mat3x4 transforms[];
};

layout(std430, binding = 11) readonly buffer g_instancingObjectIndexBuffer
{
    uint instancingObjectIndices[];
};

struct ObjectData_t
{
    vec4 vTint;
};

vec4 UnpackColor32(uint nColor)
{
    vec4 vResult;
    vResult.a = (nColor >> 24) & 0xff;
    vResult.b = (nColor >> 16) & 0xff;
    vResult.g = (nColor >> 8) & 0xff;
    vResult.r = (nColor >> 0) & 0xff;
    vResult.rgba *= ( 1.0 / 255.0 );
    return vResult;
}

mat4 UnpackMatrix4(mat3x4 m)
{
    return mat4(
        m[0][0], m[1][0], m[2][0], 0,
        m[0][1], m[1][1], m[2][1], 0,
        m[0][2], m[1][2], m[2][2], 0,
        m[0][3], m[1][3], m[2][3], 1
    );
}

uint GetObjectIndex()
{
    if (nInstancingMode == INSTANCING_MODE_OBJECT_LIST)
    {
        // gl_BaseInstance is an offset into the per-frame object index list
        return instancingObjectIndices[gl_BaseInstance + gl_InstanceID];
    }

    return gl_BaseInstance;
}

// The skinning matrices sit a fixed distance past the object's own transform, so an object list draw
// finds each instance's bones from its own entry. Kept in sync with ModelSceneNode.BoneTransformStart.
uint GetObjectBoneOffset()
{
    return objects[GetObjectIndex()].TransformIndex + 2u;
}

void SetObjectInterpolators(ObjectDataStandard objectData)
{
    #if (S_SCENE_CUBEMAP_TYPE > 0)
        g_nEnvMapVisibility = objectData.EnvMapVisibility;
    #endif

    #if (D_BAKED_LIGHTING_FROM_PROBE > 0)
        g_nVisibleLPV = objectData.VisibleLPV;
    #endif
}

mat3x4 CalculateObjectToWorldMatrix()
{
    ObjectDataStandard objectData = objects[GetObjectIndex()];

    SetObjectInterpolators(objectData);

    if (nInstancingMode == INSTANCING_MODE_NONE)
    {
        return transform;
    }

    // An aggregate draws one object entry many times, each instance taking the next transform along.
    // An object list draws many entries, each already pointing at its own.
    if (nInstancingMode == INSTANCING_MODE_AGGREGATE)
    {
        return transforms[objectData.TransformIndex + gl_InstanceID];
    }

    return transforms[objectData.TransformIndex];
}

ObjectData_t GetObjectData()
{
    ObjectDataStandard objectData = objects[GetObjectIndex()];

    SetObjectInterpolators(objectData);

    ObjectData_t object;
    object.vTint = UnpackColor32(nInstancingMode == INSTANCING_MODE_AGGREGATE ? objectData.TintAlpha : vTint);

    return object;
}


vec4 GetObjectTintSrgb()
{
    return GetObjectData().vTint;
}

#endif
