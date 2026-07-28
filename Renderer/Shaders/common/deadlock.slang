#version 460

#ifndef VRF_DEADLOCK_INCLUDE
#define VRF_DEADLOCK_INCLUDE

#include "utils.slang"

// Deadlock's opaque shaders all write the same gbuffer, so its deferred pass derives one
// specular colour for every surface. pbr's forward variants reimplement that pass, and this
// is the form they use: the usual dielectric/metal mix, faded out on near black albedo.
// Anything darker than 0.04 in its brightest channel loses its specular entirely, which is
// what keeps Deadlock's inked-in shadow areas from picking up highlights.
vec3 GetDeadlockSpecularColor(vec3 albedo, float metalness)
{
    const vec3 F0 = vec3(0.04);
    float albedoMax = max(albedo.x, max(albedo.y, albedo.z));

    return mix(F0, albedo, metalness) * saturate(albedoMax * 25.0);
}

#endif
