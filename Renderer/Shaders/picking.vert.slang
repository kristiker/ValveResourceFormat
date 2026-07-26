#version 460

#include "common/instancing.slang"
#include "common/animation.slang"

layout (location = 0) in vec3 vPOSITION;

#include "common/ViewConstants.slang"

flat out uint sceneObjectId;
flat out uint meshletId;

void main(void) {
    gl_Position = g_matWorldToProjection * vec4(ApplyUniformScale(vPOSITION) * CalculateSkinTransform(), 1.0);
    // Resolved the same way the object data is, or an object list draw reports whichever object
    // happened to sit next to the first one in the buffer
    sceneObjectId = GetObjectIndex();
    meshletId = gl_DrawID;
}
