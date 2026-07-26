#version 460

#include "common/utils.slang"

layout(binding = 0) uniform sampler2D inputImage;

// Global histogram buffer (256 bins)
layout(binding = 2, std430) buffer HistogramBuffer
{
    uint histogram[256];
};

// Shared histogram used inside a workgroup
shared uint localHistogram[256];

uniform float logMinLuminance;      // Minimum log luminance value
uniform float logLuminanceRange;    // Range of log luminance (inverse scale factor)

layout(binding = 3, std140) buffer Luminance { float outputLuminance; };

#define D_HISTOGRAM_MODE 0

#if D_HISTOGRAM_MODE == 0
    layout (local_size_x = 16, local_size_y = 16) in;
#else
    layout (local_size_x = 256) in;
#endif

void main()
{
    uvec3 globalID = gl_GlobalInvocationID;
    uint bin       = gl_LocalInvocationIndex;

    uvec2 imageResolution = uvec2(textureSize(inputImage, 0).xy);

#if D_HISTOGRAM_MODE == 0

    localHistogram[bin] = 0;
    memoryBarrierShared();
    barrier();

    if (globalID.x < imageResolution.x && globalID.y < imageResolution.y)
    {
        vec3 color = texelFetch(inputImage, ivec2(globalID.xy), 0).rgb;
        float luma = GetLuma(color);

        // Bin 0 collects pixels below the metering range, the reduction pass discards it so that
        // near-black pixels don't drag the log-average down. log2(0) is also undefined.
        uint destBin = 0u;

        if (luma > exp2(logMinLuminance))
        {
            float logLuma = log2(luma);
            float normalizedLogLuma = saturate((logLuma - logMinLuminance) / logLuminanceRange);

            destBin = uint(normalizedLogLuma * 254.0) + 1u;
        }

        // Accumulate into shared histogram
        atomicAdd(localHistogram[destBin], 1u);
    }

    // Ensure all threads finished writing
    memoryBarrierShared();
    barrier();

    // Accumulate shared histogram into global histogram
    if (localHistogram[bin] > 0u)
    {
        atomicAdd(histogram[bin], localHistogram[bin]);
    }
#else
    uint totalPixels = imageResolution.x * imageResolution.y;
    uint meteredPixels = totalPixels - min(histogram[0], totalPixels);

    // sum up weighted bin values
    localHistogram[bin] = histogram[bin] * bin;
    memoryBarrierShared();
    barrier();
    for (uint stride = 128; stride > 0; stride >>= 1)
    {
        if (bin < stride)
        {
            localHistogram[bin] += localHistogram[bin + stride];
        }
        memoryBarrierShared();
        barrier();
    }

    if(bin == 0)
    {
        uint weightedSum = localHistogram[0];
        float averageBin = meteredPixels > 0u ? float(weightedSum) / float(meteredPixels) : 0.0;
        float logLuma = max(averageBin - 1.0, 0.0) * (logLuminanceRange / 254.0) + logMinLuminance;
        outputLuminance = exp2(logLuma);
    }
#endif
}
