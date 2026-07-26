#version 460

// Trail quads arrive already stretched and oriented in world space, four corners per particle, with the
// sheet frame folded into the uv. There is no per-particle model matrix any more: every trail in a
// system goes through one buffer and one draw.
layout (location = 0) in vec3 aVertexPosition;
layout (location = 3) in vec2 aTexCoords;
layout (location = 4) in vec4 aVertexColor;

#include "common/ViewConstants.slang"

out vec2 uv;
out vec4 vColor;
out vec3 vFragPosition;

void main(void) {
    uv = aTexCoords;
    vColor = aVertexColor;
    vFragPosition = aVertexPosition;
    gl_Position = g_matWorldToProjection * vec4(aVertexPosition, 1.0);
}
