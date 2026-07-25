#version 460

#include "common/instancing.slang"
#include "common/animation.slang"

layout (location = 0) in vec3 vPOSITION;

#include "common/ViewConstants.slang"

flat out uint sceneObjectId;
flat out uint meshletId;

void main(void) {
    gl_Position = g_matWorldToProjection * CalculateObjectToWorldMatrix() * getSkinMatrix() * vec4(vPOSITION, 1.0);
    // Object indices are per draw call, the node id a draw belongs to is in its object data
    sceneObjectId = objects[GetObjectIndex()].Identification;
    meshletId = gl_DrawID;
}
