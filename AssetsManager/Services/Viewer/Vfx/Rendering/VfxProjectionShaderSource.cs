namespace AssetsManager.Services.Viewer.Vfx.Rendering;

/// <summary>Flat-ground UNLIT_DECAL preview, independent from the game-shader toggle.</summary>
internal static class VfxProjectionShaderSource
{
    internal const string Vertex = @"
layout(location=0) in vec2 aCorner;
layout(location=1) in vec3 aCenter;
layout(location=2) in vec2 aSize;
layout(location=3) in vec4 aColor;
layout(location=4) in vec2 aRotFrame;
layout(location=7) in vec4 aLookup;
uniform mat4 uViewProj;
out vec2 vUv;
out vec2 vLookup;
out vec4 vColor;
out float vFade;
void main(){
    vec2 local = aCorner * 2.0 * aSize;
    float c = cos(aRotFrame.x), s = sin(aRotFrame.x);
    vec2 offset = vec2(local.x*c-local.y*s, local.x*s+local.y*c);
    vec3 world = vec3(aCenter.x+offset.x, 0.0, aCenter.z+offset.y);
    vFade = aLookup.z;
    vUv = vec2(aCorner.x+0.5, 0.5-aCorner.y);
    vLookup = aLookup.xy;
    vColor = aColor;
    gl_Position = uViewProj * vec4(world, 1.0);
}";
    internal const string Fragment = VfxShaderSource.TextureSampling + @"
uniform sampler2D uTex;
uniform sampler2D uColorMap;
uniform int uHasTex;
uniform int uHasColor;
uniform float uAlphaCutoff;
uniform int uWireframePass;
uniform vec4 uWireframeColor;
uniform int uAddressMode;
in vec2 vUv;
in vec2 vLookup;
in vec4 vColor;
in float vFade;
out vec4 fragColor;
void main(){
    if(uWireframePass != 0){ fragColor=uWireframeColor; return; }
    vec4 texel = uHasTex != 0 ? sampleAddressed(uTex,vUv,uAddressMode) : vec4(1.0);
    if(uHasColor != 0) texel *= texture(uColorMap,vLookup);
    vec4 lit = texel * vColor;
    lit.a *= vFade;
    if(lit.a < uAlphaCutoff) discard;
    fragColor = lit;
}";
}
