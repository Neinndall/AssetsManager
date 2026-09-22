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
layout(location=3) in vec3 aNormal;
layout(location=4) in vec4 aBoneIndices;
layout(location=5) in vec4 aBoneWeights;
uniform mat4 uViewProj;
uniform vec3 uWorldPos;
uniform vec3 uScale;
uniform vec3 uRotation;
uniform vec3 uCamPos;
uniform vec3 uCamUp;
uniform int uAlignPitchToCamera;
uniform int uAlignYawToCamera;
uniform int uMeshSkinned;
uniform int uAttachedMesh;
uniform int uUseSkinning;
uniform int uIsGroundLayer;
uniform vec3 uOrbitRotation;
const int MAX_BONES = 512;
layout(std140) uniform VfxBoneTransforms {
    mat4 uBoneTransforms[MAX_BONES];
};
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
uniform vec4 uFresnel;
uniform vec4 uReflection;
out vec2 vCell;
out vec2 vCellMult;
out vec2 vRawUv;
out vec2 vLocalUv;
out vec2 vLocalUvMult;
out vec2 vCornerUv;
out vec4 vMeshColor;
out vec3 vColorDynamics;
out vec3 vRim;
out vec4 vReflect;

vec3 meshRotateEuler(vec3 p, vec3 r){
    float sz = sin(r.z); float cz = cos(r.z);
    p = vec3(p.x * cz - p.y * sz, p.x * sz + p.y * cz, p.z);
    float sx = sin(r.x); float cx = cos(r.x);
    p = vec3(p.x, p.y * cx - p.z * sx, p.y * sx + p.z * cx);
    float sy = sin(r.y); float cy = cos(r.y);
    return vec3(p.x * cy + p.z * sy, p.y, -p.x * sy + p.z * cy);
}

void main(){
    vec3 sourcePosition = aPos;
    vec3 sourceNormal = aNormal;
    if (uUseSkinning != 0) {
        ivec4 boneIndices = ivec4(aBoneIndices + vec4(0.5));
        mat4 skinMatrix =
            uBoneTransforms[boneIndices.x] * aBoneWeights.x +
            uBoneTransforms[boneIndices.y] * aBoneWeights.y +
            uBoneTransforms[boneIndices.z] * aBoneWeights.z +
            uBoneTransforms[boneIndices.w] * aBoneWeights.w;
        sourcePosition = (skinMatrix * vec4(aPos, 1.0)).xyz;
        sourceNormal = mat3(skinMatrix) * aNormal;
    }

    vec3 scaled = sourcePosition * uScale;
    vec3 scaledSurface = sourceNormal * uScale;
    vec3 local = meshRotateEuler(scaled, uRotation);
    vec3 surface = meshRotateEuler(scaledSurface, uRotation);

    // The default carrier is the particle's complete standing basis (birth/current frame,
    // authored turn and orbital turn), supplied per instance. Camera alignment replaces only
    // that carrier; if its look-at degenerates, the particle basis remains in charge.
    vec3 placementRight = uPlacementRight;
    vec3 placementUp = uPlacementUp;
    vec3 placementForward = uPlacementForward;
    bool cameraAimed = false;
    if (uAlignPitchToCamera != 0 || uAlignYawToCamera != 0) {
        vec3 facing = vec3(
            uAlignYawToCamera != 0 ? uCamPos.x - uWorldPos.x : 0.0,
            uAlignPitchToCamera != 0 ? uCamPos.y - uWorldPos.y : 0.0,
            uCamPos.z - uWorldPos.z);
        if (dot(facing, facing) > 0.0) {
            facing = normalize(facing);
            vec3 aside = cross(uCamUp, facing);
            if (dot(aside, aside) > 0.0) {
                aside = normalize(aside);
                vec3 lift = cross(facing, aside);
                if (uMeshSkinned == 0) {
                    aside = -aside;
                    facing = -facing;
                }
                placementRight = meshRotateEuler(aside, uOrbitRotation);
                placementUp = meshRotateEuler(lift, uOrbitRotation);
                placementForward = meshRotateEuler(facing, uOrbitRotation);
                cameraAimed = true;
            }
        }
    }

    vec3 p;
    vec3 worldSurface;
    if (uAttachedMesh != 0) {
        // LTK's AttachedMesh is the owner's DetachedBindMode skin at the scene origin.
        // A particle contributes scale/tint/UV/erosion, not its translation or rotation.
        p = scaled;
        worldSurface = scaledSurface;
    } else {
        vec3 carried = cameraAimed ? local : scaled;
        vec3 carriedSurface = cameraAimed ? surface : scaledSurface;
        p = placementRight * carried.x + placementUp * carried.y + placementForward * carried.z + uWorldPos;
        worldSurface = placementRight * carriedSurface.x + placementUp * carriedSurface.y + placementForward * carriedSurface.z;
    }
    if (uIsGroundLayer != 0) p.y = 0.0;

    // mesh_vs in LTK does not apply PARTICLE_DEPTH_PUSH_PULL. It derives the rim and
    // reflected ray from the real surface normal and the eye-to-surface direction.
    vRim = vec3(0.0);
    vReflect = vec4(0.0);
    if (dot(worldSurface, worldSurface) > 0.0) {
        vec3 ray = normalize(p - uCamPos);
        vec3 normal = normalize(worldSurface);
        float facing = max(clamp(dot(-ray, normal), 0.0, 1.0), 1e-30);
        vRim = (1.0 - pow(facing, uFresnel.w)) * uFresnel.rgb;
        float glancing = 1.0 - pow(facing, uReflection.x);
        // LTK crosses the mirrored X axis back before sampling the authored DDS cube.
        vReflect = vec4(reflect(ray, normal) * vec3(-1.0, 1.0, 1.0),
                        mix(uReflection.y, uReflection.z, glancing));
    }
    gl_Position = uViewProj * vec4(p, 1.0);
    vec2 baseUv = aUv;
    vRawUv = aUv;
    // mesh_ps_fixedalphauv samples the transformed-but-unscrolled coordinate. Unlike the
    // normal layer transform this path has no authored centre or translation column.
    vec2 alphaUv = baseUv * uUvScale;
    float uvSin = sin(uUvRotation); float uvCos = cos(uUvRotation);
    alphaUv = vec2(alphaUv.x * uvCos - alphaUv.y * uvSin,
                   alphaUv.x * uvSin + alphaUv.y * uvCos);
    if (uFlipU != 0) alphaUv.x = 1.0 - alphaUv.x;
    if (uFlipV != 0) alphaUv.y = 1.0 - alphaUv.y;
    vCornerUv = alphaUv;

    vec2 centeredUv = (baseUv - uUvTransformCenter) * uUvScale;
    centeredUv = vec2(centeredUv.x * uvCos - centeredUv.y * uvSin,
                      centeredUv.x * uvSin + centeredUv.y * uvCos);
    baseUv = centeredUv + uUvTransformCenter + uBirthUvOffset + uEmitterUvOffset;
    if (uFlipU != 0) baseUv.x = 1.0 - baseUv.x;
    if (uFlipV != 0) baseUv.y = 1.0 - baseUv.y;
    vLocalUv = baseUv;
    vec2 mainDiv = max(round(uTexDiv), vec2(1.0));
    float mainCols = mainDiv.x;
    float frame = floor(uFrame + 0.0001);
    float mainFrame = mod(mod(frame, mainDiv.x * mainDiv.y) + mainDiv.x * mainDiv.y, mainDiv.x * mainDiv.y);
    vec2 mainCell = vec2(mod(mainFrame, mainCols), floor(mainFrame / mainCols));
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
    vec2 multDiv = max(round(uTexDivMult), vec2(1.0));
    float multCols = multDiv.x;
    float multFrame = mod(mod(frame, multDiv.x * multDiv.y) + multDiv.x * multDiv.y, multDiv.x * multDiv.y);
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
layout(location=7) in vec4 aUvBase;
layout(location=8) in vec4 aUvErosion;
layout(location=9) in vec2 aErosionMixerZW;
layout(location=10) in vec4 aUvMult;
layout(location=11) in vec3 aUvMultDynamics;
layout(location=12) in vec3 aBasisX;
layout(location=13) in vec3 aBasisY;
layout(location=14) in vec3 aBasisZ;
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
out vec2 vRawUv;
out vec4 vColor;
out float vErosionDrive;
out vec4 vErosionMixer;
out vec2 vLocalUv;
out vec2 vLocalUvMult;
out vec2 vCornerUv;
out float vPaletteSelector;
out vec3 vColorDynamics;
out vec2 vRibbonLookup;
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
        // the eye. If the eye lies on the ray axis, Riot falls back to the particle's own
        // +X instead of inventing a camera-axis side.
        up = placedForward;
        vec3 side = cross(up, uCamPos - aCenter);
        right = dot(side, side) > 0.0 ? -normalize(side) : placedRight;
    } else if (uDirectionOriented != 0) {
        // LTK projects the particle basis' +Y (travel direction) into camera space for
        // billboards, while arbitrary quads use the world-space basis directly.
        if (uArbitraryQuad != 0) {
            right = placedRight;
            up = placedUp;
        } else {
            vec2 projectedUp = vec2(dot(placedUp, uCamRight), dot(placedUp, uCamUp));
            float projectedLen = dot(projectedUp, projectedUp);
            if (projectedLen > 0.0) {
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
    } else {
        world = aCenter + right * (rc.x * aSize.x) + up * (rc.y * aSize.y);
    }
    // LTK's GROUND_LAYER is a final world-space projection shared by quads, ribbons and meshes.
    if (uIsGroundLayer != 0) world.y = 0.0;
    vec3 eyeRay = world - uCamPos;
    if (uDepthPushPull != 0.0 && dot(eyeRay, eyeRay) > 0.000001)
        world += normalize(eyeRay) * uDepthPushPull;
    gl_Position = uViewProj * vec4(world, 1.0);
    vec2 cell = aCorner + vec2(0.5, 0.5);
    // Arbitrary quads use Riot's authored plane UV orientation: U follows corner Y
    // while V follows corner X in the mirrored render basis used by LTK.
    vec2 quadUv = uArbitraryQuad != 0
        ? vec2(aCorner.y + 0.5, aCorner.x + 0.5)
        : vec2(cell.x, 1.0 - cell.y);
    vRawUv = trailPrimitive ? aCorner : quadUv;
    if (trailPrimitive) {
        // Ribbon geometry already carries the engine's final per-vertex layer transforms.
        // This preserves its LOCK_ALPHA uv, independent base/mult cells and beam transpose.
        vLocalUv = aCorner;
        vCornerUv = aRotFrame;
        vCell = aAgeVelX.xy;
        vLocalUvMult = aRotationSize.xy;
        vCellMult = aRotationSize.zw;
        vRibbonLookup = aAgeVelX.zw;
    } else {
        float cols = max(round(uTexDiv.x), 1.0);
        float rows = max(round(uTexDiv.y), 1.0);
        float frame = floor(aRotFrame.y + 0.0001);
        float baseFrame = mod(mod(frame, cols * rows) + cols * rows, cols * rows);
        float fx = mod(baseFrame, cols);
        float fy = floor(baseFrame / cols);
        vec2 localUv = quadUv;
        vCornerUv = localUv;
        vec2 centeredUv = (localUv - uUvTransformCenter) * aUvBase.zw;
        float uvSin = sin(aUvErosion.x); float uvCos = cos(aUvErosion.x);
        centeredUv = vec2(centeredUv.x * uvCos - centeredUv.y * uvSin,
                          centeredUv.x * uvSin + centeredUv.y * uvCos);
        localUv = centeredUv + uUvTransformCenter + aUvBase.xy + uEmitterUvOffset;
        if (uFlipU != 0) localUv.x = 1.0 - localUv.x;
        if (uFlipV != 0) localUv.y = 1.0 - localUv.y;
        vLocalUv = localUv;
        vCell = vec2(fx, fy);
        vec2 multUv = quadUv;
        vec2 centeredMultUv = (multUv - uUvTransformCenterMult) * aUvMult.zw;
        float multSin = sin(aUvMultDynamics.x); float multCos = cos(aUvMultDynamics.x);
        centeredMultUv = vec2(centeredMultUv.x * multCos - centeredMultUv.y * multSin,
                              centeredMultUv.x * multSin + centeredMultUv.y * multCos);
        multUv = centeredMultUv + uUvTransformCenterMult + aUvMult.xy + uUvScrollRateMult;
        if (uFlipUMult != 0) multUv.x = 1.0 - multUv.x;
        if (uFlipVMult != 0) multUv.y = 1.0 - multUv.y;
        vLocalUvMult = multUv;
        vec2 multDiv = max(round(uTexDivMult), vec2(1.0));
        float multCols = multDiv.x;
        float multFrame = mod(mod(frame, multDiv.x * multDiv.y) + multDiv.x * multDiv.y, multDiv.x * multDiv.y);
        vCellMult = vec2(mod(multFrame, multCols), floor(multFrame / multCols));
        vRibbonLookup = vec2(0.0);
    }
    vColor = aColor;
    vPaletteSelector = aUvMultDynamics.z;
    vErosionDrive = aUvErosion.y;
    vErosionMixer = vec4(aUvErosion.zw, aErosionMixerZW);
    vColorDynamics = vec3(aAgeVelX.x, length(aAgeVelX.yzw), aUvMultDynamics.y);
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
vec2 atlasUvRaw(vec2 local, vec2 cell, vec2 divisions){
    vec2 div = max(divisions, vec2(1.0));
    return (cell + local) / div;
}
vec2 atlasUv(vec2 local, vec2 cell, vec2 divisions, vec2 size, int mode){
    return atlasUvRaw(addressedUv(local, mode), cell, divisions);
}
";

        internal const string MeshFragment = TextureSampling + @"
in vec2 vCell;
in vec2 vCellMult;
in vec2 vRawUv;
in vec2 vLocalUv;
in vec2 vLocalUvMult;
in vec2 vCornerUv;
in vec4 vMeshColor;
in vec3 vColorDynamics;
in vec3 vRim;
in vec4 vReflect;
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
uniform samplerCube uReflectionTex;
uniform int uHasReflection;
uniform int uAttachedMesh;
uniform vec4 uReflectionColor;
uniform int uWireframePass;
uniform vec4 uWireframeColor;
uniform int uUseCustomMaterial;
uniform vec4 uMaterialTint;
uniform vec2 uMaterialRepeat;
uniform int uMaterialAddressU;
uniform int uMaterialAddressV;
uniform int uMaterialPremultiplied;
out vec4 fragColor;
float customAddress(float value, int mode){
    if (mode == 0) return fract(value);
    if (mode == 2) return 1.0 - abs(mod(value, 2.0) - 1.0);
    return clamp(value, 0.0, 1.0);
}
float customCoverage(float value, int mode){
    return mode == 3 && (value < 0.0 || value > 1.0) ? 0.0 : 1.0;
}
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
        colorUv = atlasUvRaw(vLocalUvMult, vCellMult, uTexDivMult);
    return texel * texture(uColorMap, colorUv);
}

void main(){
    if (uWireframePass != 0) {
        fragColor = uWireframeColor;
        return;
    }
    if (uUseCustomMaterial != 0) {
        vec2 held = vRawUv * uMaterialRepeat;
        vec2 uv = vec2(customAddress(held.x, uMaterialAddressU), customAddress(held.y, uMaterialAddressV));
        float coverage = customCoverage(held.x, uMaterialAddressU) * customCoverage(held.y, uMaterialAddressV);
        vec4 texel = uHasTex != 0 ? texture(uTex, uv) * coverage : vec4(1.0);
        vec4 color = texel * uColor * uMaterialTint;
        if (color.a < uAlphaCutoff) discard;
        if (uMaterialPremultiplied != 0) color.rgb *= color.a;
        fragColor = color;
        return;
    }
    vec2 vUv = atlasUv(vLocalUv, vCell, uTexDiv, uTexSize, uAddressMode);
    vec2 vUvMult = atlasUv(vLocalUvMult, vCellMult, uTexDivMult, uTexSizeMult, uAddressModeMult);
    vec4 texel = (uHasTex != 0)
        ? texture(uTex, vUv) * addressMask(vLocalUv, uAddressMode)
        : vec4(1.0);
    if (uHasTex != 0 && uUvMode == 2)
        texel.a = sampleAddressed(uTex, vCornerUv, uAddressMode).a;
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
            ? sampleAddressed(
                uErosionTex,
                atlasUvRaw(vLocalUv, vCell, uTexDiv),
                uErosionAddressMode)
            : uErosionDefault;
        float erosion = clamp(dot(erosionTexel, uErosionMixer), 0.0, 1.0);
        float featherIn = max(0.0001, uErosionFeatherIn);
        float featherOut = max(0.0001, uErosionFeatherOut);
        float upper = clamp((uErosionDrive - erosion + uErosionSliceWidth) / featherIn, 0.0, 1.0);
        float lower = clamp((uErosionDrive - erosion) / featherOut, 0.0, 1.0);
        texel.a *= clamp(upper - lower, 0.0, 1.0);
    }
    // LTK's VFX geometry buffer carries no vertex-color lane. Both particle meshes
    // and attached meshes are tinted only by the particle/attachment material color.
    vec4 authoredColor = uColor;
    vec4 lit = texel * authoredColor;
    if (uAlphaTest != 0 && lit.a < uAlphaCutoff) discard;

    // LTK mesh_ps adds the Fresnel rim even when no cube map is available. The cube
    // reflection itself is weighted by its facing-derived opacity, then tinted toward
    // reflectionFresnelColor. Attached meshes use the base texel alpha as the carrier.
    float sheenCarrier = uAttachedMesh != 0 ? texel.a : lit.a;
    vec3 mirrored = vec3(0.0);
    if (uHasReflection != 0) {
        mirrored = texture(uReflectionTex, vReflect.xyz).rgb * vReflect.w
            * mix(vec3(1.0), uReflectionColor.rgb, vReflect.w);
        if (uAttachedMesh != 0) mirrored *= texel.a;
    }
    lit.rgb = clamp(lit.rgb + mirrored + vRim * sheenCarrier, vec3(0.0), vec3(1.0));

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
    if (uIsDistortion != 0 && uDistortionStrength != 0.0) {
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
    fragColor.rgb *= uEmissiveStrength;
}";

        internal const string ParticleFragment = TextureSampling + @"
in vec2 vCell;
in vec2 vCellMult;
in vec2 vRawUv;
in vec4 vColor;
in float vErosionDrive;
in vec4 vErosionMixer;
in vec2 vLocalUv;
in vec2 vLocalUvMult;
in vec2 vCornerUv;
in float vPaletteSelector;
in vec3 vColorDynamics;
in vec2 vRibbonLookup;
uniform sampler2D uTex;
uniform int uHasTex;
uniform int uPrimitiveKind;
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
uniform int uWireframePass;
uniform vec4 uWireframeColor;
uniform int uUseCustomMaterial;
uniform vec4 uMaterialTint;
uniform vec2 uMaterialRepeat;
uniform int uMaterialAddressU;
uniform int uMaterialAddressV;
uniform int uMaterialPremultiplied;
out vec4 fragColor;
float customAddress(float value, int mode){
    if (mode == 0) return fract(value);
    if (mode == 2) return 1.0 - abs(mod(value, 2.0) - 1.0);
    return clamp(value, 0.0, 1.0);
}
float customCoverage(float value, int mode){
    return mode == 3 && (value < 0.0 || value > 1.0) ? 0.0 : 1.0;
}
float colorLookUpDriver(int type){
    if (type == 1) return vColorDynamics.x;
    if (type == 2) return vColorDynamics.y;
    if (type == 3) return vColorDynamics.z;
    return 1.0;
}
vec4 applyParticleColor(vec4 tex){
    if (uHasColor == 0) return tex;
    bool ribbonPrimitive = uPrimitiveKind == 5 || uPrimitiveKind == 6 || uPrimitiveKind == 8 || uPrimitiveKind == 10;
    vec2 colorUv = ribbonPrimitive
        ? vRibbonLookup
        : vec2(
            colorLookUpDriver(uColorLookUpTypeX) * uColorLookUpScales.x,
            colorLookUpDriver(uColorLookUpTypeY) * uColorLookUpScales.y);
    if (!ribbonPrimitive) {
        if (uColorLookUpTypeX != 0) colorUv.x += uColorLookUpOffsets.x;
        if (uColorLookUpTypeY != 0) colorUv.y += uColorLookUpOffsets.y;
    }
    if (uRampAtMult != 0)
        colorUv = atlasUvRaw(vLocalUvMult, vCellMult, uTexDivMult);
    return tex * texture(uColorMap, colorUv);
}

void main(){
    if (uWireframePass != 0) {
        fragColor = uWireframeColor;
        return;
    }
    if (uUseCustomMaterial != 0) {
        vec2 held = vRawUv * uMaterialRepeat;
        vec2 uv = vec2(customAddress(held.x, uMaterialAddressU), customAddress(held.y, uMaterialAddressV));
        float coverage = customCoverage(held.x, uMaterialAddressU) * customCoverage(held.y, uMaterialAddressV);
        vec4 texel = uHasTex != 0 ? texture(uTex, uv) * coverage : vec4(1.0);
        vec4 color = texel * vColor * uMaterialTint;
        if (color.a < uAlphaCutoff) discard;
        if (uMaterialPremultiplied != 0) color.rgb *= color.a;
        fragColor = color;
        return;
    }
    vec2 vUv = atlasUv(vLocalUv, vCell, uTexDiv, uTexSize, uAddressMode);
    vec2 vUvMult = atlasUv(vLocalUvMult, vCellMult, uTexDivMult, uTexSizeMult, uAddressModeMult);
    vec4 t;
    if (uHasTex != 0) {
        t = texture(uTex, vUv) * addressMask(vLocalUv, uAddressMode);
    } else {
        t = vec4(1.0);
        bool ribbonPrimitive = uPrimitiveKind == 5 || uPrimitiveKind == 6 || uPrimitiveKind == 8 || uPrimitiveKind == 10;
        if (!ribbonPrimitive)
            t.a = 1.0 - smoothstep(0.0, 0.5, length(vCornerUv - vec2(0.5)));
    }
    if (uHasTex != 0 && uUvMode == 2)
        t.a = sampleAddressed(uTex, vCornerUv, uAddressMode).a;
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
            ? sampleAddressed(
                uErosionTex,
                atlasUvRaw(vLocalUv, vCell, uTexDiv),
                uErosionAddressMode)
            : uErosionDefault;
        float erosion = clamp(dot(erosionTexel, vErosionMixer), 0.0, 1.0);
        float featherIn = max(0.0001, uErosionFeatherIn);
        float featherOut = max(0.0001, uErosionFeatherOut);
        float upper = clamp((vErosionDrive - erosion + uErosionSliceWidth) / featherIn, 0.0, 1.0);
        float lower = clamp((vErosionDrive - erosion) / featherOut, 0.0, 1.0);
        t.a *= clamp(upper - lower, 0.0, 1.0);
    }
    vec4 authoredColor = vColor;
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
    if (uIsDistortion != 0 && uDistortionStrength != 0.0) {
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
    fragColor.rgb *= uEmissiveStrength;
}        ";
    }
}
