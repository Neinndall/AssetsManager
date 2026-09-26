using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using static AssetsManager.Services.Viewer.Vfx.Parsing.VfxParsingSchema;
using static AssetsManager.Services.Viewer.Vfx.Parsing.VfxValueParser;

namespace AssetsManager.Services.Viewer.Vfx.Parsing
{
    internal static class VfxSystemParser
    {
        internal static IReadOnlyDictionary<uint, VfxSystemDefinition> ExtractAll(BinTree bin)
        {
            var map = new Dictionary<uint, VfxSystemDefinition>();

            foreach (var o in bin.Objects.Values)
            {
                if (o.ClassHash != SystemClass) continue;
                var system = ParseSystem(o);
                if (system is not null) map[o.PathHash] = system;
            }
            return map;
        }

        internal static VfxSystemDefinition Extract(BinTree bin, uint pathHash)
        {
            if (bin?.Objects == null ||
                pathHash == 0 ||
                !bin.Objects.TryGetValue(pathHash, out BinTreeObject systemObject) ||
                systemObject.ClassHash != SystemClass)
            {
                return null;
            }

            return ParseSystem(systemObject);
        }

        private static VfxSystemDefinition ParseSystem(BinTreeObject o)
        {
            string name = GetString(o.Properties, F_particleName) ?? $"0x{o.PathHash:x8}";
            string path = GetString(o.Properties, F_particlePath) ?? "";

            var emitters = new List<VfxEmitterDefinition>();
            foreach (uint listHash in EmitterLists)
            {
                if (Get(o.Properties, listHash) is not BinTreeContainer c) continue;
                bool simple = listHash == EmitterLists[1];
                foreach (var el in c.Elements)
                    if (el is BinTreeStruct s && s.ClassHash == EmitterClass)
                        emitters.Add(ParseEmitter(s, simple));
            }
            float radius = GetF32(o.Properties, F_visibilityRadius) ?? 0f;
            Matrix4x4? transform = Get(o.Properties, F_transform) is BinTreeMatrix44 matrix
                ? matrix.Value
                : null;
            int systemFlags = GetI32(o.Properties, F_systemFlags)
                ?? (int?)(AsU32(Get(o.Properties, F_systemFlags)))
                ?? DefaultSystemFlags;
            float buildUpTime = MathF.Max(0f, GetF32(o.Properties, F_buildUpTime) ?? 0f);
            return new VfxSystemDefinition(
                o.PathHash,
                name,
                path,
                emitters,
                radius,
                transform,
                new VfxSystemAuthoredFeatures(
                    HasMaterialOverrides: HasElements(o.Properties, F_materialOverrideDefinitions),
                    HasAssetRemapping: HasElements(o.Properties, F_assetRemappingTable)),
                (systemFlags & AnalyticDragMotionFlag) != 0 ? VfxDragMotion.Analytic : VfxDragMotion.Stepped,
                buildUpTime);
        }

        private static VfxEmitterDefinition ParseEmitter(BinTreeStruct s, bool isSimpleEmitter)
        {
            var p = s.Properties;

            var legacy = Get(p, F_legacySimple) as BinTreeStruct;
            var legacyBirthScale = legacy is null ? null : ReadCurveF(legacy.Properties, F_legacyBirthScale, 1f);
            Vector2 legacyScaleBias = legacy is null
                ? Vector2.One
                : GetVec2(legacy.Properties, F_legacyScaleBias) ?? Vector2.One;
            VfxCurveF? legacyScaleCurve = legacy is null ? null : ReadCurveF(legacy.Properties, F_legacyScale, 1f);
            VfxCurveF? legacyRotationCurve = legacy is null ? null : ReadCurveF(legacy.Properties, F_legacyRotation);
            bool legacyLockedToEmitter = legacy is not null && GetBool(legacy.Properties, F_legacyLockedToEmitter);
            bool legacyHasFixedOrbit = legacy is not null && GetBool(legacy.Properties, F_legacyHasFixedOrbit);
            int legacyFixedOrbitRaw = legacy is null ? 1 : GetU8(legacy.Properties, F_legacyFixedOrbitType) ?? 1;
            byte legacyFixedOrbitType = legacyFixedOrbitRaw is >= 0 and <= 5
                ? (byte)legacyFixedOrbitRaw
                : (byte)1;
            byte legacyOrientation = legacy is null
                ? (byte)0
                : NormalizeEnumByte(GetU8(legacy.Properties, F_legacyOrientation), 3, 0);
            Vector2 legacyParticleBind = legacy is null
                ? Vector2.Zero
                : GetVec2(legacy.Properties, F_legacyParticleBind) ?? Vector2.Zero;
            Vector2 legacyUvScroll = legacy is null
                ? Vector2.Zero
                : GetVec2(legacy.Properties, F_legacyUvScrollRate) ?? Vector2.Zero;
            bool legacyScaleUpFromOrigin = legacy is not null && GetBool(legacy.Properties, F_legacyScaleUpFromOrigin);
            var birthScale = ReadCurve3(p, F_birthScale0, Vector3.One)
                ?? (legacyBirthScale is { } lbs ? ScalarSizeCurve(lbs) : VfxCurve3.Const(Vector3.One));
            var birthScale1 = ReadCurve3(p, F_birthScale1);
            var scaleOverLife = ReadCurve3(p, F_scale0, Vector3.One);
            if (scaleOverLife is null && legacyScaleCurve is { } legacyScale)
                scaleOverLife = ScalarScaleCurve(legacyScale);
            var birthRotation = ReadCurve3(p, F_birthRotation);
            if (birthRotation is null && legacy is not null && ReadCurveF(legacy.Properties, F_legacyBirthRotation) is { } legacyRotation)
                birthRotation = ScalarRotationCurve(legacyRotation);
            var birthRotationalVelocity = ReadCurve3(p, F_birthRotVel0);
            if (birthRotationalVelocity is null && legacy is not null && ReadCurveF(legacy.Properties, F_legacyBirthRotVel) is { } legacyRotVel)
                birthRotationalVelocity = ScalarRotationCurve(legacyRotVel);
            var birthColor = ReadCurve4(p, F_birthColor) ?? VfxCurve4.Const(Vector4.One);
            var flexShape = ReadFlexShape(p);
            var palette = ReadPalette(p);
            string audioSoundOnCreate = Get(p, F_audio) is BinTreeStruct audio
                ? GetString(audio.Properties, F_soundOnCreate)
                : null;
            IReadOnlyList<string> filteringKeywords = ReadStringContainer(
                Get(p, F_filtering) is BinTreeStruct filtering
                    ? Get(filtering.Properties, F_keywordsExcluded)
                    : null);

            p.TryGetValue(F_primitive, out var prim);
            // League reads a null Struct pointer exactly like an omitted primitive: CameraQuad.
            uint authoredPrimitiveClass = prim is BinTreeStruct primitive && primitive.ClassHash != 0
                ? primitive.ClassHash
                : 0u;
            uint primitiveClass = authoredPrimitiveClass != 0 ? authoredPrimitiveClass : PrimCameraQuad;
            VfxPrimitiveKind primitiveKind = GetPrimitiveKind(primitiveClass);
            bool isMesh = primitiveKind is VfxPrimitiveKind.Mesh or VfxPrimitiveKind.AttachedMesh;
            bool isArbitraryQuad = prim is BinTreeStruct aq && aq.ClassHash == PrimArbitraryQuad;
            string meshPath = null, meshSkl = null, meshAnm = null, meshFallbackPath = null;
            IReadOnlyList<string> meshAnimationVariants = Array.Empty<string>();
            bool meshIsSkinned = false;
            bool meshAlignPitch = false;
            bool meshAlignYaw = false;
            IReadOnlyList<uint> submeshesToDraw = Array.Empty<uint>();
            IReadOnlyList<uint> submeshesToDrawAlways = Array.Empty<uint>();
            IReadOnlyList<uint> attachedSubmeshHashes = Array.Empty<uint>();
            VfxTrailDefinition trail = null;
            VfxBeamDefinition beam = null;
            bool readsMeshDefinition = isMesh || primitiveKind is VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam;
            if (readsMeshDefinition && prim is BinTreeStruct ps2 && Get(ps2.Properties, F_meshDef) is BinTreeStruct md)
            {
                // LTK/engine precedence: a complete skinned mMeshName + mMeshSkeletonName
                // pair wins. mSimpleMeshName is only the fallback when that pair is absent.
                string skinnedMesh = ReadAsset(md.Properties, F_meshName, ".skn");
                string skeleton = ReadAsset(md.Properties, F_meshSkeleton, ".skl");
                string simpleMesh = ReadAsset(md.Properties, F_simpleMesh, ".scb");
                if (!IsNoMeshPath(simpleMesh) && IsSupportedSimpleMeshPath(simpleMesh))
                    meshFallbackPath = simpleMesh;

                if (!IsNoMeshPath(skinnedMesh) && !IsNoMeshPath(skeleton))
                {
                    meshPath = skinnedMesh;
                    meshSkl = skeleton;
                    meshIsSkinned = true;
                }
                else if (!string.IsNullOrWhiteSpace(meshFallbackPath))
                {
                    meshPath = meshFallbackPath;
                }

                meshAnm = ReadAsset(md.Properties, F_meshAnim, ".anm");
                meshAnimationVariants = ReadAssetContainer(Get(md.Properties, F_meshAnimationVariants), ".anm");
                meshAlignPitch = GetBool(ps2.Properties, F_meshAlignPitch);
                meshAlignYaw = GetBool(ps2.Properties, F_meshAlignYaw);
                submeshesToDraw = ReadHashContainer(Get(md.Properties, F_submeshesToDraw));
                submeshesToDrawAlways = ReadHashContainer(Get(md.Properties, F_submeshesToDrawAlways));
                // Preserve the legacy union for diagnostics while retaining the two lists above
                // so AttachedMesh rendering can follow League's draw/always semantics exactly.
                attachedSubmeshHashes = submeshesToDrawAlways
                    .Concat(submeshesToDraw)
                    .Distinct()
                    .ToArray();
            }
            if (primitiveKind is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail &&
                prim is BinTreeStruct trailPrimitive)
            {
                IReadOnlyDictionary<uint, BinTreeProperty> tp =
                    Get(trailPrimitive.Properties, F_trailDefinition) is BinTreeStruct trailData
                        ? trailData.Properties
                        : new Dictionary<uint, BinTreeProperty>();
                trail = new VfxTrailDefinition(
                    ReadCurve3(tp, F_trailBirthTilingSize) ?? VfxCurve3.Const(Vector3.Zero),
                    NormalizeEnumByte(GetU8(tp, F_trailSmoothingMode), 2, 0),
                    NormalizeEnumByte(GetU8(tp, F_trailMode), 1, 0),
                    GetI32(tp, F_trailMaxAddedPerFrame) ?? 0,
                    GetF32(tp, F_trailCutoff) ?? 0f);
            }
            if (primitiveKind is VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam &&
                prim is BinTreeStruct beamPrimitive)
            {
                IReadOnlyDictionary<uint, BinTreeProperty> bp =
                    Get(beamPrimitive.Properties, F_beamDefinition) is BinTreeStruct beamData
                        ? beamData.Properties
                        : new Dictionary<uint, BinTreeProperty>();
                beam = new VfxBeamDefinition(
                    NormalizeEnumByte(GetU8(bp, F_trailMode) ?? GetI32(bp, F_trailMode), 1, 0),
                    NormalizeEnumByte(GetU8(bp, F_beamTrailMode) ?? GetI32(bp, F_beamTrailMode), 1, 0),
                    GetI32(bp, F_beamSegments) ?? GetU16(bp, F_beamSegments) ?? GetU8(bp, F_beamSegments) ?? 0,
                    ReadCurve3(bp, F_trailBirthTilingSize) ?? VfxCurve3.Const(Vector3.Zero),
                    ReadCurve4(bp, F_beamColor) ?? VfxCurve4.Const(Vector4.One),
                    GetBool(bp, F_beamColorBound),
                    AsVec3(Get(bp, F_beamSourceOffset)) ?? Vector3.Zero,
                    AsVec3(Get(bp, F_beamTargetOffset)) ?? Vector3.Zero);
            }

            string textureMultPath = null;
            Vector2 textureMultTexDiv = Vector2.One, textureMultUvScroll = Vector2.Zero;
            VfxCurve2? textureMultBirthUvOffset = null;
            VfxCurve2? textureMultBirthUvScroll = null;
            VfxCurve2? textureMultParticleUvScroll = null;
            VfxCurve2? textureMultUvScale = null;
            VfxCurveF? textureMultUvRotation = null;
            VfxCurveF? textureMultBirthUvRotate = null;
            VfxCurveF? textureMultParticleUvRotate = null;
            int textureMultAddressMode = 0;
            bool textureMultFlipV = false;
            bool textureMultFlipU = false;
            bool textureMultClampUv = false;
            Vector2 textureMultTransformCenter = new(0.5f, 0.5f);
            Vector2 textureMultEmitterUvScroll = Vector2.Zero;
            BinTreeStruct textureMult = Get(p, F_textureMult) as BinTreeStruct;
            bool hasTextureMultLayer = textureMult is not null;
            if (textureMult is not null)
            {
                textureMultPath = ReadAsset(textureMult.Properties, F_textureMult, ".tex");
                textureMultTexDiv = ReadValueVec2(Get(textureMult.Properties, F_texDivMult)) ?? Vector2.One;
                textureMultBirthUvScroll = ReadCurve2(textureMult.Properties, F_birthUvScrollMult);
                textureMultUvScroll = textureMultBirthUvScroll?.Constant ?? Vector2.Zero;
                textureMultBirthUvOffset = ReadCurve2(textureMult.Properties, F_birthUvOffsetMult);
                textureMultParticleUvScroll = ReadCurve2(textureMult.Properties, F_particleUvScrollMult);
                textureMultUvScale = ReadCurve2(textureMult.Properties, F_uvScaleMult, Vector2.One);
                textureMultUvRotation = ReadCurveF(textureMult.Properties, F_uvRotationMult);
                textureMultBirthUvRotate = ReadCurveF(textureMult.Properties, F_birthUvRotateMult);
                textureMultParticleUvRotate = ReadCurveF(textureMult.Properties, F_particleUvRotateMult);
                textureMultAddressMode = NormalizeEnumByte(
                    GetU8(textureMult.Properties, F_texAddressMult),
                    maxInclusive: 3,
                    fallback: 0);
                textureMultFlipV = GetBool(textureMult.Properties, F_textureMultFlipV);
                textureMultFlipU = GetBool(textureMult.Properties, F_textureMultFlipU);
                textureMultTransformCenter =
                    GetVec2(textureMult.Properties, F_textureMultTransformCenter) ?? new Vector2(0.5f, 0.5f);
                textureMultClampUv = GetBool(textureMult.Properties, F_textureMultClampUv);
                textureMultEmitterUvScroll =
                    GetVec2(textureMult.Properties, F_textureMultEmitterUvScroll) ?? Vector2.Zero;
            }

            VfxCurve2? birthUvScrollRate = ReadCurve2(p, F_birthUvScroll);

            VfxDistortionDefinition distortion = null;
            if (Get(p, F_distortionDefinition) is BinTreeStruct distortionData)
            {
                var dp = distortionData.Properties;
                distortion = new VfxDistortionDefinition(
                    GetF32(dp, F_distortion) ?? 0f,
                    GetU8(dp, F_distortionMode) ?? 1,
                    ReadAsset(dp, F_normalMapTexture, ".tex"));
            }

            VfxAlphaErosionDefinition alphaErosion = null;
            if (Get(p, F_alphaErosionDefinition) is BinTreeStruct erosionData)
            {
                var ep = erosionData.Properties;
                int authoredAddress = GetU8(ep, F_erosionMapAddressMode) ?? 2;
                int samplerAddress = authoredAddress switch
                {
                    0 => 0,
                    1 => 2,
                    2 => 1,
                    3 => 3,
                    _ => 1
                };
                alphaErosion = new VfxAlphaErosionDefinition(
                    ReadAsset(ep, F_erosionMapName, ".tex"),
                    ReadCurveF(ep, F_erosionDriveCurve, 1f) ?? VfxCurveF.Const(1f),
                    GetF32(ep, F_erosionFeatherIn) ?? 0.1f,
                    GetF32(ep, F_erosionFeatherOut) ?? 0.1f,
                    samplerAddress,
                    ReadCurve4(ep, F_erosionMapChannelMixer, new Vector4(0f, 0f, 0f, 1f))
                        ?? VfxCurve4.Const(new Vector4(0f, 0f, 0f, 1f)),
                    GetF32(ep, F_erosionSliceWidth) ?? 1.5f,
                    GetBool(ep, F_useLingerErosionDrive)
                        ? ReadCurveF(ep, F_lingerErosionDrive, 1f) ?? VfxCurveF.Const(1f)
                        : null,
                    GetF32(ep, F_erosionDriveSource) ?? 0f);
            }
            VfxLingerDefinition linger = ReadLinger(p);
            VfxChildParticleSetDefinition childParticleSet = ReadChildParticleSet(p);
            VfxFieldCollectionDefinition fields = ReadFields(p);
            VfxSoftParticleDefinition softParticle = null;
            if (Get(p, F_softParticleParams) is BinTreeStruct softParticleData)
            {
                var sp = softParticleData.Properties;
                softParticle = new VfxSoftParticleDefinition(
                    GetF32(sp, F_softBeginIn) ?? 0f,
                    GetF32(sp, F_softDeltaIn) ?? 0f,
                    GetF32(sp, F_softBeginOut) ?? 0f,
                    GetF32(sp, F_softDeltaOut) ?? 0f);
            }
            VfxReflectionDefinition reflection = null;
            if (Get(p, F_reflectionDefinition) is BinTreeStruct reflectionData)
            {
                var rp = reflectionData.Properties;
                reflection = new VfxReflectionDefinition(
                    GetF32(rp, F_reflectionOpacityDirect) ?? 0f,
                    GetF32(rp, F_reflectionOpacityGlancing) ?? 1f,
                    GetF32(rp, F_reflectionFresnel) ?? 1f,
                    GetF32(rp, F_fresnel) ?? 1f,
                    GetVec4(rp, F_fresnelColor) ?? Vector4.Zero,
                    GetVec4(rp, F_reflectionFresnelColor) ?? Vector4.One,
                    ReadAsset(rp, F_reflectionMapTexture, ".tex"));
            }

            bool isSingle = GetBool(p, F_isSingle);
            byte blendMode = NormalizeEnumByte(
                GetU8(p, F_blendMode) ?? (int?)(AsU32(Get(p, F_blendMode))),
                maxInclusive: 8,
                fallback: (byte)VfxAuthoredDefaults.BlendMode);
            byte stencilMode = NormalizeEnumByte(
                GetU8(p, F_stencilMode),
                maxInclusive: 4,
                fallback: VfxAuthoredDefaults.StencilMode);
            byte stencilReference = stencilMode == 0
                ? (byte)0
                : (byte)(GetU8(p, F_stencilRef) ?? VfxAuthoredDefaults.StencilReference);
            byte textureAddressMode = NormalizeEnumByte(GetU8(p, F_texAddress), 3, 0);
            byte colorLookupX = NormalizeEnumByte(
                GetU8(p, F_colorLookUpX),
                maxInclusive: 3,
                fallback: VfxAuthoredDefaults.ColorLookUpTypeX);
            byte colorLookupY = NormalizeEnumByte(
                GetU8(p, F_colorLookUpY),
                maxInclusive: 3,
                fallback: VfxAuthoredDefaults.ColorLookUpTypeY);
            byte lingerType = NormalizeEnumByte(GetU8(p, F_particleLingerType), 2, 0);
            byte uvMode = NormalizeEnumByte(GetU8(p, F_uvMode), 5, 0);
            VfxEmissionSurfaceDefinition emissionSurface = ReadEmissionSurface(p);
            uint customMaterialPathHash = ReadCustomMaterialPathHash(p);
            byte importance = (byte)(GetU8(p, F_importance) ?? VfxAuthoredDefaults.Importance);
            byte colorblindVisibility = (byte)(GetU8(p, F_colorblindVisibility) ?? VfxAuthoredDefaults.ColorblindVisibility);
            VfxCullReason culled = importance == 4
                ? VfxCullReason.Importance
                : (!isSimpleEmitter && colorblindVisibility == 2 ? VfxCullReason.Colorblind : VfxCullReason.None);
            bool disabled = GetBool(p, F_disabled) || culled != VfxCullReason.None;

            return new VfxEmitterDefinition(
                Name: GetString(p, F_emitterName) ?? string.Empty,
                Rate: ReadCurveF(p, F_rate) ?? VfxCurveF.Zero,
                ParticleLifetime: ReadCurveF(p, F_particleLife, 3f) ?? VfxCurveF.Const(3f),
                EmitterLifetime: GetOptionalF32(p, F_lifetime),
                ParticleLinger: GetOptionalF32(p, F_particleLinger) ?? 0f,
                TimeBeforeFirstEmission: GetF32(p, F_timeBefore) ?? 0f,
                IsSingleParticle: isSingle,
                Disabled: disabled,
                RateIsPeriod: GetBool(p, F_rateIsPeriod),
                BirthTimePeriod: GetF32(p, F_birthTimePeriod) ?? 0f,
                IsLoop: GetBool(p, F_isLoop),
                BlendMode: blendMode,
                BirthScale: birthScale,
                ScaleOverLife: scaleOverLife,
                BirthColor: birthColor,
                ColorOverLife: ReadCurve4(p, F_color),
                BirthVelocity: ReadCurve3(p, F_birthVelocity),
                Acceleration: ReadCurve3(p, F_worldAccel),
                BirthRotationalVelocity: birthRotationalVelocity,
                EmitterPosition: ReadCurve3(p, F_emitterPos) ?? VfxCurve3.Const(Vector3.Zero),
                TexturePath: ReadAsset(p, F_texture, ".tex"),
                TexDiv: GetVec2(p, F_texDiv) ?? Vector2.One,
                NumFrames: GetU16(p, F_numFrames) ?? 1,
                RandomStartFrame: GetBool(p, F_randomStart),
                IsMeshPrimitive: isMesh,
                MeshPath: meshPath,
                UvScrollRate: birthUvScrollRate?.Constant ?? Vector2.Zero,
                MeshSkeletonPath: meshSkl,
                MeshAnimationPath: meshAnm,
                MeshIsSkinned: meshIsSkinned,
                MeshFallbackPath: meshFallbackPath,
                MeshAlignPitchToCamera: meshAlignPitch,
                MeshAlignYawToCamera: meshAlignYaw,
                SpawnShape: ReadSpawnShape(p),
                BirthAcceleration: ReadCurve3(p, F_birthAccel),
                AccelerationOverLife: ReadCurve3(p, F_accel),
                BirthRotationalAcceleration: ReadCurve3(p, F_birthRotAccel),
                TranslationOverride: AsVec3(Get(p, F_translationOverride)),
                RotationOverride: AsVec3(Get(p, F_rotationOverride)),
                ScaleOverride: AsVec3(Get(p, F_scaleOverride)),
                BirthOrbitalVelocity: ReadCurve3(p, F_birthOrbital),
                BirthDrag: ReadCurve3(p, F_birthDrag),
                DragOverLife: ReadCurve3(p, F_drag),
                BirthRotation: birthRotation,
                IsDirectionOriented: GetBool(p, F_direction),
                IsArbitraryQuad: isArbitraryQuad,
                BirthFrameRate: ReadCurveF(p, F_birthFrameRate, 1f),
                FrameRate: GetF32(p, F_frameRate),
                TextureMultPath: textureMultPath,
                TextureMultTexDiv: textureMultTexDiv,
                TextureMultUvScrollRate: textureMultUvScroll,
                StartFrame: GetU16(p, F_startFrame) ?? 0,
                Distortion: distortion,
                ParticleColorTexturePath: ReadAsset(p, F_particleColorTex, ".tex"),
                ColorLookUpTypeX: colorLookupX,
                ColorLookUpTypeY: colorLookupY,
                RenderState: new VfxEmitterRenderState(
                    RenderPass: GetI16(p, F_renderPass) ?? 0,
                    AlphaReference: (byte)(GetU8(p, F_alphaRef) ?? VfxAuthoredDefaults.AlphaReference),
                    TextureAddressMode: textureAddressMode,
                    ClampUvScroll: GetBool(p, F_uvScrollClamp),
                    FlipU: GetBool(p, F_textureFlipU),
                    FlipV: GetBool(p, F_textureFlipV),
                    DisableBackfaceCull: GetBool(p, F_disableCull),
                    RenderPhase: (byte)(GetU8(p, F_renderPhaseOverride) ?? VfxAuthoredDefaults.RenderPhaseOverride),
                    StencilMode: stencilMode,
                    StencilReference: stencilReference,
                    StencilReferenceId: AsU32(Get(p, F_stencilReferenceId)) ?? 0u,
                    WriteAlphaOnly: GetBool(p, F_writeAlphaOnly),
                    SortEmittersByPosition: GetBool(p, F_sortEmittersByPos)),
                MiscRenderFlags: (byte)(GetU8(p, F_miscRenderFlags) ?? 0),
                MeshRenderFlags: (byte)(GetU8(p, F_meshRenderFlags) ?? VfxAuthoredDefaults.MeshRenderFlags),
                UseNavmeshMask: GetBool(p, F_useNavmeshMask),
                DepthBiasFactors: GetVec2(p, F_depthBiasFactors),
                IsRotationEnabled: GetBool(p, F_isRotationEnabled),
                PrimitiveKind: primitiveKind,
                VelocityOverLife: ReadCurve3(p, F_velocity),
                RotationOverLife: ReadCurve3(p, F_rotation),
                BirthUvOffset: ReadCurve2(p, F_birthUvOffset),
                UvScale: ReadCurve2(p, F_uvScale, Vector2.One),
                UvRotation: ReadCurveF(p, F_uvRotation),
                AlphaErosion: alphaErosion,
                ChildParticleSet: childParticleSet,
                Fields: fields,
                ParticleLingerType: lingerType,
                EmitterLinger: GetOptionalF32(p, F_emitterLinger) ?? 0f,
                IsEmitterSpace: legacyLockedToEmitter || GetBool(p, F_isEmitterSpace),
                IsLocalOrientation: GetBool(p, F_isLocalOrientation, defaultValue: true),
                ParticleIsLocalOrientation: GetBool(p, F_particleIsLocalOrientation),
                IsFollowingTerrain: GetBool(p, F_isFollowingTerrain),
                IsGroundLayer: GetBool(p, F_isGroundLayer),
                IsUniformScale: GetBool(p, F_isUniformScale),
                EmitterUvScrollRate: legacyUvScroll != Vector2.Zero
                    ? legacyUvScroll
                    : GetVec2(p, F_emitterUvScroll) ?? Vector2.Zero,
                Trail: trail,
                BirthUvScrollRateCurve: birthUvScrollRate,
                ParticleUvScrollRate: ReadCurve2(p, F_particleUvScroll),
                BirthUvRotateRate: ReadCurveF(p, F_birthUvRotate),
                ParticleUvRotateRate: ReadCurveF(p, F_particleUvRotate),
                TextureMultBirthUvOffset: textureMultBirthUvOffset,
                TextureMultBirthUvScrollRate: textureMultBirthUvScroll,
                TextureMultParticleUvScroll: textureMultParticleUvScroll,
                TextureMultUvScale: textureMultUvScale,
                TextureMultUvRotation: textureMultUvRotation,
                TextureMultBirthUvRotateRate: textureMultBirthUvRotate,
                TextureMultParticleUvRotate: textureMultParticleUvRotate,
                TextureMultAddressMode: textureMultAddressMode,
                TextureMultFlipV: textureMultFlipV,
                ColorLookUpOffsets: GetVec2(p, F_colorLookUpOffsets) ?? Vector2.Zero,
                ColorLookUpScales: GetVec2(p, F_colorLookUpScales) ?? Vector2.One,
                ColorRenderFlags: (byte)(GetU8(p, F_colorRenderFlags) ?? 0),
                ModulationFactor: GetVec4(p, F_modulationFactor),
                IsTexturePixelated: GetBool(p, F_isTexturePixelated),
                UvTransformCenter: GetVec2(p, F_uvTransformCenter) ?? new Vector2(0.5f, 0.5f),
                TextureMultFlipU: textureMultFlipU,
                TextureMultTransformCenter: textureMultTransformCenter,
                TextureMultClampUvScroll: textureMultClampUv,
                TextureMultEmitterUvScrollRate: textureMultEmitterUvScroll,
                SoftParticle: softParticle,
                Reflection: reflection,
                Importance: importance,
                ColorblindVisibility: colorblindVisibility,
                Culled: culled,
                BirthScale1: birthScale1,
                Rotation1: ReadCurve3(p, F_rotation1),
                UvMode: uvMode,
                BindWeight: legacyLockedToEmitter ? VfxCurveF.Const(1f) : ReadCurveF(p, F_bindWeight),
                FlexShape: flexShape,
                PaletteDefinition: palette,
                DirectionVelocityScale: GetF32(p, F_directionVelocityScale) ?? 0f,
                DirectionVelocityMinScale: GetF32(p, F_directionVelocityMinScale) ?? 1f,
                ParticlesShareRandomValue: GetBool(p, F_particlesShareRandomValue),
                AudioSoundOnCreate: audioSoundOnCreate,
                FilteringKeywordsExcluded: filteringKeywords,
                LegacyBirthScale: legacyBirthScale,
                LegacyScaleBias: legacyScaleBias,
                LegacyScale: legacyScaleCurve,
                LegacyRotation: legacyRotationCurve,
                LegacyOrientation: legacyOrientation,
                LegacyScaleUpFromOrigin: legacyScaleUpFromOrigin,
                LegacyLockedToEmitter: legacyLockedToEmitter,
                LegacyHasFixedOrbit: legacyHasFixedOrbit,
                LegacyFixedOrbitType: legacyFixedOrbitType,
                LegacyParticleBind: legacyParticleBind,
                DepthPushPull: GetF32(p, F_depthPushPull) ?? 0f,
                Beam: beam,
                Linger: linger,
                IsSimpleEmitter: isSimpleEmitter,
                MeshAnimationVariants: meshAnimationVariants,
                EmissionSurface: emissionSurface,
                CustomMaterialPathHash: customMaterialPathHash,
                SubmeshesToDraw: submeshesToDraw,
                SubmeshesToDrawAlways: submeshesToDrawAlways,
                AttachedSubmeshHashes: attachedSubmeshHashes,
                AuthoredFeatures: new VfxEmitterAuthoredFeatures(
                    PrimitiveClassHash: authoredPrimitiveClass,
                    HasCustomMaterial: HasValue(p, F_customMaterial),
                    HasStencil: stencilMode != 0 ||
                        (AsU32(Get(p, F_stencilReferenceId)) ?? 0u) != 0,
                    HasEmissionMesh: HasValue(p, F_emissionMeshName),
                    HasEmissionSurface: HasValue(p, F_emissionSurfaceDefinition),
                    UsesEmissionMeshNormal: GetBool(p, F_useEmissionMeshNormal),
                    HasTranslationOverride: HasValue(p, F_translationOverride),
                    HasRotationOverride: HasValue(p, F_rotationOverride),
                    HasScaleOverride: HasValue(p, F_scaleOverride),
                    HasPeriodControl: HasValue(p, F_period) || HasValue(p, F_timeActiveDuringPeriod),
                    HasLegacySimple: legacy is not null,
                    HasTextureMultLayer: hasTextureMultLayer));
        }

        private static bool HasValue(IReadOnlyDictionary<uint, BinTreeProperty> properties, uint fieldHash)
        {
            if (!properties.TryGetValue(fieldHash, out BinTreeProperty property)) return false;
            return property switch
            {
                BinTreeOptional optional => optional.Value is not null,
                BinTreeStruct structure => structure.ClassHash != 0,
                _ => true
            };
        }

        private static bool HasElements(IReadOnlyDictionary<uint, BinTreeProperty> properties, uint fieldHash)
            => properties.TryGetValue(fieldHash, out BinTreeProperty property) && property switch
            {
                BinTreeContainer container => container.Elements.Count > 0,
                BinTreeMap map => map.Count > 0,
                _ => HasValue(properties, fieldHash)
            };

        private static VfxFlexShapeDefinition ReadFlexShape(
            IReadOnlyDictionary<uint, BinTreeProperty> emitterProperties)
        {
            if (Get(emitterProperties, F_flexShapeDefinition) is not BinTreeStruct flex) return null;
            return new VfxFlexShapeDefinition(
                GetF32(flex.Properties, F_scaleBirthScaleByBoundObjectSize) ?? 0f,
                GetF32(flex.Properties, F_scaleEmitOffsetByBoundObjectSize) ?? 0f);
        }

        private static VfxLingerDefinition ReadLinger(
            IReadOnlyDictionary<uint, BinTreeProperty> emitterProperties)
        {
            if (Get(emitterProperties, F_linger) is not BinTreeStruct linger) return null;
            var p = linger.Properties;
            VfxCurve3? rotation = GetBool(p, F_useLingerRotation)
                ? ReadCurve3(p, F_lingerRotation) ?? VfxCurve3.Const(Vector3.Zero)
                : null;
            VfxCurve3? scale = GetBool(p, F_useLingerScale)
                ? ReadCurve3(p, F_lingerScale, Vector3.One) ?? VfxCurve3.Const(Vector3.One)
                : null;
            VfxCurve4? color = GetBool(p, F_useLingerColor)
                ? ReadCurve4(p, F_lingerColor) ?? VfxCurve4.Const(Vector4.One)
                : null;
            VfxCurve3? acceleration = GetBool(p, F_useLingerAcceleration)
                ? ReadCurve3(p, F_lingerAcceleration) ?? VfxCurve3.Const(Vector3.Zero)
                : null;
            VfxCurve3? velocity = GetBool(p, F_useLingerVelocity)
                ? ReadCurve3(p, F_lingerVelocity) ?? VfxCurve3.Const(Vector3.Zero)
                : null;
            VfxCurve3? drag = GetBool(p, F_useLingerDrag)
                ? ReadCurve3(p, F_lingerDrag) ?? VfxCurve3.Const(Vector3.Zero)
                : null;
            return new VfxLingerDefinition(rotation, scale, color, acceleration, velocity, drag);
        }

        private static VfxPaletteDefinition ReadPalette(
            IReadOnlyDictionary<uint, BinTreeProperty> emitterProperties)
        {
            if (Get(emitterProperties, F_paletteDefinition) is not BinTreeStruct palette) return null;
            Vector4 luma = new(0.299f, 0.587f, 0.114f, 0f);
            VfxCurve4? sourceMixColor = ReadCurve4(palette.Properties, F_paletteSourceMixColor, luma)
                ?? ReadCurve4(palette.Properties, F_palleteSourceMixColor, luma);
            return new VfxPaletteDefinition(
                GetI32(palette.Properties, F_paletteCount) ?? 1,
                ReadCurve3(palette.Properties, F_paletteSelector) ?? VfxCurve3.Const(Vector3.Zero),
                ReadAsset(palette.Properties, F_paletteTexture, ".tex"),
                sourceMixColor?.Sample(0f) ?? luma,
                ReadCurveF(palette.Properties, F_paletteScrollU) ?? VfxCurveF.Zero,
                ReadCurveF(palette.Properties, F_paletteScrollV) ?? VfxCurveF.Zero,
                NormalizeEnumByte(GetU8(palette.Properties, F_paletteAddressMode), 3, 1));
        }

        private static IReadOnlyList<string> ReadStringContainer(BinTreeProperty property)
        {
            if (property is not BinTreeContainer container || container.Elements.Count == 0)
                return Array.Empty<string>();
            return container.Elements
                .OfType<BinTreeString>()
                .Select(static value => value.Value)
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
        }

        private static IReadOnlyList<string> ReadAssetContainer(BinTreeProperty property, string extension)
        {
            if (property is not BinTreeContainer container || container.Elements.Count == 0)
                return Array.Empty<string>();
            return container.Elements
                .Select(value => ReadAsset(value, extension))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
        }

        private static uint ReadCustomMaterialPathHash(IReadOnlyDictionary<uint, BinTreeProperty> emitterProperties)
        {
            if (Get(emitterProperties, F_customMaterial) is not BinTreeStruct custom) return 0u;
            return AsU32(Get(custom.Properties, F_customMaterialLink)) ?? 0u;
        }

        private static VfxEmissionSurfaceDefinition ReadEmissionSurface(
            IReadOnlyDictionary<uint, BinTreeProperty> emitterProperties)
        {
            if (Get(emitterProperties, F_emissionSurfaceDefinition) is not BinTreeStruct outer) return null;

            BinTreeStruct held = outer;
            BinTreeProperty nested = Get(outer.Properties, F_emissionSurface);
            if (nested is BinTreeStruct nestedSurface)
            {
                if (nestedSurface.ClassHash != EmissionSkeletonClass && nestedSurface.ClassHash != EmissionMeshClass)
                    return null;
                held = nestedSurface;
            }
            else if (nested is not null)
            {
                return null;
            }

            IReadOnlyDictionary<uint, BinTreeProperty> properties = held.Properties;
            return new VfxEmissionSurfaceDefinition(
                held.ClassHash == EmissionSkeletonClass ? VfxEmissionSurfaceKind.Skeleton : VfxEmissionSurfaceKind.Mesh,
                ReadAsset(properties, F_emissionMesh, ".skn"),
                ReadAsset(properties, F_emissionSkeleton, ".skl"),
                ReadAsset(properties, F_emissionAnimation, ".anm"),
                ReadHashContainer(Get(properties, F_emissionSubmeshes)),
                ReadHashContainer(Get(properties, F_emissionJointMask)),
                GetF32(properties, F_emissionMeshScale) ?? 1f,
                Math.Clamp(GetI32(properties, F_emissionMaxJointWeights) ?? GetU8(properties, F_emissionMaxJointWeights) ?? 4, 0, 4),
                GetBool(properties, F_emissionUseSurfaceNormal, defaultValue: true));
        }

        private static IReadOnlyList<uint> ReadHashContainer(BinTreeProperty property)
        {
            if (property is not BinTreeContainer container || container.Elements.Count == 0)
                return Array.Empty<uint>();
            return container.Elements
                .Select(AsU32)
                .Where(static value => value is > 0)
                .Select(static value => value.Value)
                .ToArray();
        }

        private static VfxFieldCollectionDefinition ReadFields(
            IReadOnlyDictionary<uint, BinTreeProperty> emitterProperties)
        {
            if (Get(emitterProperties, F_fieldCollection) is not BinTreeStruct fieldData) return null;
            var acceleration = ReadStructContainer(fieldData.Properties, F_fieldAccelerationDefinitions)
                .Select(value => new VfxAccelerationField(
                    ReadCurve3(value.Properties, F_accel) ?? VfxCurve3.Const(Vector3.Zero),
                    GetBool(value.Properties, F_isLocalSpace, defaultValue: true))).ToArray();
            var attraction = ReadStructContainer(fieldData.Properties, F_fieldAttractionDefinitions)
                .Select(value => new VfxAttractionField(
                    ReadCurveF(value.Properties, F_accel) ?? VfxCurveF.Zero,
                    ReadCurve3(value.Properties, F_position) ?? VfxCurve3.Const(Vector3.Zero),
                    ReadCurveF(value.Properties, F_radius) ?? VfxCurveF.Zero)).ToArray();
            var drag = ReadStructContainer(fieldData.Properties, F_fieldDragDefinitions)
                .Select(value => new VfxDragField(
                    ReadCurveF(value.Properties, F_strength) ?? VfxCurveF.Zero,
                    ReadCurve3(value.Properties, F_position) ?? VfxCurve3.Const(Vector3.Zero),
                    ReadCurveF(value.Properties, F_radius) ?? VfxCurveF.Zero)).ToArray();
            var orbital = ReadStructContainer(fieldData.Properties, F_fieldOrbitalDefinitions)
                .Select(value => new VfxOrbitalField(
                    ReadCurve3(value.Properties, F_directionField, Vector3.UnitY) ?? VfxCurve3.Const(Vector3.UnitY),
                    GetBool(value.Properties, F_isLocalSpace, defaultValue: true))).ToArray();
            var noise = ReadStructContainer(fieldData.Properties, F_fieldNoiseDefinitions)
                .Select(value => new VfxNoiseField(
                    ReadCurveF(value.Properties, F_frequency) ?? VfxCurveF.Zero,
                    ReadCurveF(value.Properties, F_velocityDelta) ?? VfxCurveF.Zero,
                    ReadCurve3(value.Properties, F_position) ?? VfxCurve3.Const(Vector3.Zero),
                    ReadCurveF(value.Properties, F_radius) ?? VfxCurveF.Zero,
                    AsVec3(Get(value.Properties, F_axisFraction)) ?? Vector3.Zero)).ToArray();
            if (acceleration.Length == 0 && attraction.Length == 0 && drag.Length == 0 && orbital.Length == 0 && noise.Length == 0)
                return null;
            return new VfxFieldCollectionDefinition(acceleration, attraction, drag, orbital, noise);
        }

        private static IEnumerable<BinTreeStruct> ReadStructContainer(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint fieldHash)
            => Get(properties, fieldHash) is BinTreeContainer container
                ? container.Elements.OfType<BinTreeStruct>()
                : Enumerable.Empty<BinTreeStruct>();

        private static VfxChildParticleSetDefinition ReadChildParticleSet(
            IReadOnlyDictionary<uint, BinTreeProperty> emitterProperties)
        {
            if (Get(emitterProperties, F_childParticleSet) is not BinTreeStruct childData) return null;
            var children = new List<VfxChildSystemReference>();
            if (Get(childData.Properties, F_childrenIdentifiers) is BinTreeContainer identifiers)
            {
                foreach (BinTreeProperty item in identifiers.Elements)
                {
                    if (item is not BinTreeStruct identifier)
                    {
                        // LTK preserves unresolved child slots so childrenProbability keeps its authored indices.
                        children.Add(null);
                        continue;
                    }

                    string name = GetString(identifier.Properties, F_effectName) ?? string.Empty;
                    uint systemHash = AsU32(Get(identifier.Properties, F_effect)) ?? 0u;
                    uint effectKey = AsU32(Get(identifier.Properties, F_effectKey)) ?? 0u;
                    children.Add(!string.IsNullOrEmpty(name) || systemHash != 0 || effectKey != 0
                        ? new VfxChildSystemReference(name, systemHash, effectKey)
                        : null);
                }
            }

            // LTK keeps boneToSpawnAt positional and parallel to childrenIdentifiers.
            // Preserve empty string slots: dropping one would shift every child after it.
            IReadOnlyList<string> bones = Get(childData.Properties, F_boneToSpawnAt) is BinTreeContainer boneList
                ? boneList.Elements.OfType<BinTreeString>()
                    .Select(static value => value.Value)
                    .ToArray()
                : Array.Empty<string>();

            VfxCurve3 relativeOffset = VfxCurve3.Const(Vector3.Zero);
            int inheritanceMode = 0;
            if (Get(childData.Properties, F_parentInheritance) is BinTreeStruct inheritance)
            {
                relativeOffset = ReadCurve3(inheritance.Properties, F_relativeOffset) ?? relativeOffset;
                inheritanceMode = GetU8(inheritance.Properties, F_inheritanceMode) ?? 0;
            }

            return new VfxChildParticleSetDefinition(
                children,
                GetBool(childData.Properties, F_childEmitOnDeath),
                // childrenProbability is a zero-based child index, not a weight. Its schema
                // default is zero, so an omitted curve must select the first child.
                ReadCurveF(childData.Properties, F_childrenProbability) ?? VfxCurveF.Zero,
                relativeOffset,
                inheritanceMode,
                bones);
        }

        private static VfxPrimitiveKind GetPrimitiveKind(uint classHash) => classHash switch
        {
            var value when value == PrimCameraQuad => VfxPrimitiveKind.CameraQuad,
            var value when value == PrimCameraUnitQuad => VfxPrimitiveKind.CameraUnitQuad,
            var value when value == PrimArbitraryQuad => VfxPrimitiveKind.ArbitraryQuad,
            var value when value == PrimMesh => VfxPrimitiveKind.Mesh,
            var value when value == PrimAttachedMesh => VfxPrimitiveKind.AttachedMesh,
            var value when value == PrimCameraTrail => VfxPrimitiveKind.CameraTrail,
            var value when value == PrimArbitraryTrail => VfxPrimitiveKind.ArbitraryTrail,
            var value when value == PrimRay => VfxPrimitiveKind.Ray,
            var value when value == PrimBeam => VfxPrimitiveKind.Beam,
            var value when value == PrimCameraSegmentBeam => VfxPrimitiveKind.CameraSegmentBeam,
            var value when value == PrimPlanarProjection => VfxPrimitiveKind.PlanarProjection,
            _ => VfxPrimitiveKind.Unsupported
        };

        private static VfxSpawnShape ReadSpawnShape(IReadOnlyDictionary<uint, BinTreeProperty> emitterProps)
        {
            if ((Get(emitterProps, F_spawnShape) ?? Get(emitterProps, F_shape)) is not BinTreeStruct shape) return null;

            VfxCurve3 offset = shape.ClassHash switch
            {
                var value when value == ShapeLegacy || value == ShapeOld =>
                    ReadCurve3Property(Get(shape.Properties, F_emitOffset)) ?? VfxCurve3.Const(Vector3.Zero),
                var value when value == ShapePoint =>
                    VfxCurve3.Const(AsVec3(Get(shape.Properties, F_emitOffset)) ?? Vector3.Zero),
                _ => VfxCurve3.Const(Vector3.Zero)
            };
            var axes = ReadVector3Container(Get(shape.Properties, F_emitRotAxes));
            var angles = ReadCurveFContainer(Get(shape.Properties, F_emitRotAngles));
            VfxSpawnShapeKind kind = shape.ClassHash switch
            {
                var value when value == ShapeBox => VfxSpawnShapeKind.Box,
                var value when value == ShapeSphere => VfxSpawnShapeKind.Sphere,
                var value when value == ShapeCylinder => VfxSpawnShapeKind.Cylinder,
                var value when value == ShapeLegacy || value == ShapeOld => VfxSpawnShapeKind.Legacy,
                _ => VfxSpawnShapeKind.Point
            };
            return new VfxSpawnShape(
                kind,
                offset,
                axes,
                angles,
                AsVec3(Get(shape.Properties, F_shapeSize)) ?? Vector3.Zero,
                GetF32(shape.Properties, F_shapeRadius) ?? 0f,
                GetF32(shape.Properties, F_shapeHeight) ?? 0f,
                (byte)(GetU8(shape.Properties, F_shapeFlags) ?? 0),
                ReadCurve3Property(Get(shape.Properties, F_birthTranslation)));
        }

        private static VfxCurve3 ScalarSizeCurve(VfxCurveF curve) => new(
            new Vector3(curve.Constant, curve.Constant, 0f), curve.Times,
            curve.Values?.Select(static v => new Vector3(v, v, 0f)).ToArray());

        private static VfxCurve3 ScalarScaleCurve(VfxCurveF curve) => new(
            new Vector3(curve.Constant, curve.Constant, curve.Constant), curve.Times,
            curve.Values?.Select(static v => new Vector3(v, v, v)).ToArray());

        private static VfxCurve3 ScalarRotationCurve(VfxCurveF curve)
        {
            VfxProbTable[] probability = null;
            if (curve.Prob is { Length: > 0 } && !curve.Prob[0].IsEmpty)
            {
                probability = new VfxProbTable[3];
                probability[2] = curve.Prob[0];
            }

            return new VfxCurve3(
                new Vector3(0f, 0f, curve.Constant),
                curve.Times,
                curve.Values?.Select(static v => new Vector3(0f, 0f, v)).ToArray(),
                probability);
        }

        private static IReadOnlyList<Vector3> ReadVector3Container(BinTreeProperty prop)
        {
            if (prop is not BinTreeContainer c || c.Elements.Count == 0) return Array.Empty<Vector3>();
            var values = new List<Vector3>(c.Elements.Count);
            foreach (var el in c.Elements)
                if (AsVec3(el) is { } value) values.Add(value);
            return values;
        }

        private static IReadOnlyList<VfxCurveF> ReadCurveFContainer(BinTreeProperty prop)
        {
            if (prop is not BinTreeContainer c || c.Elements.Count == 0) return Array.Empty<VfxCurveF>();
            var values = new List<VfxCurveF>(c.Elements.Count);
            foreach (var el in c.Elements)
            {
                // LTK's curves() preserves one slot per container element and substitutes
                // DEFAULT.zero when an item cannot be read as a ValueFloat.
                values.Add(ReadCurveFProperty(el) ?? VfxCurveF.Zero);
            }
            return values;
        }

    }
}
