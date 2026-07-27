#version 460

// Builds the top level of the occlusion culling depth pyramid out of the previous frame's depth
// buffer, reprojected into the view being culled. Occlusion culling is always a frame behind, since
// a frame's depth only exists once that frame has been drawn, and testing this frame's bounds against
// last frame's screen space is what makes geometry the camera just revealed pop in a frame late.
// Reprojection moves last frame's occluders to where they are now, leaving behind only the regions
// the previous frame genuinely never saw; those get the far plane, which occludes nothing.
//
// Reprojecting a depth buffer is a scatter: which source texel lands on a destination texel is only
// known by walking forward from the source. Texels that collide are resolved with an atomic min over
// the float bit pattern, which is monotonic for non-negative floats, so the furthest sample wins -
// the same conservative reduction the pyramid's mip chain does.

#define D_RESOLVE 0

layout (local_size_x = 8, local_size_y = 8) in;

// Window-space depth range of the main scene layer (DepthRange.Scene), as (min, max).
uniform vec2 g_vSceneDepthRange;

// Destination texel that no source sample landed on. Picked to lose every atomic min.
#define REPROJECT_GAP 0xFFFFFFFFu

#if D_RESOLVE == 0

layout(binding = 0, r32f) uniform readonly image2D g_tSourceDepth;
layout(binding = 1, r32ui) uniform coherent uimage2D g_tScatterDepth;

// Previous frame's clip space to the current frame's clip space.
uniform mat4 g_matReprojection;

void main()
{
    ivec2 sourceSize = imageSize(g_tSourceDepth);
    ivec2 sourceCoord = ivec2(gl_GlobalInvocationID.xy) * 2;

    if (sourceCoord.x >= sourceSize.x || sourceCoord.y >= sourceSize.y)
    {
        return;
    }

    // Reduce each 2x2 block to its furthest sample before scattering. That quarters the atomic
    // traffic, and keeping the furthest of the four can only drop occluders, never invent them.
    ivec2 lastCoord = sourceSize - ivec2(1);
    float depth = imageLoad(g_tSourceDepth, sourceCoord).r;
    depth = min(depth, imageLoad(g_tSourceDepth, min(sourceCoord + ivec2(1, 0), lastCoord)).r);
    depth = min(depth, imageLoad(g_tSourceDepth, min(sourceCoord + ivec2(0, 1), lastCoord)).r);
    depth = min(depth, imageLoad(g_tSourceDepth, min(sourceCoord + ivec2(1, 1), lastCoord)).r);

    // The depth buffer also holds the sky behind the scene depth range and the viewmodel in front of
    // it, both drawn with a different projection. Clamping puts the sky on the far plane, which is at
    // infinity under an infinite reverse-Z projection, so reprojecting it comes out as a pure
    // rotation - exactly right - and the viewmodel on the near plane, where it occludes as before.
    float sceneDepthSize = g_vSceneDepthRange.y - g_vSceneDepthRange.x;
    float ndcDepth = clamp((depth - g_vSceneDepthRange.x) / sceneDepthSize, 0.0, 1.0);

    vec2 sourceUv = (vec2(sourceCoord) + 1.0) / vec2(sourceSize);
    vec4 clipPosition = g_matReprojection * vec4(sourceUv * 2.0 - 1.0, ndcDepth, 1.0);

    if (clipPosition.w <= 0.0)
    {
        return; // Moved behind the camera.
    }

    vec3 ndcPosition = clipPosition.xyz / clipPosition.w;

    if (any(greaterThan(abs(ndcPosition.xy), vec2(1.0))))
    {
        return; // Moved off screen.
    }

    ivec2 destSize = imageSize(g_tScatterDepth);
    ivec2 destCoord = clamp(ivec2((ndcPosition.xy * 0.5 + 0.5) * vec2(destSize)), ivec2(0), destSize - ivec2(1));

    float destDepth = clamp(ndcPosition.z, 0.0, 1.0) * sceneDepthSize + g_vSceneDepthRange.x;

    imageAtomicMin(g_tScatterDepth, destCoord, floatBitsToUint(destDepth));
}

#else

layout(binding = 1, r32ui) uniform readonly uimage2D g_tScatterDepth;
layout(binding = 2, r32f) uniform writeonly image2D g_tDestDepth;

void main()
{
    ivec2 coord = ivec2(gl_GlobalInvocationID.xy);
    ivec2 size = imageSize(g_tScatterDepth);

    if (coord.x >= size.x || coord.y >= size.y)
    {
        return;
    }

    uint packedDepth = imageLoad(g_tScatterDepth, coord).r;

    // Single texel gaps are where the new view pulled last frame's samples apart, which happens on
    // geometry close enough to the camera to swing across the screen in one frame. Filling them with
    // the furthest neighbour keeps the pyramid useful: every coarser mip is the furthest sample of
    // the level below, so one unfilled gap would flatten every mip covering it to the far plane and
    // stop culling anything large. A gap the size of a real disocclusion keeps an empty core, and
    // stays the far plane where it matters.
    const bool FillGaps = true;

    if (FillGaps && packedDepth == REPROJECT_GAP)
    {
        ivec2 lastCoord = size - ivec2(1);

        for (int y = -1; y <= 1; y++)
        {
            for (int x = -1; x <= 1; x++)
            {
                packedDepth = min(packedDepth, imageLoad(g_tScatterDepth, clamp(coord + ivec2(x, y), ivec2(0), lastCoord)).r);
            }
        }
    }

    // Whatever the previous frame never saw must not occlude anything, so it becomes the far plane.
    float depth = packedDepth == REPROJECT_GAP ? g_vSceneDepthRange.x : uintBitsToFloat(packedDepth);

    imageStore(g_tDestDepth, coord, vec4(depth, 0.0, 0.0, 0.0));
}

#endif
