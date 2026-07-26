#version 460

#ifndef VRF_DEPTH_INCLUDE
#define VRF_DEPTH_INCLUDE

#include "ViewConstants.slang"
#include "utils.slang"

// The opaque depth resolved before the translucent pass. Declaring this puts g_tSceneDepth in the
// shader's ReservedTexturesUsed set, which is how the scene knows to produce the copy at all.
uniform sampler2D g_tSceneDepth;

// Reconstructs the world position the depth buffer holds at a given pixel. vCameraRay is the vector
// from the camera to the fragment, not normalised: dividing by its projection onto the camera forward
// axis is what turns a depth-buffer value into a distance along that ray.
vec3 GetWorldPositionFromDepth(ivec2 vScreenPosition, vec3 vCameraRay)
{
    float flSceneDepth = texelFetch(g_tSceneDepth, vScreenPosition, 0).x;
    float flSceneDepthNormalized = RemapValClamped(flSceneDepth, g_flViewportMinZ, g_flViewportMaxZ, 0.0, 1.0);

    float invProjTerm = fma(flSceneDepthNormalized, g_vInvProjRow3.z, g_vInvProjRow3.w);

    float flPerspectiveCorrection = dot(g_vCameraDirWs, vCameraRay);

    return (g_vCameraPositionWs.xyz + (vCameraRay * (1.0 / (invProjTerm * flPerspectiveCorrection))));
}

#endif
