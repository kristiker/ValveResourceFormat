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
layout(std140) uniform PerLayerConstantBuffer_t {
	vec4 g_vWireframeColor;
} ;
layout(location=0) in vec3 PS_INPUT_gl_vPositionWs;
layout(location=0) out vec4 PS_OUTPUT_gl_vColor;
const float C_0d0 = 0.0;
const float C_1d0 = 1.0;
const vec3 C_vec3p0d0p = vec3(0.0);
const vec4 C_xx1m2m1 = vec4(0.0, 0.0, 0.0, 1.0);
void main()
{
	float flDist = distance(PS_INPUT_gl_vPositionWs, g_vCameraPositionWs);
	float H_j6vk5z = flDist * g_flInvFogRange;
	float param = H_j6vk5z + g_flNegFogStartOverFogRange;
	float misc3a = clamp(param, C_0d0, C_1d0);
	float flFog = pow(misc3a, g_flFogExponent);
	float misc2a = min(g_flFogMaxDensity, flFog);
	float misc3a1 = clamp(misc2a, C_0d0, C_1d0);
	vec3 misc3a2 = mix(C_vec3p0d0p, g_vFogColor, misc3a1);
	vec3 H_48eakq = vec3(g_flToneMapScalarLinear);
	vec3 H_yke8c51 = H_48eakq * misc3a2;
	vec4 H_rwwpo7 = C_xx1m2m1;
	H_rwwpo7.xyz = H_yke8c51.xyz;
	PS_OUTPUT_gl_vColor = H_rwwpo7;
}
