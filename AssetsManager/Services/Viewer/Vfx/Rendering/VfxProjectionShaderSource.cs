namespace AssetsManager.Services.Viewer.Vfx.Rendering;

/// <summary>
/// Flat-ground UNLIT_DECAL preview, independent from the game-shader toggle. Like the game's permutations, the colour
/// ramp is read only by a decal without ALPHA_EROSION or MULT_PASS, the textureMult layer samples the decal uv through
/// the particle's textureMult transform, and erosion reads its map at the decal uv.
/// </summary>
internal static class VfxProjectionShaderSource
{
    internal const string Vertex = @"
layout(location=0) in vec2 aCorner;
layout(location=1) in vec3 aCenter;
layout(location=2) in vec2 aSize;
layout(location=3) in vec4 aColor;
layout(location=4) in vec2 aRotFrame;
layout(location=7) in vec4 aLookup;
layout(location=8) in vec4 aUvErosion;
layout(location=9) in vec2 aErosionMixerZW;
layout(location=10) in vec4 aUvMult;
layout(location=11) in vec3 aUvMultDynamics;
uniform mat4 uViewProj;
uniform vec2 uUvTransformCenterMult;
uniform vec2 uUvScrollRateMult;
uniform int uFlipUMult;
uniform int uFlipVMult;
out vec2 vUv;
out vec2 vUvMult;
out vec2 vLookup;
out vec4 vColor;
out float vFade;
out float vErosionDrive;
out vec4 vErosionMixer;
void main(){
    vec2 local = aCorner * 2.0 * aSize;
    float c = cos(aRotFrame.x), s = sin(aRotFrame.x);
    vec2 offset = vec2(local.x*c-local.y*s, local.x*s+local.y*c);
    vec3 world = vec3(aCenter.x+offset.x, 0.0, aCenter.z+offset.y);
    vFade = aLookup.z;
    vUv = vec2(aCorner.x+0.5, 0.5-aCorner.y);
    vec2 centeredMult = (vUv - uUvTransformCenterMult) * aUvMult.zw;
    float multSin = sin(aUvMultDynamics.x), multCos = cos(aUvMultDynamics.x);
    centeredMult = vec2(centeredMult.x*multCos - centeredMult.y*multSin, centeredMult.x*multSin + centeredMult.y*multCos);
    vUvMult = centeredMult + uUvTransformCenterMult + aUvMult.xy + uUvScrollRateMult;
    if(uFlipUMult != 0) vUvMult.x = 1.0 - vUvMult.x;
    if(uFlipVMult != 0) vUvMult.y = 1.0 - vUvMult.y;
    vLookup = aLookup.xy;
    vColor = aColor;
    vErosionDrive = aUvErosion.y;
    vErosionMixer = vec4(aUvErosion.zw, aErosionMixerZW);
    gl_Position = uViewProj * vec4(world, 1.0);
}";
    internal const string Fragment = VfxShaderSource.TextureSampling + @"
uniform sampler2D uTex;
uniform sampler2D uColorMap;
uniform sampler2D uTexMult;
uniform sampler2D uErosionTex;
uniform int uHasTex;
uniform int uHasColor;
uniform int uHasTexMult;
uniform int uHasErosion;
uniform int uHasErosionMap;
uniform int uAddressModeMult;
uniform int uErosionAddressMode;
uniform vec4 uErosionDefault;
uniform float uErosionFeatherIn;
uniform float uErosionFeatherOut;
uniform float uErosionSliceWidth;
uniform float uAlphaCutoff;
uniform int uWireframePass;
uniform vec4 uWireframeColor;
uniform int uAddressMode;
in vec2 vUv;
in vec2 vUvMult;
in vec2 vLookup;
in vec4 vColor;
in float vFade;
in float vErosionDrive;
in vec4 vErosionMixer;
out vec4 fragColor;
void main(){
    if(uWireframePass != 0){ fragColor=uWireframeColor; return; }
    vec4 texel = uHasTex != 0 ? sampleAddressed(uTex,vUv,uAddressMode) : vec4(1.0);
    if(uHasColor != 0) texel *= texture(uColorMap,vLookup);
    if(uHasTexMult != 0) texel *= sampleAddressed(uTexMult,vUvMult,uAddressModeMult);
    vec4 lit = texel * vColor;
    lit.a *= max(vFade, 0.0);
    if(uHasErosion != 0){
        vec4 erosionTexel = uHasErosionMap != 0 ? sampleAddressed(uErosionTex,vUv,uErosionAddressMode) : uErosionDefault;
        float erosion = clamp(dot(erosionTexel, vErosionMixer), 0.0, 1.0);
        float upper = clamp((vErosionDrive - erosion + uErosionSliceWidth) / max(0.0001, uErosionFeatherIn), 0.0, 1.0);
        float lower = clamp((vErosionDrive - erosion) / max(0.0001, uErosionFeatherOut), 0.0, 1.0);
        lit.a *= max(upper - lower, 0.0);
    }
    if(lit.a < uAlphaCutoff) discard;
    fragColor = lit;
}";
}
