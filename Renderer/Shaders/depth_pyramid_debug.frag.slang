#version 460

// Draws the occlusion culling depth pyramid.
//
// Reverse Z against an infinite far plane stores the near plane over the view distance, so the whole
// scene past twenty units or so lands within a few percent of the far plane. Drawn as it is stored,
// even through a gamma curve, the pyramid is a flat dark grey with the shape of the scene buried in
// the bottom of the range. So the depth is turned back into a view distance and stepped through a
// colour ramp on a log2 scale, which spends an equal part of the ramp on every doubling of distance.

layout(binding = 0) uniform sampler2D g_tDepthPyramid;

// Window-space depth range of the main scene layer (DepthRange.Scene), as (min, max).
uniform vec2 g_vSceneDepthRange;
// Near plane distance, which is the numerator the stored depth is a reciprocal of.
uniform float g_flNearPlane;
uniform vec2 g_vViewportSize;

layout (location = 0) out vec4 vColorOutput;

// Ramp stops from far to near. Luminance climbs the whole way, so depth still reads as depth with
// the hues ignored, while the hue change on top of it is what makes a step of one stop obvious.
const vec3 RampStops[5] = vec3[5](
    vec3(0.02, 0.02, 0.10),
    vec3(0.15, 0.20, 0.75),
    vec3(0.10, 0.70, 0.65),
    vec3(0.90, 0.80, 0.20),
    vec3(1.00, 1.00, 1.00)
);

vec3 SampleRamp(float position)
{
    float scaled = clamp(position, 0.0, 1.0) * float(RampStops.length() - 1);
    int stop = int(min(scaled, float(RampStops.length() - 2)));

    return mix(RampStops[stop], RampStops[stop + 1], scaled - float(stop));
}

void main()
{
    vec2 uv = gl_FragCoord.xy / g_vViewportSize;
    float depth = textureLod(g_tDepthPyramid, uv, 0.0).r;

    // Back out of the depth range into NDC, where the depth is the near plane over the distance.
    // Texels nothing occludes sit on the far plane, at zero, and come out as an infinite distance.
    float ndcDepth = (depth - g_vSceneDepthRange.x) / (g_vSceneDepthRange.y - g_vSceneDepthRange.x);
    float viewDistance = g_flNearPlane / max(ndcDepth, 1e-9);

    // Furthest distance the ramp resolves; everything beyond it shares the far end. A Source map is
    // around 16k units across, so a whole map fits without the ramp being spent on the near plane.
    const float MaxDistance = 8192.0;

    vColorOutput = vec4(SampleRamp(1.0 - log2(viewDistance) / log2(MaxDistance)), 1.0);
}
