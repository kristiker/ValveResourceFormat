//ATTRIBMAP-00-20-5D-xx
#version 330 core
// LunarGOO output
#extension GL_ARB_separate_shader_objects : enable
#extension GL_ARB_shading_language_420pack : enable
layout(std140, binding = 0 ) uniform PerViewConstantBuffer_t {
	layout(row_major) mat4 g_matWorldToProjection;
	layout(row_major) mat4 g_matProjectionToWorld;
	layout(row_major) mat4 g_matWorldToView;
	layout(row_major) mat4 g_matViewToProjection;
	vec4 g_vInvProjRow3;
	vec4 g_vClipPlane0;
	float g_flToneMapScalarLinear;
	float g_flLightMapScalar;
	float g_flEnvMapScalar;
	float g_flToneMapScalarGamma;
	vec3 g_vCameraPositionWs;
	float g_flViewportMinZ;
	vec3 g_vCameraDirWs;
	float g_flViewportMaxZ;
	vec3 g_vCameraUpDirWs;
	float g_flTime;
	vec3 g_vDepthPsToVsConversion;
	float g_flNearPlane;
	float g_flFarPlane;
	float g_flLightBinnerFarPlane;
	vec2 g_vInvViewportSize;
	vec2 g_vViewportToGBufferRatio;
	vec2 g_vMorphTextureAtlasSize;
	vec4 g_vInvGBufferSize;
	vec2 g_vViewportOffset;
	vec2 g_vViewportSize;
	vec2 g_vRenderTargetSize;
	float g_flFogBlendToBackground;
	float g_flHenyeyGreensteinCoeff;
	vec3 g_vFogColor;
	float g_flNegFogStartOverFogRange;
	float g_flInvFogRange;
	float g_flFogMaxDensity;
	float g_flFogExponent;
	float g_flMod2xIdentity;
	float g_bStereoEnabled;
	float g_flStereoCameraIndex;
	float g_fInvViewportZRange;
	float g_fMinViewportZScaled;
	vec3 g_vMiddleEyePositionWs;
	float g_flPad2;
	layout(row_major) mat4 g_matWorldToProjectionMultiview[2];
	vec4 g_vCameraPositionWsMultiview[2];
	vec4 g_vFrameBufferCopyInvSizeAndUvScale;
	vec4 g_vCameraAngles;
	vec4 g_vWorldToCameraOffset;
	vec4 g_vWorldToCameraOffsetMultiview[2];
	vec4 g_vPerViewConstantExtraData0;
	vec4 g_vPerViewConstantExtraData1;
	vec4 g_vPerViewConstantExtraData2;
	vec4 g_vPerViewConstantExtraData3;
} ;
layout( binding = 18 ) uniform sampler2D g_tTransformTexture;
layout(std140) uniform PerLayerConstantBuffer_t {
	vec4 g_vWireframeColor;
} ;
layout(location=0) in vec3 VS_INPUT_gl_vPositionOs;
layout(location=1) in uvec4 VS_INPUT_gl_vBlendIndices;
layout(location=2) in vec2 VS_INPUT_gl_vTransformTextureUV;
out gl_PerVertex {
	vec4 gl_Position;
	float gl_ClipDistance[1];
} ;
layout(location=0) out vec3 PS_INPUT_gl_vPositionWs;
const float C_0d0 = 0.0;
const float C_2d0 = 2.0;
const float C_4d0 = 4.0;
const float C_0d5 = 0.5;
const ivec2 C_ivec2p1ca0p = ivec2(1, 0);
const ivec2 C_ivec2p2ca0p = ivec2(2, 0);
const float C_1d0 = 1.0;
void main()
{
	gl_ClipDistance[0] = C_0d0;
	float H_33xh0u1 = float(ivec4(VS_INPUT_gl_vBlendIndices).x);
	float H_twpgh9 = H_33xh0u1 + C_2d0;
	float H_3qvjkl1 = H_twpgh9 * C_4d0;
	float H_u77z731 = H_3qvjkl1 + C_0d5;
	float H_jxjzkk = H_u77z731 * g_vInvGBufferSize.z;
	vec2 H_bqvuk21 = vec2(H_jxjzkk, C_0d0);
	vec2 vBlendTransform0UV = H_bqvuk21 + VS_INPUT_gl_vTransformTextureUV;
	float flWrapLines = floor(vBlendTransform0UV.x);
	float H_7mdp57 = vBlendTransform0UV.x - flWrapLines;
	float H_wgm8gx = flWrapLines * g_vInvGBufferSize.w;
	float H_06afd41 = H_wgm8gx + vBlendTransform0UV.y;
	vec2 H_crnj9e1 = vec2(H_7mdp57, H_06afd41);
	vec4 vMatObjectToWorldRow = textureLod(g_tTransformTexture, H_crnj9e1, C_0d0);
	vec4 vMatObjectToWorldRow1 = textureLodOffset(g_tTransformTexture, H_crnj9e1, C_0d0, C_ivec2p1ca0p);
	vec4 vMatObjectToWorldRow2 = textureLodOffset(g_tTransformTexture, H_crnj9e1, C_0d0, C_ivec2p2ca0p);
	vec4 vAnimationControlWord = textureLod(g_tTransformTexture, VS_INPUT_gl_vTransformTextureUV, C_0d0);
	vec3 H_u8espd = VS_INPUT_gl_vPositionOs * vAnimationControlWord.zzz;
	vec4 H_2f7gnb = vec4(H_u8espd.x, H_u8espd.y, H_u8espd.z, C_1d0);
	float dotres = dot(H_2f7gnb, vMatObjectToWorldRow);
	float dotres1 = dot(H_2f7gnb, vMatObjectToWorldRow1);
	float dotres2 = dot(H_2f7gnb, vMatObjectToWorldRow2);
	vec3 H_3cij4j1 = vec3(dotres, dotres1, dotres2);
	vec4 H_vokf33 = vec4(dotres, dotres1, dotres2, C_1d0);
	vec4 H_a5xsfe = H_vokf33 + g_vWorldToCameraOffset;
	float dotres3 = dot(H_a5xsfe, g_matWorldToProjection[0]);
	float dotres4 = dot(H_a5xsfe, g_matWorldToProjection[1]);
	float dotres5 = dot(H_a5xsfe, g_matWorldToProjection[2]);
	float dotres6 = dot(H_a5xsfe, g_matWorldToProjection[3]);
	float H_aenq1s = dot(H_vokf33, g_vClipPlane0);
	PS_INPUT_gl_vPositionWs = H_3cij4j1;
	float H_rdf2pt1 = C_0d0 - dotres4;
	float H_v600s41 = C_2d0 * dotres5;
	float H_2td2am = H_v600s41 - dotres6;
	vec4 H_mhfqwn1 = vec4(dotres3, H_rdf2pt1, H_2td2am, dotres6);
	gl_Position = H_mhfqwn1;
	gl_ClipDistance[0] = H_aenq1s;
}
