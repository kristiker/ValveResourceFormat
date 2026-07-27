#version 460

#include "common/ViewConstants.slang"
#include "common/utils.slang"

layout (local_size_x = 8, local_size_y = 8) in;

// Reduces one mip level of the depth pyramid into the next. The top level is written by
// depth_reproject.comp.slang, which reprojects the previous frame's depth buffer into this
// frame's view.

layout(binding = 1, r32f) uniform readonly image2D g_tSourceDepth;

layout(binding = 2, r32f) uniform writeonly image2D g_tDestDepth;
uniform int g_nDestDepthWidth;
uniform int g_nDestDepthHeight;

void main()
{
    ivec2 destCoord = ivec2(gl_GlobalInvocationID.xy);
    ivec2 destSize = ivec2(g_nDestDepthWidth, g_nDestDepthHeight);

    if (destCoord.x >= destSize.x || destCoord.y >= destSize.y)
    {
        return;
    }

    ivec2 sourceCoord = destCoord * 2;

    // Regular 2x2 downsample - take min (reverse Z: furthest point)
    float depth0 = imageLoad(g_tSourceDepth, sourceCoord + ivec2(0, 0)).r;
    float depth1 = imageLoad(g_tSourceDepth, sourceCoord + ivec2(1, 0)).r;
    float depth2 = imageLoad(g_tSourceDepth, sourceCoord + ivec2(0, 1)).r;
    float depth3 = imageLoad(g_tSourceDepth, sourceCoord + ivec2(1, 1)).r;

    float furthestDepth = min(min(depth0, depth1), min(depth2, depth3));

    imageStore(g_tDestDepth, destCoord, vec4(furthestDepth, 0.0, 0.0, 0.0));
}
