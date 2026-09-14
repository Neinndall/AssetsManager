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
out vec2 vCornerUv;
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
    vCornerUv = centeredUv + uUvTransformCenter;
    baseUv = vCornerUv + uBirthUvOffset + uEmitterUvOffset;
    if (uFlipU != 0) baseUv.x = 1.0 - baseUv.x;
    if (uFlipV != 0) baseUv.y = 1.0 - baseUv.y;
    vLocalUv = baseUv;
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
    multUv = centeredMultUv + uUvTransformCenterMult + uUvOffsetMult + uEmitterUvOffsetMult;
    if (uFlipUMult != 0) multUv.x = 1.0 - multUv.x;
    if (uFlipVMult != 0) multUv.y = 1.0 - multUv.y;
    vLocalUvMult = multUv;
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
layout(location=6) in vec4 aRotationSize;
layout(location=7) in vec2 aUvOffset;
layout(location=8) in vec2 aUvScale;
layout(location=9) in float aUvRotation;
layout(location=10) in float aErosionDrive;
layout(location=11) in vec4 aErosionMixer;
layout(location=12) in vec2 aUvOffsetMult;
layout(location=13) in vec2 aUvScaleMult;
layout(location=14) in float aUvRotationMult;
layout(location=15) in vec2 aTextureMultFramePalette;
layout(location=16) in vec3 aBasisX;
layout(location=17) in vec3 aBasisY;
layout(location=18) in vec3 aBasisZ;
uniform mat4 uViewProj;
uniform vec3 uCamRight;
uniform vec3 uCamUp;
uniform vec3 uCamPos;
uniform float uDepthPushPull;
uniform vec2 uTexDiv;
uniform vec2 uTexSize;
uniform vec2 uEmitterUvOffset;
uniform vec2 uTexDivMult;
uniform vec2 uTexSizeMult;
uniform vec2 uUvScrollRateMult;
uniform int uDirectionOriented;
uniform int uArbitraryQuad;
uniform int uLegacyOrientation;
uniform int uPivotUp;
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
out vec2 vCornerUv;
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
    bool rayPrimitive = uPrimitiveKind == 7;
    bool trailPrimitive = uPrimitiveKind == 5 || uPrimitiveKind == 6 || uPrimitiveKind == 8 || uPrimitiveKind == 10;
    float rotation = (uArbitraryQuad != 0 || rayPrimitive || trailPrimitive || uDirectionOriented != 0) ? 0.0 : aRotFrame.x;
    vec3 placedRight = normalize(aBasisX);
    vec3 placedUp = normalize(aBasisY);
    vec3 placedForward = normalize(aBasisZ);
    vec3 right = uArbitraryQuad != 0 ? placedRight : uCamRight;
    vec3 up = uArbitraryQuad != 0 ? placedUp : uCamUp;

    if (rayPrimitive) {
        // Rays lie on the particle's own +Z and roll about that axis just enough to face
        // the eye. The basis is computed per particle on the CPU, as in LTK.
        up = placedForward;
        vec3 side = cross(up, uCamPos - aCenter);
        if (dot(side, side) < 0.0001) side = cross(up, uCamUp);
        if (dot(side, side) < 0.0001) side = cross(up, uCamRight);
        right = -normalize(side);
    } else if (uDirectionOriented != 0) {
        // LTK projects the particle basis' +Y (travel direction) into camera space for
        // billboards, while arbitrary quads use the world-space basis directly.
        if (uArbitraryQuad != 0) {
            right = placedRight;
            up = placedUp;
        } else {
            vec2 projectedUp = vec2(dot(placedUp, uCamRight), dot(placedUp, uCamUp));
            float projectedLen = dot(projectedUp, projectedUp);
            if (projectedLen > 0.000001) {
                projectedUp *= inversesqrt(projectedLen);
                right = uCamRight * projectedUp.y - uCamUp * projectedUp.x;
                up = uCamRight * projectedUp.x + uCamUp * projectedUp.y;
            }
        }
    }

    float s = sin(rotation);
    float c = cos(rotation);
    vec2 geometryCorner = aCorner;
    if (uPivotUp != 0) geometryCorner.y += 0.5;
    vec2 rc = vec2(geometryCorner.x * c - geometryCorner.y * s, geometryCorner.x * s + geometryCorner.y * c);
    if (uPrimitiveKind == 0) rc *= 2.0;
    vec3 world;
    if (rayPrimitive) {
        float alongRay = aCorner.y + 0.5;
        world = aCenter
            + up * (aRotationSize.w + alongRay * aSize.y)
            + right * (aCorner.x * aSize.x);
    } else if (trailPrimitive) {
        world = aCenter;
    } else if (uLegacyOrientation != 0) {
        vec3 planeU = uLegacyOrientation == 2 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 1.0, 0.0);
        vec3 planeV = uLegacyOrientation == 3 ? vec3(1.0, 0.0, 0.0) : vec3(0.0, 0.0, -1.0);
        world = aCenter + planeU * (rc.y * aSize.y) + planeV * (rc.x * aSize.x);
        if (uIsGroundLayer != 0) world.y = 0.02;
    } else if (uIsGroundLayer != 0 || uPrimitiveKind == 9) {
        vec3 groundForward = uArbitraryQuad != 0 ? placedUp : vec3(0.0, 0.0, 1.0);
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
    vec3 eyeRay = world - uCamPos;
    if (uDepthPushPull != 0.0 && dot(eyeRay, eyeRay) > 0.000001)
        world += normalize(eyeRay) * uDepthPushPull;
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
    vCornerUv = localUv;
    vec2 centeredUv = (localUv - uUvTransformCenter) * aUvScale;
    float uvSin = sin(aUvRotation); float uvCos = cos(aUvRotation);
    centeredUv = vec2(centeredUv.x * uvCos - centeredUv.y * uvSin,
                      centeredUv.x * uvSin + centeredUv.y * uvCos);
    localUv = centeredUv + uUvTransformCenter + aUvOffset + uEmitterUvOffset;
    if (uFlipU != 0) localUv.x = 1.0 - localUv.x;
    if (uFlipV != 0) localUv.y = 1.0 - localUv.y;
    vLocalUv = localUv;
    vCell = vec2(fx, fy);
    vec2 multUv = trailPrimitive
        ? aCorner
        : vec2(cell.x, 1.0 - cell.y);
    vec2 centeredMultUv = (multUv - uUvTransformCenterMult) * aUvScaleMult;
    float multSin = sin(aUvRotationMult); float multCos = cos(aUvRotationMult);
    centeredMultUv = vec2(centeredMultUv.x * multCos - centeredMultUv.y * multSin,
                          centeredMultUv.x * multSin + centeredMultUv.y * multCos);
    multUv = centeredMultUv + uUvTransformCenterMult + aUvOffsetMult + uUvScrollRateMult;
    if (uFlipUMult != 0) multUv.x = 1.0 - multUv.x;
    if (uFlipVMult != 0) multUv.y = 1.0 - multUv.y;
    vLocalUvMult = multUv;
    vec2 multDiv = max(uTexDivMult, vec2(1.0));
    float multCols = multDiv.x;
    float multFrame = floor(aRotFrame.y + 0.0001);
    vec2 multCell = vec2(mod(multFrame, multCols), floor(multFrame / multCols));
    vCellMult = multCell;
    vColor = aColor;
    vPaletteSelector = aTextureMultFramePalette.y;
    vErosionDrive = aErosionDrive;
    vErosionMixer = aErosionMixer;
    vColorDynamics = vec3(aAgeVelX.x, length(aAgeVelX.yzw), aTextureMultFramePalette.x);
}";


        private const string TextureSampling = @"
uniform int uClampUv;
uniform int uClampUvMult;
uniform vec2 uTexDiv;
uniform vec2 uTexSize;
uniform vec2 uTexDivMult;
uniform vec2 uTexSizeMult;
vec2 addressedUv(vec2 placed, int mode){
    if (mode >= 2) return clamp(placed, vec2(0.0), vec2(1.0));
    if (mode == 1) return vec2(1.0) - abs(mod(placed, vec2(2.0)) - vec2(1.0));
    return fract(placed);
}
float addressMask(vec2 placed, int mode){
    if (mode != 3) return 1.0;
    return all(greaterThanEqual(placed, vec2(0.0))) && all(lessThanEqual(placed, vec2(1.0))) ? 1.0 : 0.0;
}
vec4 sampleAddressed(sampler2D tex, vec2 placed, int mode){
    return texture(tex, addressedUv(placed, mode)) * addressMask(placed, mode);
}
vec2 atlasUv(vec2 local, vec2 cell, vec2 divisions, vec2 size, int mode){
    vec2 div = max(divisions, vec2(1.0));
    return (cell + addressedUv(local, mode)) / div;
}
";

        internal const string MeshFragment = TextureSampling + @"
in vec2 vCell;
in vec2 vCellMult;
in vec2 vLocalUv;
in vec2 vLocalUvMult;
in vec2 vCornerUv;
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
uniform int uRampAtMult;
uniform int uUvMode;
uniform int uColorRenderFlags;
uniform sampler2D uPaletteMap;
uniform int uHasPalette;
uniform int uPaletteCount;
uniform int uPaletteAddressMode;
uniform float uPaletteSelector;
uniform vec4 uPaletteMixMask;
uniform vec2 uPaletteScroll;
uniform int uIsAdditive;
uniform vec4 uModulationFactor;
uniform int uColorLookUpTypeX;
uniform int uColorLookUpTypeY;
uniform vec2 uColorLookUpScales;
uniform vec2 uColorLookUpOffsets;
uniform sampler2D uErosionTex;
uniform int uHasErosion;
uniform int uHasErosionMap;
uniform int uErosionAddressMode;
uniform vec4 uErosionDefault;
uniform float uErosionDrive;
uniform float uErosionFeatherIn;
uniform float uErosionFeatherOut;
uniform float uErosionSliceWidth;
uniform vec4 uErosionMixer;
uniform sampler2D uSceneDepthTex;
uniform int uHasSoftParticle;
uniform vec4 uSoftParticleParams;
uniform vec4 uSoftParticleControl;
uniform vec2 uDepthProjection;
uniform vec2 uViewportSize;
uniform sampler2D uReflectionTex;
uniform int uHasReflection;
uniform vec2 uReflectionOpacity;
uniform vec4 uReflectionColor;
out vec4 fragColor;
float colorLookUpDriver(int type){
    if (type == 1) return vColorDynamics.x;
    if (type == 2) return vColorDynamics.y;
    if (type == 3) return vColorDynamics.z;
    return 1.0;
}
vec4 applyParticleColor(vec4 texel){
    if (uHasColor == 0) return texel;
    vec2 colorUv = vec2(
        colorLookUpDriver(uColorLookUpTypeX) * uColorLookUpScales.x,
        colorLookUpDriver(uColorLookUpTypeY) * uColorLookUpScales.y);
    if (uColorLookUpTypeX != 0) colorUv.x += uColorLookUpOffsets.x;
    if (uColorLookUpTypeY != 0) colorUv.y += uColorLookUpOffsets.y;
    if (uRampAtMult != 0)
        colorUv = atlasUv(vLocalUvMult, vCellMult, uTexDivMult, uTexSizeMult, uAddressModeMult);
    return texel * texture(uColorMap, colorUv);
}

void main(){
    vec2 vUv = atlasUv(vLocalUv, vCell, uTexDiv, uTexSize, uAddressMode);
    vec2 vUvMult = atlasUv(vLocalUvMult, vCellMult, uTexDivMult, uTexSizeMult, uAddressModeMult);
    vec4 texel = (uHasTex != 0)
        ? texture(uTex, vUv) * addressMask(vLocalUv, uAddressMode)
        : vec4(0.0);
    if (uHasTex != 0 && uUvMode == 2)
        texel.a = texture(uTex, vCornerUv).a;
    if (uHasPalette != 0) {
        float paletteCoverage = texel.a;
        float paletteIndex = dot(texel, uPaletteMixMask);
        float paletteU = clamp(paletteIndex, 0.0, 1.0);
        float paletteV = (uPaletteSelector + 0.5) / max(float(uPaletteCount), 1.0);
        texel.rgb = sampleAddressed(uPaletteMap, vec2(paletteU, paletteV) + uPaletteScroll, uPaletteAddressMode).rgb;
        texel.a = paletteCoverage;
    }
    texel = applyParticleColor(texel);
    if (uHasTexMult != 0) {
        vec4 mult = texture(uTexMult, vUvMult) * addressMask(vLocalUvMult, uAddressModeMult);
        texel.rgb *= mult.rgb;
        texel.a *= mult.a;
    }
    if (uHasErosion != 0) {
        vec4 erosionTexel = uHasErosionMap != 0
            ? sampleAddressed(uErosionTex, vUv, uErosionAddressMode)
            : uErosionDefault;
        float erosion = clamp(dot(erosionTexel, uErosionMixer), 0.0, 1.0);
        float featherIn = max(0.0001, uErosionFeatherIn);
        float featherOut = max(0.0001, uErosionFeatherOut);
        float upper = clamp((uErosionDrive - erosion + uErosionSliceWidth) / featherIn, 0.0, 1.0);
        float lower = clamp((uErosionDrive - erosion) / featherOut, 0.0, 1.0);
        texel.a *= clamp(upper - lower, 0.0, 1.0);
    }
    vec4 authoredColor = uColor * vMeshColor * uModulationFactor;
    vec4 lit = texel * authoredColor;
    if (uAlphaTest != 0 && lit.a < uAlphaCutoff) discard;
    if (uHasReflection != 0) {
        vec4 reflection = texture(uReflectionTex, vUv);
        float edge = clamp(length(vLocalUv - vec2(0.5)) * 1.4142, 0.0, 1.0);
        float opacity = mix(uReflectionOpacity.x, uReflectionOpacity.y, edge);
        lit.rgb = mix(lit.rgb, reflection.rgb * uReflectionColor.rgb, clamp(opacity * reflection.a, 0.0, 1.0));
    }
    if (uHasSoftParticle != 0) {
        vec2 sceneUv = gl_FragCoord.xy / max(uViewportSize, vec2(1.0));
        float storedNdc = texture(uSceneDepthTex, sceneUv).r * 2.0 - 1.0;
        float hereNdc = gl_FragCoord.z * 2.0 - 1.0;
        float sceneDenom = storedNdc + uDepthProjection.x;
        float hereDenom = hereNdc + uDepthProjection.x;
        sceneDenom = abs(sceneDenom) < 0.000001 ? (sceneDenom < 0.0 ? -0.000001 : 0.000001) : sceneDenom;
        hereDenom = abs(hereDenom) < 0.000001 ? (hereDenom < 0.0 ? -0.000001 : 0.000001) : hereDenom;
        float scene = -uDepthProjection.y / sceneDenom;
        float here = -uDepthProjection.y / hereDenom;
        vec2 through = clamp((here - scene - uSoftParticleParams.xy) * uSoftParticleParams.zw, 0.0, 1.0);
        vec2 eased = through * through * (3.0 - 2.0 * through);
        float fade = eased.x - eased.y;
        lit.rgb *= uSoftParticleControl.x + fade * uSoftParticleControl.y;
        lit.a *= uSoftParticleControl.z + fade * uSoftParticleControl.w;
    }
    if (uIsDistortion != 0) {
        vec4 normalSample = texture(uDistortionTex, vLocalUv);
        float mask = normalSample.a * lit.a;
        vec2 normalOffset = normalSample.rg * 2.0 - vec2(1.0);
        vec2 sceneUv = gl_FragCoord.xy / max(uViewportSize, vec2(1.0));
        sceneUv = clamp(sceneUv + normalOffset * uDistortionStrength * mask * vec2(uViewportSize.y / max(uViewportSize.x, 1.0), 1.0), vec2(0.0), vec2(1.0));
        vec4 refracted = texture(uSceneTex, sceneUv);
        fragColor = vec4(refracted.rgb, mask);
        return;
    }
    fragColor = lit;
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
in vec2 vCornerUv;
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
uniform int uRampAtMult;
uniform int uUvMode;
uniform int uColorRenderFlags;
uniform sampler2D uPaletteMap;
uniform int uHasPalette;
uniform int uPaletteCount;
uniform int uPaletteAddressMode;
uniform vec4 uPaletteMixMask;
uniform vec2 uPaletteScroll;
uniform int uIsAdditive;
uniform vec4 uModulationFactor;
uniform int uColorLookUpTypeX;
uniform int uColorLookUpTypeY;
uniform vec2 uColorLookUpScales;
uniform vec2 uColorLookUpOffsets;
uniform sampler2D uErosionTex;
uniform int uHasErosion;
uniform int uHasErosionMap;
uniform int uErosionAddressMode;
uniform vec4 uErosionDefault;
uniform float uErosionFeatherIn;
uniform float uErosionFeatherOut;
uniform float uErosionSliceWidth;
uniform sampler2D uSceneDepthTex;
uniform int uHasSoftParticle;
uniform vec4 uSoftParticleParams;
uniform vec4 uSoftParticleControl;
uniform vec2 uDepthProjection;
uniform sampler2D uReflectionTex;
uniform int uHasReflection;
uniform vec2 uReflectionOpacity;
uniform vec4 uReflectionColor;
out vec4 fragColor;
float colorLookUpDriver(int type){
    if (type == 1) return vColorDynamics.x;
    if (type == 2) return vColorDynamics.y;
    if (type == 3) return vColorDynamics.z;
    return 1.0;
}
vec4 applyParticleColor(vec4 tex){
    if (uHasColor == 0) return tex;
    vec2 colorUv = vec2(
        colorLookUpDriver(uColorLookUpTypeX) * uColorLookUpScales.x,
        colorLookUpDriver(uColorLookUpTypeY) * uColorLookUpScales.y);
    if (uColorLookUpTypeX != 0) colorUv.x += uColorLookUpOffsets.x;
    if (uColorLookUpTypeY != 0) colorUv.y += uColorLookUpOffsets.y;
    if (uRampAtMult != 0)
        colorUv = atlasUv(vLocalUvMult, vCellMult, uTexDivMult, uTexSizeMult, uAddressModeMult);
    return tex * texture(uColorMap, colorUv);
}

void main(){
    vec2 vUv = atlasUv(vLocalUv, vCell, uTexDiv, uTexSize, uAddressMode);
    vec2 vUvMult = atlasUv(vLocalUvMult, vCellMult, uTexDivMult, uTexSizeMult, uAddressModeMult);
    vec4 t = (uHasTex != 0)
        ? texture(uTex, vUv) * addressMask(vLocalUv, uAddressMode)
        : vec4(0.0);
    if (uHasTex != 0 && uUvMode == 2)
        t.a = texture(uTex, vCornerUv).a;
    if (uHasPalette != 0) {
        float paletteCoverage = t.a;
        float paletteIndex = dot(t, uPaletteMixMask);
        float paletteU = clamp(paletteIndex, 0.0, 1.0);
        float paletteV = (vPaletteSelector + 0.5) / max(float(uPaletteCount), 1.0);
        t.rgb = sampleAddressed(uPaletteMap, vec2(paletteU, paletteV) + uPaletteScroll, uPaletteAddressMode).rgb;
        t.a = paletteCoverage;
    }
    t = applyParticleColor(t);
    if (uHasTexMult != 0) {
        vec4 mult = texture(uTexMult, vUvMult) * addressMask(vLocalUvMult, uAddressModeMult);
        t.rgb *= mult.rgb;
        t.a *= mult.a;
    }
    if (uHasErosion != 0) {
        vec4 erosionTexel = uHasErosionMap != 0
            ? sampleAddressed(uErosionTex, vUv, uErosionAddressMode)
            : uErosionDefault;
        float erosion = clamp(dot(erosionTexel, vErosionMixer), 0.0, 1.0);
        float featherIn = max(0.0001, uErosionFeatherIn);
        float featherOut = max(0.0001, uErosionFeatherOut);
        float upper = clamp((vErosionDrive - erosion + uErosionSliceWidth) / featherIn, 0.0, 1.0);
        float lower = clamp((vErosionDrive - erosion) / featherOut, 0.0, 1.0);
        t.a *= clamp(upper - lower, 0.0, 1.0);
    }
    vec4 authoredColor = vColor * uModulationFactor;
    vec4 lit = t * authoredColor;
    if (uAlphaTest != 0 && lit.a < uAlphaCutoff) discard;
    if (uHasSoftParticle != 0) {
        vec2 sceneUv = gl_FragCoord.xy / max(uViewportSize, vec2(1.0));
        float storedNdc = texture(uSceneDepthTex, sceneUv).r * 2.0 - 1.0;
        float hereNdc = gl_FragCoord.z * 2.0 - 1.0;
        float sceneDenom = storedNdc + uDepthProjection.x;
        float hereDenom = hereNdc + uDepthProjection.x;
        sceneDenom = abs(sceneDenom) < 0.000001 ? (sceneDenom < 0.0 ? -0.000001 : 0.000001) : sceneDenom;
        hereDenom = abs(hereDenom) < 0.000001 ? (hereDenom < 0.0 ? -0.000001 : 0.000001) : hereDenom;
        float scene = -uDepthProjection.y / sceneDenom;
        float here = -uDepthProjection.y / hereDenom;
        vec2 through = clamp((here - scene - uSoftParticleParams.xy) * uSoftParticleParams.zw, 0.0, 1.0);
        vec2 eased = through * through * (3.0 - 2.0 * through);
        float fade = eased.x - eased.y;
        lit.rgb *= uSoftParticleControl.x + fade * uSoftParticleControl.y;
        lit.a *= uSoftParticleControl.z + fade * uSoftParticleControl.w;
    }
    if (uIsDistortion != 0) {
        vec4 normalSample = texture(uDistortionTex, vLocalUv);
        float mask = normalSample.a * lit.a;
        vec2 normalOffset = normalSample.rg * 2.0 - vec2(1.0);
        vec2 sceneUv = gl_FragCoord.xy / max(uViewportSize, vec2(1.0));
        sceneUv = clamp(sceneUv + normalOffset * uDistortionStrength * mask * vec2(uViewportSize.y / max(uViewportSize.x, 1.0), 1.0), vec2(0.0), vec2(1.0));
        vec4 refracted = texture(uSceneTex, sceneUv);
        fragColor = vec4(refracted.rgb, mask);
        return;
    }
    fragColor = lit;
    if (uIsAdditive == 1 || uIsMultiply != 0)
        fragColor.rgb *= authoredColor.a;
    fragColor.rgb *= uEmissiveStrength;
}        ";
    }
}
