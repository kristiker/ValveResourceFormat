#version 460

layout (local_size_x = 64) in;

// One thread per composite slot, for every mesh sharing this morph atlas. Each slot gathers every morph
// rect that covers it and sums their weighted contributions, replacing the additive blending that the
// old raster pass relied on.

struct MorphRect
{
    uvec4 destSrc; // destX, destY, srcX, srcY, all in texels
    uvec4 morph;   // morphId, unused
    vec4 offsets;
    vec4 ranges;
};

layout(binding = 14, std430) readonly buffer MorphRects
{
    MorphRect rects[];
};

layout(binding = 15, std430) readonly buffer MorphRectStarts
{
    uint rectStarts[]; // Where each slot's rect list begins in rectEntries, plus a terminator
};

layout(binding = 16, std430) readonly buffer MorphRectEntries
{
    uint rectEntries[];
};

layout(binding = 17, std430) readonly buffer MorphWeights
{
    float morphWeights[];
};

layout(binding = 13, std430) writeonly buffer MorphOffsets
{
    vec4 morphOffsets[];
};

uniform sampler2D morphAtlas;

// Per morph resource: all meshes in one dispatch share these, since they share the gather tables
uniform uint g_nCompositeWidth;
uniform uint g_nCompositeSlotCount;
uniform uint g_nRectStartsBase;
uniform uint g_nMeshCount;

// Per mesh ranges. Meshes are packed back to back in dispatch order, so a stride reaches all of them
uniform uint g_nOffsetsBase;
uniform uint g_nWeightsBase;
uniform uint g_nWeightStride;

void main()
{
    uint mesh = gl_GlobalInvocationID.x / g_nCompositeSlotCount;
    uint slot = gl_GlobalInvocationID.x % g_nCompositeSlotCount;

    if (mesh >= g_nMeshCount)
    {
        return;
    }

    uint weightsBase = g_nWeightsBase + mesh * g_nWeightStride;

    ivec2 destCoord = ivec2(slot % g_nCompositeWidth, slot / g_nCompositeWidth);

    vec3 offset = vec3(0.0);

    for (uint i = rectStarts[g_nRectStartsBase + slot]; i < rectStarts[g_nRectStartsBase + slot + 1u]; i++)
    {
        MorphRect rect = rects[rectEntries[i]];

        float weight = morphWeights[weightsBase + rect.morph.x];

        if (weight == 0.0)
        {
            continue;
        }

        // Destination and source rects are the same size, so the offset within the rect carries over as-is
        ivec2 local = destCoord - ivec2(rect.destSrc.xy);
        vec4 pixel = texelFetch(morphAtlas, ivec2(rect.destSrc.zw) + local, 0) * rect.ranges + rect.offsets;

        offset += weight * pixel.xyz;
        // TODO: normals would gather from the NormalWrinkle bundle's own source rect: normal += weight * (texelFetch(morphAtlas, ivec2(rect.normalSrc) + local, 0) * rect.normalRanges + rect.normalOffsets).xyz;
    }

    morphOffsets[g_nOffsetsBase + mesh * g_nCompositeSlotCount + slot] = vec4(offset, 0.0);
}
