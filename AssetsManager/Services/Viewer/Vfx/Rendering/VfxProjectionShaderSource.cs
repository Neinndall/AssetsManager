namespace AssetsManager.Services.Viewer.Vfx.Rendering;

/// <summary>
/// UNLIT_DECAL preview, independent from the game-shader toggle. Like the game's permutations, the colour ramp is read
/// only by a decal without ALPHA_EROSION or MULT_PASS, the textureMult layer samples the decal uv through the
/// particle's textureMult transform, and erosion reads its map at the decal uv. Without a map the footprint is drawn
/// on the flat ground. Over a map (uTerrainMode) each decal covers its footprint's screen bounds and lands on the map
/// surface the terrain depth holds, fading by that surface's height like UNLIT_DECAL_VS.
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
uniform int uTerrainMode;
uniform vec2 uProjectionBand;
out vec2 vUv;
out vec2 vLookup;
out vec4 vColor;
out float vFade;
out float vErosionDrive;
out vec4 vErosionMixer;
flat out vec4 vUvMult;
flat out float vUvMultTurn;
flat out vec3 vCenter;
flat out vec2 vSize;
flat out float vTurn;
void main(){
    float c = cos(aRotFrame.x), s = sin(aRotFrame.x);
    vUv = vec2(aCorner.x+0.5, 0.5-aCorner.y);
    vFade = aLookup.z;
    vLookup = aLookup.xy;
    vColor = aColor;
    vErosionDrive = aUvErosion.y;
    vErosionMixer = vec4(aUvErosion.zw, aErosionMixerZW);
    vUvMult = aUvMult;
    vUvMultTurn = aUvMultDynamics.x;
    vCenter = aCenter;
    vSize = aSize;
    vTurn = aRotFrame.x;
    if(uTerrainMode == 0){
        vec2 local = aCorner * 2.0 * aSize;
        vec2 offset = vec2(local.x*c-local.y*s, local.x*s+local.y*c);
        gl_Position = uViewProj * vec4(aCenter.x+offset.x, 0.0, aCenter.z+offset.y, 1.0);
        return;
    }
    // The screen bounds of the footprint extruded over the height band the decal can reach.
    float reach = min(uProjectionBand.x + uProjectionBand.y, 20000.0);
    vec2 lo = vec2(1.0), hi = vec2(-1.0);
    bool behind = false;
    for(int i = 0; i < 8; i++){
        vec2 corner = vec2((i & 1) == 0 ? -1.0 : 1.0, (i & 2) == 0 ? -1.0 : 1.0) * aSize;
        vec2 offset = vec2(corner.x*c-corner.y*s, corner.x*s+corner.y*c);
        vec4 clip = uViewProj * vec4(aCenter.x+offset.x, aCenter.y + ((i & 4) == 0 ? -reach : reach), aCenter.z+offset.y, 1.0);
        if(clip.w <= 0.0001){ behind = true; break; }
        lo = min(lo, clip.xy / clip.w);
        hi = max(hi, clip.xy / clip.w);
    }
    if(behind){ lo = vec2(-1.0); hi = vec2(1.0); }
    lo = clamp(lo, vec2(-1.0), vec2(1.0));
    hi = clamp(hi, vec2(-1.0), vec2(1.0));
    gl_Position = vec4(mix(lo, hi, aCorner + 0.5), 0.0, 1.0);
}";
    internal const string TerrainFragment = "#define TERRAIN_DEPTH" + "\n" + Fragment;

    internal const string Fragment = VfxShaderSource.TextureSampling + @"
uniform sampler2D uTex;
uniform sampler2D uColorMap;
uniform sampler2D uTexMult;
uniform sampler2D uErosionTex;
uniform sampler2D uTerrainDepth;
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
uniform vec2 uUvTransformCenterMult;
uniform vec2 uUvScrollRateMult;
uniform int uFlipUMult;
uniform int uFlipVMult;
uniform float uAlphaCutoff;
uniform int uWireframePass;
uniform vec4 uWireframeColor;
uniform int uAddressMode;
uniform mat4 uInverseViewProj;
uniform vec2 uViewportSize;
uniform vec2 uProjectionBand;
in vec2 vUv;
in vec2 vLookup;
in vec4 vColor;
in float vFade;
in float vErosionDrive;
in vec4 vErosionMixer;
flat in vec4 vUvMult;
flat in float vUvMultTurn;
flat in vec3 vCenter;
flat in vec2 vSize;
flat in float vTurn;
out vec4 fragColor;
vec2 multUv(vec2 uv){
    vec2 centered = (uv - uUvTransformCenterMult) * vUvMult.zw;
    float s = sin(vUvMultTurn), c = cos(vUvMultTurn);
    centered = vec2(centered.x*c - centered.y*s, centered.x*s + centered.y*c);
    vec2 placed = centered + uUvTransformCenterMult + vUvMult.xy + uUvScrollRateMult;
    if(uFlipUMult != 0) placed.x = 1.0 - placed.x;
    if(uFlipVMult != 0) placed.y = 1.0 - placed.y;
    return placed;
}
void main(){
    if(uWireframePass != 0){ fragColor=uWireframeColor; return; }
    vec2 uv = vUv;
    float fade = vFade;
#ifdef TERRAIN_DEPTH
    {
        vec2 screen = gl_FragCoord.xy / max(uViewportSize, vec2(1.0));
        float depth = texture(uTerrainDepth, screen).r;
        if(depth >= 1.0) discard;
        vec4 surface = uInverseViewProj * vec4(screen * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
        vec3 world = surface.xyz / surface.w;
        vec2 offset = world.xz - vCenter.xz;
        float c = cos(vTurn), s = sin(vTurn);
        // Clamp the magnitude without losing the authored mirror on either axis.
        vec2 sizeSign = mix(vec2(1.0), vec2(-1.0), lessThan(vSize, vec2(0.0)));
        vec2 corner = vec2(offset.x*c + offset.y*s, -offset.x*s + offset.y*c) / (sizeSign * max(abs(2.0 * vSize), vec2(0.0001)));
        uv = vec2(corner.x + 0.5, 0.5 - corner.y);
        if(any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) discard;
        float gap = abs(world.y - vCenter.y);
        fade = gap <= uProjectionBand.x ? 1.0 : 1.0 - (gap - uProjectionBand.x) / max(uProjectionBand.y, 0.000001);
        gl_FragDepth = depth;
    }
#endif
    vec4 texel = uHasTex != 0 ? sampleAddressed(uTex,uv,uAddressMode) : vec4(1.0);
    if(uHasColor != 0) texel *= texture(uColorMap,vLookup);
    if(uHasTexMult != 0) texel *= sampleAddressed(uTexMult,multUv(uv),uAddressModeMult);
    vec4 lit = texel * vColor;
    lit.a *= max(fade, 0.0);
    if(uHasErosion != 0){
        vec4 erosionTexel = uHasErosionMap != 0 ? sampleAddressed(uErosionTex,uv,uErosionAddressMode) : uErosionDefault;
        float erosion = clamp(dot(erosionTexel, vErosionMixer), 0.0, 1.0);
        float upper = clamp((vErosionDrive - erosion + uErosionSliceWidth) / max(0.0001, uErosionFeatherIn), 0.0, 1.0);
        float lower = clamp((vErosionDrive - erosion) / max(0.0001, uErosionFeatherOut), 0.0, 1.0);
        lit.a *= max(upper - lower, 0.0);
    }
    if(lit.a < uAlphaCutoff) discard;
    fragColor = lit;
}";
}
