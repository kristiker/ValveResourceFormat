#version 460

layout (location = 0) in vec3 aVertexPosition;
layout (location = 3) in vec2 aTexCoords;
layout (location = 4) in vec4 aVertexColor;

#include "common/utils.slang"
#include "common/ViewConstants.slang"

// m_bGammaCorrectVertexColors. Particle colours are authored in gamma space; without this they are used
// as though they were already linear. Alpha is left alone.
uniform int uGammaCorrectVertexColors = 0;

out vec2 vTexCoordOut;
out vec4 vColor;
out vec3 vFragPosition;

void main(void) {
    vColor = aVertexColor;

    if (uGammaCorrectVertexColors != 0)
    {
        vColor.rgb = SrgbGammaToLinear(vColor.rgb);
    }

    vTexCoordOut = aTexCoords;
    vFragPosition = aVertexPosition;
    gl_Position = g_matWorldToProjection * vec4(aVertexPosition, 1.0);
}
