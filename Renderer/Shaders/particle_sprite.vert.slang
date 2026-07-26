#version 460

// One attribute set per particle, with divisor 1: the quad's four corners come from gl_VertexID rather
// than from the buffer, so nothing is duplicated per vertex. aRight and aUp are the card's axes with the
// radius already folded in, and aPosition is its centre with the m_flCenterXOffset/YOffset shift already
// applied -- the basis itself is still built on the CPU.
layout (location = 0) in vec3 aPosition;
layout (location = 1) in vec3 aRight;
layout (location = 2) in vec3 aUp;
layout (location = 4) in vec4 aVertexColor;
// Sheet frame rectangles as (min.x, min.y, max.x, max.y), for this frame and the one after it.
layout (location = 5) in vec4 aUvRect;
layout (location = 6) in vec4 aUvRectNextFrame;
layout (location = 7) in float aFrameBlend;

#include "common/utils.slang"
#include "common/ViewConstants.slang"

// m_bGammaCorrectVertexColors. Particle colours are authored in gamma space; without this they are used
// as though they were already linear. Alpha is left alone.
uniform int uGammaCorrectVertexColors = 0;

out vec2 vTexCoordOut;
out vec2 vTexCoordNextFrameOut;
out float vFrameBlend;
out vec4 vColor;
out vec3 vFragPosition;

// v runs opposite to the corner's y, so the rectangle's max lands on the -1 corner.
vec2 UvFromRect(vec4 rect, vec2 unitCorner)
{
    return mix(vec2(rect.x, rect.w), vec2(rect.z, rect.y), unitCorner);
}

void main(void) {
    // 0 -> (-1,-1), 1 -> (-1,+1), 2 -> (+1,-1), 3 -> (+1,+1): a triangle strip over the quad.
    vec2 corner = vec2(float(gl_VertexID / 2), float(gl_VertexID % 2)) * 2.0 - 1.0;
    vec2 unitCorner = (corner * 0.5) + 0.5;

    vColor = aVertexColor;

    if (uGammaCorrectVertexColors != 0)
    {
        vColor.rgb = SrgbGammaToLinear(vColor.rgb);
    }

    vTexCoordOut = UvFromRect(aUvRect, unitCorner);
    vTexCoordNextFrameOut = UvFromRect(aUvRectNextFrame, unitCorner);
    vFrameBlend = aFrameBlend;

    vec3 positionWs = aPosition + (aRight * corner.x) + (aUp * corner.y);

    vFragPosition = positionWs;
    gl_Position = g_matWorldToProjection * vec4(positionWs, 1.0);
}
