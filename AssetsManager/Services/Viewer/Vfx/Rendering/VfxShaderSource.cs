namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    internal static class VfxShaderSource
    {
        // Numeric uPrimitiveKind branches mirror VfxPrimitiveKind. The contract is
        // guarded by PrimitiveEnumKeepsTheShaderInterfaceContract in Benchmark.
        internal const string MeshVertex = @"
layout(location=0) in vec3 aPos;
layout(location=1) in vec2 aUv;
layout(location=2) in vec4 aColor;
uniform mat4 uViewProj;
uniform vec3 uWorldPos;
uniform vec3 uScale;
uniform vec3 uRotation;
uniform vec2 uEmitterUvOffset;
uniform vec2 uTexDiv;
uniform vec2 uTexSize;
uniform float uFrame;
uniform vec2 uUvTransformCenter;
uniform int uAddressMode;
uniform int uClampUv;
uniform vec2 uUvOffsetMult;
uniform vec2 uUvScaleMult;
uniform float uUvRotationMult;
uniform float uTextureMultFrame;
uniform vec2 uEmitterUvOffsetMult;
uniform vec2 uTexDivMult;
uniform vec2 uTexSizeMult;
uniform vec2 uUvTransformCenterMult;
uniform vec3 uPlacementRight;
uniform vec3 uPlacementUp;
uniform vec3 uPlacementForward;
uniform int uFlipU;
uniform int uFlipV;
uniform int uFlipUMult;
uniform int uFlipVMult;
uniform int uAddressModeMult;
uniform int uClampUvMult;
uniform vec2 uBirthUvOffset;
uniform vec2 uUvScale;
uniform float uUvRotation;
out vec2 vCell;
out vec2 vCellMult;
out vec2 vLocalUv;
out vec2 vLocalUvMult;
out vec4 vMeshColor;
out vec3 vColorDynamics;

void main(){
    vec3 scaled = aPos * uScale;
    float sz = sin(uRotation.z); float cz = cos(uRotation.z);
    vec3 local = vec3(scaled.x * cz - scaled.y * sz, scaled.x * sz + scaled.y * cz, scaled.z);
    float sx = sin(uRotation.x); float cx = cos(uRotation.x);
    local = vec3(local.x, local.y * cx - local.z * sx, local.y * sx + local.z * cx);
    float sy = sin(uRotation.y); float cy = cos(uRotation.y);
    local = vec3(local.x * cy + local.z * sy, local.y, -local.x * sy + local.z * cy);
    vec3 p = uPlacementRight * local.x + uPlacementUp * local.y + uPlacementForward * local.z + uWorldPos;
    gl_Position = uViewProj * vec4(p, 1.0);
    vec2 baseUv = aUv;
    vec2 centeredUv = (baseUv - uUvTransformCenter) * uUvScale;
    float uvSin = sin(uUvRotation); float uvCos = cos(uUvRotation);
    centeredUv = vec2(centeredUv.x * uvCos - centeredUv.y * uvSin,
                      centeredUv.x * uvSin + centeredUv.y * uvCos);
    baseUv = centeredUv + uUvTransformCenter + uBirthUvOffset;
    if (uFlipU != 0) baseUv.x = 1.0 - baseUv.x;
    if (uFlipV != 0) baseUv.y = 1.0 - baseUv.y;
    vec2 mainScroll = uEmitterUvOffset;
    vLocalUv = baseUv + mainScroll;
    vec2 mainDiv = max(uTexDiv, vec2(1.0));
    float mainCols = mainDiv.x;
    float frame = floor(uFrame + 0.0001);
    vec2 mainCell = vec2(mod(frame, mainCols), floor(frame / mainCols));
    vCell = mainCell;
    vec2 multUv = aUv;
    vec2 centeredMultUv = (multUv - uUvTransformCenterMult) * uUvScaleMult;
    float multSin = sin(uUvRotationMult); float multCos = cos(uUvRotationMult);
    centeredMultUv = vec2(centeredMultUv.x * multCos - centeredMultUv.y * multSin,
                          centeredMultUv.x * multSin + centeredMultUv.y * multCos);
    multUv = centeredMultUv + uUvTransformCenterMult + uUvOffsetMult;
    if (uFlipUMult != 0) multUv.x = 1.0 - multUv.x;
    if (uFlipVMult != 0) multUv.y = 1.0 - multUv.y;
    vec2 multScroll = uEmitterUvOffsetMult;
    vLocalUvMult = multUv + multScroll;
    vec2 multDiv = max(uTexDivMult, vec2(1.0));
    float multCols = multDiv.x;
    float multFrame = floor(uTextureMultFrame + 0.0001);
    vec2 multCell = vec2(mod(multFrame, multCols), floor(multFrame / multCols));
    vCellMult = multCell;
    vMeshColor = aColor;
    vColorDynamics = vec3(1.0, 0.0, 0.0);
}";

        internal const string ParticleVertex = @"
layout(location=0) in vec2 aCorner;
layout(location=1) in vec3 aCenter;
layout(location=2) in vec2 aSize;
layout(location=3) in vec4 aColor;
layout(location=4) in vec2 aRotFrame;
layout(location=5) in vec4 aAgeVelX;
layout(location=6) in vec3 aRotation;
layout(location=7) in vec2 aUvOffset;
layout(location=8) in vec2 aUvScale;
layout(location=9) in float aUvRotation;
layout(location=10) in float aErosionDrive;
layout(location=11) in vec4 aErosionMixer;
layout(location=12) in vec2 aUvOffsetMult;
layout(location=13) in vec2 aUvScaleMult;
layout(location=14) in float aUvRotationMult;
layout(location=15) in vec2 aTextureMultFramePalette;
uniform mat4 uViewProj;
uniform vec3 uCamRight;
uniform vec3 uCamUp;
uniform vec2 uTexDiv;
uniform vec2 uTexSize;
uniform vec2 uEmitterUvOffset;
uniform vec2 uTexDivMult;
uniform vec2 uTexSizeMult;
uniform vec2 uUvScrollRateMult;
uniform int uDirectionOriented;
uniform int uArbitraryQuad;
uniform int uIsGroundLayer;
uniform int uPrimitiveKind;
uniform int uFlipU;
uniform int uFlipV;
uniform int uFlipUMult;
uniform int uFlipVMult;
uniform int uClampUv;
uniform int uClampUvMult;
uniform int uAddressMode;
uniform int uAddressModeMult;
uniform vec2 uUvTransformCenter;
uniform vec2 uUvTransformCenterMult;
uniform vec3 uPlacementRight;
uniform vec3 uPlacementUp;
uniform vec3 uPlacementForward;
out vec2 vCell;
out vec2 vCellMult;
out vec4 vColor;
out float vErosionDrive;
out vec4 vErosionMixer;
out vec2 vLocalUv;
out vec2 vLocalUvMult;
out float vPaletteSelector;
out vec3 vColorDynamics;
vec3 rotateEuler(vec3 p, vec3 r){
    float sz = sin(r.z); float cz = cos(r.z);
    p = vec3(p.x * cz - p.y * sz, p.x * sz + p.y * cz, p.z);
    float sx = sin(r.x); float cx = cos(r.x);
    p = vec3(p.x, p.y * cx - p.z * sx, p.y * sx + p.z * cx);
    float sy = sin(r.y); float cy = cos(r.y);
    return vec3(p.x * cy + p.z * sy, p.y, -p.x * sy + p.z * cy);
}

void main(){
    bool rayPrimitive = uPrimitiveKind == 7 || uPrimitiveKind == 8;
    bool trailPrimitive = uPrimitiveKind == 5 || uPrimitiveKind == 6;
    float rotation = (uArbitraryQuad != 0 || rayPrimitive || trailPrimitive) ? 0.0 : aRotFrame.x;
    vec3 localRight = rotateEuler(vec3(1.0, 0.0, 0.0), aRotation);
    vec3 localUp = rotateEuler(vec3(0.0, 1.0, 0.0), aRotation);
    vec3 localForward = rotateEuler(vec3(0.0, 0.0, 1.0), aRotation);
    vec3 placedRight = uPlacementRight * localRight.x + uPlacementUp * localRight.y + uPlacementForward * localRight.z;
    vec3 placedUp = uPlacementRight * localUp.x + uPlacementUp * localUp.y + uPlacementForward * localUp.z;
    vec3 placedForward = uPlacementRight * localForward.x + uPlacementUp * localForward.y + uPlacementForward * localForward.z;
    vec3 right = uArbitraryQuad != 0 ? placedRight : uCamRight;
    vec3 up = uArbitraryQuad != 0 ? placedUp : uCamUp;
    vec3 cameraForward = normalize(cross(uCamRight, uCamUp));

    if (rayPrimitive) {
        vec3 vel = aAgeVelX.yzw;
        float lenSq = dot(vel, vel);
        up = lenSq > 0.0001 ? vel * inversesqrt(lenSq) : normalize(placedForward);
        vec3 side = cross(up, cameraForward);
        if (dot(side, side) < 0.0001) side = cross(up, uCamUp);
        if (dot(side, side) < 0.0001) side = cross(up, uCamRight);
        right = normalize(side);
    } else if (uDirectionOriented != 0) {
        vec3 vel = aAgeVelX.yzw;
        float lenSq = dot(vel, vel);
        if (lenSq > 0.0001) {
            vec3 dir = vel * inversesqrt(lenSq);
            vec3 side = uArbitraryQuad != 0 ? cross(placedForward, dir) : cross(dir, cameraForward);
            if (dot(side, side) < 0.0001)
                side = uArbitraryQuad != 0 ? cross(placedUp, dir) : cross(dir, uCamUp);
            if (dot(side, side) < 0.0001)
                side = uArbitraryQuad != 0 ? cross(placedRight, dir) : cross(dir, uCamRight);
            right = normalize(side);
            up = dir;
        } else {
            right = placedRight;
            up = placedUp;
        }
    }

    float s = sin(rotation);
    float c = cos(rotation);
    vec2 rc = vec2(aCorner.x * c - aCorner.y * s, aCorner.x * s + aCorner.y * c);
    if (uPrimitiveKind == 0) rc *= 2.0;
    vec3 world;
    if (rayPrimitive) {
        float alongRay = aCorner.y + 0.5;
        float rayLength = aSize.y;
        if (aCenter.y > 0.0 && up.y < -0.00001) {
            float groundDistance = aCenter.y / -up.y;
            if (groundDistance > rayLength && groundDistance <= rayLength * 1.25)
                rayLength = groundDistance + 2.0;
        }
        world = aCenter + up * (alongRay * rayLength) + right * (rc.x * aSize.x);
    } else if (trailPrimitive) {
        world = aCenter;
    } else if (uIsGroundLayer != 0 || uPrimitiveKind == 9) {
        vec3 groundForward = uArbitraryQuad != 0 ? (uPlacementRight * localUp.x + uPlacementUp * localUp.y + uPlacementForward * localUp.z) : vec3(0.0, 0.0, 1.0);
        vec3 groundRight = uArbitraryQuad != 0 ? placedRight : vec3(1.0, 0.0, 0.0);
        if (dot(cross(groundRight, groundForward), vec3(0.0, 1.0, 0.0)) < 0.0) {
            vec3 authoredRight = groundRight;
            groundRight = -groundForward;
            groundForward = -authoredRight;
        }
        world = aCenter + groundRight * (rc.x * aSize.x) + groundForward * (rc.y * aSize.y) + vec3(0.0, 0.02, 0.0);
    } else {
        world = aCenter + right * (rc.x * aSize.x) + up * (rc.y * aSize.y);
    }
    gl_Position = uViewProj * vec4(world, 1.0);
    vec2 cell = aCorner + vec2(0.5, 0.5);
    float cols = max(uTexDiv.x, 1.0);
    float rows = max(uTexDiv.y, 1.0);
    float frame = floor(aRotFrame.y + 0.0001);
    float fx = mod(frame, cols);
    float fy = floor(frame / cols);
    vec2 localUv = trailPrimitive
        ? aCorner
        : vec2(cell.x, 1.0 - cell.y);
    vec2 centeredUv = (localUv - uUvTransformCenter) * aUvScale;
    float uvSin = sin(aUvRotation); float uvCos = cos(aUvRotation);
    centeredUv = vec2(centeredUv.x * uvCos - centeredUv.y * uvSin,
                      centeredUv.x * uvSin + centeredUv.y * uvCos);
    localUv = centeredUv + uUvTransformCenter + aUvOffset;
    if (uFlipU != 0) localUv.x = 1.0 - localUv.x;
    if (uFlipV != 0) localUv.y = 1.0 - localUv.y;
    vec2 scroll = uEmitterUvOffset;
    vLocalUv = localUv + scroll;
    vCell = vec2(fx, fy);
    vec2 multUv = trailPrimitive
        ? aCorner
        : vec2(cell.x, 1.0 - cell.y);
    vec2 centeredMultUv = (multUv - uUvTransformCenterMult) * aUvScaleMult;
    float multSin = sin(aUvRotationMult); float multCos = cos(aUvRotationMult);
    centeredMultUv = vec2(centeredMultUv.x * multCos - centeredMultUv.y * multSin,
                          centeredMultUv.x * multSin + centeredMultUv.y * multCos);
    multUv = centeredMultUv + uUvTransformCenterMult + aUvOffsetMult;
    if (uFlipUMult != 0) multUv.x = 1.0 - multUv.x;
    if (uFlipVMult != 0) multUv.y = 1.0 - multUv.y;
    vec2 multScroll = uUvScrollRateMult;
    vLocalUvMult = multUv + multScroll;
    vec2 multDiv = max(uTexDivMult, vec2(1.0));
    float multCols = multDiv.x;
    float multFrame = floor(aTextureMultFramePalette.x + 0.0001);
    vec2 multCell = vec2(mod(multFrame, multCols), floor(multFrame / multCols));
    vCellMult = multCell;
    vColor = aColor;
    vPaletteSelector = aTextureMultFramePalette.y;
    vErosionDrive = aErosionDrive;
    vErosionMixer = aErosionMixer;
    vColorDynamics = vec3(aAgeVelX.x, length(aAgeVelX.yzw), aRotFrame.y);
}";


        private const string TextureSampling = @"
uniform int uClampUv;
uniform int uClampUvMult;
uniform vec2 uTexDiv;
uniform vec2 uTexSize;
uniform vec2 uTexDivMult;
uniform vec2 uTexSizeMult;
vec2 atlasUv(vec2 local, vec2 cell, vec2 divisions, vec2 size, int mode){
    vec2 held = mode >= 2 ? clamp(local, vec2(0.0), vec2(1.0))
        : mode == 1 ? vec2(1.0) - abs(mod(local, vec2(2.0)) - vec2(1.0))
        : fract(local);
    vec2 div = max(divisions, vec2(1.0));
    vec2 inset = min(0.5 / max(size, vec2(1.0)), 0.5 / div);
    return clamp((cell + held) / div, cell / div + inset, (cell + vec2(1.0)) / div - inset);
}
";

        internal const string MeshFragment = TextureSampling + @"
in vec2 vCell;
in vec2 vCellMult;
in vec2 vLocalUv;
in vec2 vLocalUvMult;
in vec4 vMeshColor;
in vec3 vColorDynamics;
uniform int uIsDistortion;
uniform sampler2D uDistortionTex;
uniform sampler2D uSceneTex;
uniform float uDistortionStrength;
uniform sampler2D uTex;
uniform int uHasTex;
uniform sampler2D uTexMult;
uniform int uHasTexMult;
uniform int uAddressMode;
uniform int uAddressModeMult;
uniform vec4 uColor;
uniform float uAlphaCutoff;
uniform int uAlphaTest;
uniform float uEmissiveStrength;
uniform int uIsMultiply;
uniform sampler2D uColorMap;
uniform int uHasColor;
uniform int uColorRenderFlags;
uniform sampler2D uPaletteMap;
uniform int uHasPalette;
uniform int uPaletteCount;
uniform float uPaletteSelector;
uniform vec4 uPaletteMixMask;
uniform int uIsAdditive;
uniform vec4 uModulationFactor;
uniform int uColorLookUpTypeX;
uniform int uColorLookUpTypeY;
uniform vec2 uColorLookUpScales;
uniform vec2 uColorLookUpOffsets;
uniform sampler2D uErosionTex;
uniform int uHasErosion;
uniform float uErosionDrive;
uniform float uErosionFeatherIn;
uniform float uErosionFeatherOut;
uniform vec4 uErosionMixer;
uniform sampler2D uSceneDepthTex;
uniform int uHasSoftParticle;
uniform vec4 uSoftParticleParams;
uniform vec2 uViewportSize;
uniform sampler2D uReflectionTex;
uniform int uHasReflection;
uniform vec2 uReflectionOpacity;
uniform vec4 uReflectionColor;
out vec4 fragColor;
float addressMask(vec2 uv, int mode){
    if (mode != 3) return 1.0;
    return all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0))) ? 1.0 : 0.0;
}
float colorLookUpDriver(int type){
    if (type == 1) return vColorDynamics.x;
    if (type == 2) return vColorDynamics.y;
    if (type == 3) return vColorDynamics.z;
    return 1.0;
}
vec4 applyParticleColor(vec4 texel){
    if (uHasColor == 0) return texel;
    if (uColorLookUpTypeX != 0 || uColorLookUpTypeY != 0) {
        vec2 colorUv = vec2(
            colorLookUpDriver(uColorLookUpTypeX) * uColorLookUpScales.x,
            colorLookUpDriver(uColorLookUpTypeY) * uColorLookUpScales.y) + uColorLookUpOffsets;
        vec4 colorTex = texture(uColorMap, colorUv);
        texel.rgb = colorTex.rgb;
        texel.a *= colorTex.a;
        return texel;
    }
    vec4 colorTex = texture(uColorMap, vLocalUv);
    if ((uColorRenderFlags & 1) != 0) {
        texel.rgb *= colorTex.rgb;
        texel.a *= colorTex.a;
    } else if (uIsAdditive != 0) {
        texel.rgb += colorTex.rgb * colorTex.a;
        texel.a = max(texel.a, colorTex.a);
    } else {
        texel.rgb = mix(texel.rgb, colorTex.rgb, colorTex.a);
        texel.a = max(texel.a, colorTex.a * 0.5);
    }
    return texel;
}
void main(){
    vec2 vUv = atlasUv(vLocalUv, vCell, uTexDiv, uTexSize, uClampUv != 0 ? 2 : uAddressMode);
    vec2 vUvMult = atlasUv(vLocalUvMult, vCellMult, uTexDivMult, uTexSizeMult, uClampUvMult != 0 ? 2 : uAddressModeMult);
    vec4 texel = (uHasTex != 0)
        ? texture(uTex, vUv) * addressMask(vLocalUv, uAddressMode)
        : vec4(0.0);
    texel = applyParticleColor(texel);
    if (uHasPalette != 0) {
        float paletteCoverage = texel.a;
        float paletteIndex = dot(texel, uPaletteMixMask);
        float paletteU = clamp(paletteIndex, 0.0, 1.0);
        float paletteV = clamp((uPaletteSelector + 0.5) / max(float(uPaletteCount), 1.0), 0.0, 1.0);
        vec4 palette = texture(uPaletteMap, vec2(paletteU, paletteV));
        if (uIsAdditive != 0) {
            texel.rgb = palette.rgb * max(texel.a, palette.a);
        } else {
            texel.rgb = mix(texel.rgb, palette.rgb, palette.a * texel.a);
        }
        texel.a = paletteCoverage;
    }
    if (uHasTexMult != 0) {
        vec4 mult = texture(uTexMult, vUvMult) * addressMask(vLocalUvMult, uAddressModeMult);
        texel.rgb *= mult.rgb;
        texel.a *= mult.a;
    }
    if (uHasErosion != 0) {
        float erosion = clamp(dot(texture(uErosionTex, vUv), uErosionMixer), 0.0, 1.0);
        float featherIn = max(0.0001, uErosionFeatherIn);
        float featherOut = max(0.0001, uErosionFeatherOut);
        float upper = clamp((uErosionDrive - erosion + 1.0) / featherIn, 0.0, 1.0);
        float lower = clamp((uErosionDrive - erosion) / featherOut, 0.0, 1.0);
        texel.a *= clamp(upper - lower, 0.0, 1.0);
    }
    if (uHasSoftParticle != 0) {
        vec2 sceneUv = gl_FragCoord.xy / max(uViewportSize, vec2(1.0));
        float depthGap = (texture(uSceneDepthTex, sceneUv).r - gl_FragCoord.z) * 1000.0;
        float fadeIn = uSoftParticleParams.y > 0.0
            ? smoothstep(uSoftParticleParams.x, uSoftParticleParams.x + uSoftParticleParams.y, depthGap)
            : 1.0;
        float fadeOut = uSoftParticleParams.w > 0.0
            ? 1.0 - smoothstep(uSoftParticleParams.z, uSoftParticleParams.z + uSoftParticleParams.w, -depthGap)
            : 1.0;
        float fade = fadeIn * fadeOut;
        if (uIsAdditive != 0) {
            texel.rgb *= fade;
        } else {
            texel.a *= fade;
        }
    }
    if (uHasReflection != 0) {
        vec4 reflection = texture(uReflectionTex, vUv);
        float edge = clamp(length(vLocalUv - vec2(0.5)) * 1.4142, 0.0, 1.0);
        float opacity = mix(uReflectionOpacity.x, uReflectionOpacity.y, edge);
        texel.rgb = mix(texel.rgb, reflection.rgb * uReflectionColor.rgb, clamp(opacity * reflection.a, 0.0, 1.0));
    }
    vec4 authoredColor = uColor * vMeshColor * uModulationFactor;
    float effectiveAlpha = texel.a * authoredColor.a;
    if (uAlphaTest != 0 && effectiveAlpha < uAlphaCutoff) discard;
    if (uIsDistortion != 0) {
        vec4 normalSample = texture(uDistortionTex, vUv);
        float mask = normalSample.a * effectiveAlpha;
        vec2 normalOffset = normalSample.rg * 2.0 - vec2(1.0);
        vec2 sceneUv = gl_FragCoord.xy / max(uViewportSize, vec2(1.0));
        sceneUv = clamp(sceneUv + normalOffset * uDistortionStrength * mask * vec2(uViewportSize.y / max(uViewportSize.x, 1.0), 1.0), vec2(0.0), vec2(1.0));
        vec4 refracted = texture(uSceneTex, sceneUv);
        fragColor = vec4(refracted.rgb, mask);
        return;
    }
    fragColor = texel * authoredColor;
    if (uIsAdditive == 1 || uIsMultiply != 0)
        fragColor.rgb *= authoredColor.a;
    fragColor.rgb *= uEmissiveStrength;
}";

        internal const string ParticleFragment = TextureSampling + @"
in vec2 vCell;
in vec2 vCellMult;
in vec4 vColor;
in float vErosionDrive;
in vec4 vErosionMixer;
in vec2 vLocalUv;
in vec2 vLocalUvMult;
in float vPaletteSelector;
in vec3 vColorDynamics;
uniform sampler2D uTex;
uniform int uHasTex;
uniform sampler2D uTexMult;
uniform int uHasTexMult;
uniform int uAddressMode;
uniform int uAddressModeMult;
uniform int uIsDistortion;
uniform sampler2D uDistortionTex;
uniform sampler2D uSceneTex;
uniform vec2 uViewportSize;
uniform float uDistortionStrength;
uniform float uAlphaCutoff;
uniform int uAlphaTest;
uniform float uEmissiveStrength;
uniform int uIsMultiply;
uniform sampler2D uColorMap;
uniform int uHasColor;
uniform int uColorRenderFlags;
uniform sampler2D uPaletteMap;
uniform int uHasPalette;
uniform int uPaletteCount;
uniform vec4 uPaletteMixMask;
uniform int uIsAdditive;
uniform vec4 uModulationFactor;
uniform int uColorLookUpTypeX;
uniform int uColorLookUpTypeY;
uniform vec2 uColorLookUpScales;
uniform vec2 uColorLookUpOffsets;
uniform sampler2D uErosionTex;
uniform int uHasErosion;
uniform float uErosionFeatherIn;
uniform float uErosionFeatherOut;
uniform sampler2D uSceneDepthTex;
uniform int uHasSoftParticle;
uniform vec4 uSoftParticleParams;
uniform sampler2D uReflectionTex;
uniform int uHasReflection;
uniform vec2 uReflectionOpacity;
uniform vec4 uReflectionColor;
out vec4 fragColor;
float addressMask(vec2 uv, int mode){
    if (mode != 3) return 1.0;
    return all(greaterThanEqual(uv, vec2(0.0))) && all(lessThanEqual(uv, vec2(1.0))) ? 1.0 : 0.0;
}
float colorLookUpDriver(int type){
    if (type == 1) return vColorDynamics.x;
    if (type == 2) return vColorDynamics.y;
    if (type == 3) return vColorDynamics.z;
    return 1.0;
}
vec4 applyParticleColor(vec4 tex){
    if (uHasColor == 0) return tex;
    if (uColorLookUpTypeX != 0 || uColorLookUpTypeY != 0) {
        vec2 colorUv = vec2(
            colorLookUpDriver(uColorLookUpTypeX) * uColorLookUpScales.x,
            colorLookUpDriver(uColorLookUpTypeY) * uColorLookUpScales.y) + uColorLookUpOffsets;
        vec4 colorTex = texture(uColorMap, colorUv);
        tex.rgb = colorTex.rgb;
        tex.a *= colorTex.a;
        return tex;
    }
    vec4 colorTex = texture(uColorMap, vLocalUv);
    if ((uColorRenderFlags & 1) != 0) {
        tex.rgb *= colorTex.rgb;
        tex.a *= colorTex.a;
    } else if (uIsAdditive != 0) {
        tex.rgb += colorTex.rgb * colorTex.a;
        tex.a = max(tex.a, colorTex.a);
    } else {
        tex.rgb = mix(tex.rgb, colorTex.rgb, colorTex.a);
        tex.a = max(tex.a, colorTex.a * 0.5);
    }
    return tex;
}
void main(){
    vec2 vUv = atlasUv(vLocalUv, vCell, uTexDiv, uTexSize, uClampUv != 0 ? 2 : uAddressMode);
    vec2 vUvMult = atlasUv(vLocalUvMult, vCellMult, uTexDivMult, uTexSizeMult, uClampUvMult != 0 ? 2 : uAddressModeMult);
    vec4 t = (uHasTex != 0)
        ? texture(uTex, vUv) * addressMask(vLocalUv, uAddressMode)
        : vec4(0.0);
    t = applyParticleColor(t);
    if (uHasPalette != 0) {
        float paletteCoverage = t.a;
        float paletteIndex = dot(t, uPaletteMixMask);
        float paletteU = clamp(paletteIndex, 0.0, 1.0);
        float paletteV = clamp((vPaletteSelector + 0.5) / max(float(uPaletteCount), 1.0), 0.0, 1.0);
        vec4 palette = texture(uPaletteMap, vec2(paletteU, paletteV));
        if (uIsAdditive != 0) {
            t.rgb = palette.rgb * max(t.a, palette.a);
        } else {
            t.rgb = mix(t.rgb, palette.rgb, palette.a * t.a);
        }
        t.a = paletteCoverage;
    }
    if (uHasTexMult != 0) {
        vec4 mult = texture(uTexMult, vUvMult) * addressMask(vLocalUvMult, uAddressModeMult);
        t.rgb *= mult.rgb;
        t.a *= mult.a;
    }
    if (uHasErosion != 0) {
        float erosion = clamp(dot(texture(uErosionTex, vUv), vErosionMixer), 0.0, 1.0);
        float featherIn = max(0.0001, uErosionFeatherIn);
        float featherOut = max(0.0001, uErosionFeatherOut);
        float upper = clamp((vErosionDrive - erosion + 1.0) / featherIn, 0.0, 1.0);
        float lower = clamp((vErosionDrive - erosion) / featherOut, 0.0, 1.0);
        t.a *= clamp(upper - lower, 0.0, 1.0);
    }
    if (uHasSoftParticle != 0) {
        vec2 sceneUv = gl_FragCoord.xy / max(uViewportSize, vec2(1.0));
        float depthGap = (texture(uSceneDepthTex, sceneUv).r - gl_FragCoord.z) * 1000.0;
        float fadeIn = uSoftParticleParams.y > 0.0
            ? smoothstep(uSoftParticleParams.x, uSoftParticleParams.x + uSoftParticleParams.y, depthGap)
            : 1.0;
        float fadeOut = uSoftParticleParams.w > 0.0
            ? 1.0 - smoothstep(uSoftParticleParams.z, uSoftParticleParams.z + uSoftParticleParams.w, -depthGap)
            : 1.0;
        float fade = fadeIn * fadeOut;
        if (uIsAdditive != 0) {
            t.rgb *= fade;
        } else {
            t.a *= fade;
        }
    }
    if (uHasReflection != 0) {
        vec4 reflection = texture(uReflectionTex, vUv);
        float edge = clamp(length(vLocalUv - vec2(0.5)) * 1.4142, 0.0, 1.0);
        float opacity = mix(uReflectionOpacity.x, uReflectionOpacity.y, edge);
        t.rgb = mix(t.rgb, reflection.rgb * uReflectionColor.rgb, clamp(opacity * reflection.a, 0.0, 1.0));
    }
    vec4 authoredColor = vColor * uModulationFactor;
    float effectiveAlpha = t.a * authoredColor.a;
    if (uAlphaTest != 0 && effectiveAlpha < uAlphaCutoff) discard;
    if (uIsDistortion != 0) {
        vec4 normalSample = texture(uDistortionTex, vUv);
        float mask = normalSample.a * effectiveAlpha;
        vec2 normalOffset = normalSample.rg * 2.0 - vec2(1.0);
        vec2 sceneUv = gl_FragCoord.xy / max(uViewportSize, vec2(1.0));
        sceneUv = clamp(sceneUv + normalOffset * uDistortionStrength * mask * vec2(uViewportSize.y / max(uViewportSize.x, 1.0), 1.0), vec2(0.0), vec2(1.0));
        vec4 refracted = texture(uSceneTex, sceneUv);
        fragColor = vec4(refracted.rgb, mask);
        return;
    }
    fragColor = vec4(t.rgb * authoredColor.rgb, effectiveAlpha);
    if (uIsAdditive == 1 || uIsMultiply != 0)
        fragColor.rgb *= authoredColor.a;
    fragColor.rgb *= uEmissiveStrength;
}        ";
    }
}
