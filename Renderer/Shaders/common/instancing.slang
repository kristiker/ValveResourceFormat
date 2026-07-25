#version 460

#ifndef VRF_INSTANCING_INCLUDE
#define VRF_INSTANCING_INCLUDE

uniform mat3x4 transform;
uniform uint vTint;
uniform bool bIsInstancing = false;

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

struct ObjectData_t
{
    mat4 transform;
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

// Skinning matrices live in the shared transform buffer, starting two slots after
// the object transform, the way Source 2 lays them out.
uint GetObjectBoneOffset()
{
    return objects[gl_BaseInstance].TransformIndex + 2u;
}

// The object to world matrix still packed. vec4(v, w) * m is the same three dot products
// UnpackMatrix4 would hand to a mat4.
mat3x4 GetObjectToWorldMatrix3x4()
{
    ObjectDataStandard objectData = objects[gl_BaseInstance];

    #if (S_SCENE_CUBEMAP_TYPE > 0)
        g_nEnvMapVisibility = objectData.EnvMapVisibility;
    #endif

    #if (D_BAKED_LIGHTING_FROM_PROBE > 0)
        g_nVisibleLPV = objectData.VisibleLPV;
    #endif

    return bIsInstancing ? transforms[objectData.TransformIndex + gl_InstanceID] : transform;
}

ObjectData_t GetObjectData()
{
    ObjectData_t object;
    object.transform = UnpackMatrix4(GetObjectToWorldMatrix3x4());
    object.vTint = UnpackColor32(bIsInstancing ? objects[gl_BaseInstance].TintAlpha : vTint);

    return object;
}

mat4 CalculateObjectToWorldMatrix()
{
    return GetObjectData().transform;
}

vec4 GetObjectTintSrgb()
{
    return GetObjectData().vTint;
}

#endif
