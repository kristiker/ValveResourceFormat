#version 460

#include "common/utils.glsl"
#include "common/fullbright.glsl"
#include "common/ViewConstants.glsl"
#include "common/fog.glsl"

#define renderMode_Color 0
#define renderMode_Normals 0
#define renderMode_VertexColor 0

in vec4 vtxColor;
in vec3 vtxNormal;
in vec3 vtxPos;
in vec3 camPos;

out vec4 outputColor;

const float shadingStrength = 0.8;

uniform bool g_bNormalShaded;
uniform bool g_bTriplanarMapping;
uniform sampler2D g_tColor; // SrgbRead(true)

// "p" point being textured
// "n" surface normal at "p"
// "k" controls the sharpness of the blending in the transitions areas
// "s" texture sampler
vec4 boxmap( in sampler2D s, in vec3 p, in vec3 n, in float k )
{
    // project+fetch
    vec4 x = texture( s, p.zy );
    vec4 y = texture( s, p.xz * vec2(1,-1) );
    vec4 z = texture( s, p.yx );

    // blend weights
    vec3 w = pow( abs(n), vec3(k) );
    // blend and return
    return (x*w.x + y*w.y + z*w.z) / (w.x + w.y + w.z);
}

#extension GL_EXT_fragment_shader_barycentric  : require

float WireFrame(in float Thickness, in float Falloff)
{
	const vec3 BaryCoord = gl_BaryCoordEXT;

	const vec3 dBaryCoordX = dFdxFine(BaryCoord);
	const vec3 dBaryCoordY = dFdyFine(BaryCoord);
	const vec3 dBaryCoord  = sqrt(dBaryCoordX*dBaryCoordX + dBaryCoordY*dBaryCoordY);

	const vec3 dFalloff   = dBaryCoord * Falloff;
	const vec3 dThickness = dBaryCoord * Thickness;

	const vec3 Remap = smoothstep(dThickness, dThickness + dFalloff, BaryCoord);
	const float ClosestEdge = min(min(Remap.x, Remap.y), Remap.z);

	return 1.0 - ClosestEdge;
}


void main(void)
{
    outputColor = vtxColor;
    vec3 toolTexture = vec3(1.0);

    if(g_bNormalShaded)
    {
        vec3 viewDir = normalize(vtxPos - camPos);

        if (g_bTriplanarMapping)
        {
            const float uvScale = 0.0625;
            outputColor = boxmap(g_tColor, vtxPos * uvScale, vtxNormal, 1.0);
            toolTexture = outputColor.rgb;
        }

        vec3 lighting = CalculateFullbrightLighting(outputColor.rgb, vtxNormal, viewDir);
        outputColor = vec4(lighting, vtxColor.a);
    }

    float wireframe = WireFrame(2, 1);
    if (wireframe > 0.0)
    {
        outputColor = vec4(vec3(1.0), wireframe);
    }

    if (!gl_FrontFacing) {
        outputColor.rgba *= 0.75;
    }

    ApplyFog(outputColor.rgb, vtxPos);

    if (g_iRenderMode == renderMode_Color)
    {
        outputColor = vec4(toolTexture, 1.0);
    }
    else if (g_iRenderMode == renderMode_Normals)
    {
        outputColor = vec4(SrgbGammaToLinear(PackToColor(vtxNormal)), 1.0);
    }
    else if (g_iRenderMode == renderMode_VertexColor)
    {
        outputColor = vec4(SrgbGammaToLinear(vtxColor.rgb), 1.0);
    }
}
