using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using static AssetsManager.Services.Viewer.Vfx.Parsing.VfxParsingSchema;
using static AssetsManager.Services.Viewer.Vfx.Parsing.VfxValueParser;

namespace AssetsManager.Services.Viewer.Vfx.Parsing
{
    internal static partial class VfxSystemParser
    {
        private static VfxEmitterDefinition ParseEmitter(BinTreeStruct s, bool isSimpleEmitter, bool hudLayer = false)
        {
            var p = s.Properties;

            var authoredLegacy = Get(p, F_legacySimple) as BinTreeStruct;
            var legacy = isSimpleEmitter
                ? Get(p, F_legacySimple) as BinTreeStruct ?? new BinTreeStruct(0, 0, Array.Empty<BinTreeProperty>())
                : null;
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
            VfxProjectionDefinition projection = null;
            if (primitiveKind == VfxPrimitiveKind.PlanarProjection)
            {
                BinTreeStruct data = prim is BinTreeStruct projectionPrimitive
                    ? Get(projectionPrimitive.Properties, F_projection) as BinTreeStruct : null;
                projection = data is null ? new VfxProjectionDefinition() : new VfxProjectionDefinition(
                    GetF32(data.Properties, F_projectionYRange) ?? 5f,
                    GetF32(data.Properties, F_projectionFading) ?? 200f);
            }
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
                    GetF32(sp, F_softDeltaOut) ?? 0f,
                    NormalizeEnumByte(GetU8(sp, F_softTarget), 2, 0));
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
            byte lingerType = (byte)Math.Min(GetU8(p, F_particleLingerType) ?? 0, 3);
            byte uvMode = NormalizeEnumByte(GetU8(p, F_uvMode), 5, 0);
            VfxEmissionSurfaceDefinition emissionSurface = ReadEmissionSurface(p);
            uint customMaterialPathHash = ReadCustomMaterialPathHash(p);
            byte importance = (byte)(GetU8(p, F_importance) ?? VfxAuthoredDefaults.Importance);
            VfxCurveF rate = ReadCurveF(p, F_rate) ?? VfxCurveF.Zero;
            VfxCurve2? velocityRate = ReadCurve2(p, F_rateByVelocityFunction);
            if (velocityRate?.Constant == Vector2.Zero) velocityRate = null;
            bool noRate = !isSingle && Get(p, F_flexRate) is not BinTreeStruct && !HasElements(p, F_materialOverrideDefinitions)
                && VfxPlaybackRuntime.CurveMaximum(rate) == 0f;
            byte colorblindVisibility = (byte)(GetU8(p, F_colorblindVisibility) ?? VfxAuthoredDefaults.ColorblindVisibility);
            bool off = GetBool(p, F_disabled);
            int policy = Get(p, F_filtering) is BinTreeStruct filteringData
                ? GetU8(filteringData.Properties, F_spectatorPolicy) ?? 0 : 0;
            VfxCullReason culled = off ? VfxCullReason.None
                : policy is not (0 or 1) ? VfxCullReason.Spectator
                : isSimpleEmitter && hudLayer ? VfxCullReason.HudLayer
                : noRate ? VfxCullReason.NoRate
                : importance == 4 ? VfxCullReason.Importance
                : !isSimpleEmitter && colorblindVisibility == 2 ? VfxCullReason.Colorblind
                : !isSimpleEmitter && colorblindVisibility > 2 ? VfxCullReason.Never
                : VfxCullReason.None;
            bool disabled = off || culled != VfxCullReason.None;

            var definition = new VfxEmitterDefinition(
                Name: GetString(p, F_emitterName) ?? string.Empty,
                Rate: rate,
                RateByVelocityFunction: velocityRate,
                MaximumRateByVelocity: GetOptionalF32(p, F_maximumRateByVelocity),
                HasVariableStartTime: GetBool(p, F_hasVariableStartTime),
                ParticleLifetime: ReadCurveF(p, F_particleLife, 3f) ?? VfxCurveF.Const(3f),
                EmitterLifetime: GetOptionalF32(p, F_lifetime),
                ParticleLinger: GetOptionalF32(p, F_particleLinger) ?? 0f,
                TimeBeforeFirstEmission: GetF32(p, F_timeBefore) ?? 0f,
                EmissionPeriod: VfxEmissionPeriod.FromAuthored(
                    GetOptionalF32(p, F_period), GetOptionalF32(p, F_timeActiveDuringPeriod)),
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
                    SortEmittersByPosition: GetBool(p, F_sortEmittersByPos),
                    FlipWinding: GetBool(p, F_flipWinding)),
                MiscRenderFlags: (byte)(GetU8(p, F_miscRenderFlags) ?? 0),
                MeshRenderFlags: (byte)(GetU8(p, F_meshRenderFlags) ?? VfxAuthoredDefaults.MeshRenderFlags),
                UseNavmeshMask: GetBool(p, F_useNavmeshMask),
                DepthBiasFactors: GetVec2(p, F_depthBiasFactors),
                IsRotationEnabled: GetBool(p, F_isRotationEnabled),
                PrimitiveKind: primitiveKind,
                Projection: projection,
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
                LegacyHasFixedOrbit: authoredLegacy is not null && GetBool(authoredLegacy.Properties, F_legacyHasFixedOrbit),
                LegacyFixedOrbitType: authoredLegacy is not null ? NormalizeEnumByte(GetU8(authoredLegacy.Properties, F_legacyFixedOrbitType), 5, 1) : legacyFixedOrbitType,
                LegacyParticleBind: authoredLegacy is not null ? GetVec2(authoredLegacy.Properties, F_legacyParticleBind) ?? Vector2.Zero : legacyParticleBind,
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
                    HasLegacySimple: HasValue(p, F_legacySimple),
                    HasTextureMultLayer: hasTextureMultLayer),
                OverridesMaterials: HasElements(p, F_materialOverrideDefinitions),
                ChanceToNotExist: isSimpleEmitter ? 0f : GetF32(p, F_chanceToNotExist) ?? 0f,
                EmissionMesh: !isSimpleEmitter && ReadAsset(p, F_emissionMeshName, ".scb") is { } emissionMeshPath
                    ? new VfxEmissionMeshDefinition(emissionMeshPath,
                        GetF32(p, F_staticEmissionMeshScale) ?? 1f, GetBool(p, F_useEmissionMeshNormal, true)) : null,
                OffsetLifetimeScaling: isSimpleEmitter ? Vector3.Zero : AsVec3(Get(p, F_offsetLifetimeScaling)) ?? Vector3.Zero,
                OffsetLifeScalingSymmetryMode: (byte)(GetU8(p, F_offsetLifeScalingSymmetryMode) ?? 0),
                PostRotateOrientation: !isSimpleEmitter && GetBool(p, F_hasPostRotateOrientation)
                    ? AsVec3(Get(p, F_postRotateOrientationAxis)) ?? Vector3.Zero : null,
                IsHudLayer: hudLayer);
            definition = definition with { TextureMultEmitterUvScrollRate = definition.EmitterUvScrollRate,
                IsGroundLayer = definition.RenderState.RenderPhase == 5 ||
                (definition.RenderState.RenderPhase == 7 && !hudLayer && definition.IsGroundLayer) };
            if (!isSimpleEmitter) return definition;
            return definition with
            {
                LegacyBirthScale = legacyBirthScale ?? VfxCurveF.Const(1f),
                LegacyScale = legacyScaleCurve ?? VfxCurveF.Const(1f),
                LegacyRotation = legacyRotationCurve ?? VfxCurveF.Zero,
                BirthScale = ScalarSizeCurve(legacyBirthScale ?? VfxCurveF.Const(1f)),
                ScaleOverLife = ScalarScaleCurve(legacyScaleCurve ?? VfxCurveF.Const(1f)),
                BirthRotation = ScalarRotationCurve(ReadCurveF(legacy.Properties, F_legacyBirthRotation) ?? VfxCurveF.Zero),
                BirthRotationalVelocity = ScalarRotationCurve(ReadCurveF(legacy.Properties, F_legacyBirthRotVel) ?? VfxCurveF.Zero),
                BirthAcceleration = null, AccelerationOverLife = null, VelocityOverLife = null,
                Acceleration = null, BirthDrag = null, DragOverLife = null, BindWeight = null,
                EmissionSurface = null, TranslationOverride = null, RotationOverride = null, ScaleOverride = null,
                RateByVelocityFunction = null, HasVariableStartTime = false, IsEmitterSpace = false,
                ParticleLingerType = 0, Linger = null,
                RenderState = definition.RenderState with { ClampUvScroll = legacyUvScroll == Vector2.Zero && definition.RenderState.ClampUvScroll },
                EmitterUvScrollRate = GetVec2(p, F_emitterUvScroll) ?? Vector2.Zero,
                TextureMultEmitterUvScrollRate = GetVec2(p, F_emitterUvScroll) ?? Vector2.Zero,
                BirthUvScrollRateCurve = legacyUvScroll == Vector2.Zero ? birthUvScrollRate : VfxCurve2.Const(legacyUvScroll),
                UvScrollRate = legacyUvScroll == Vector2.Zero ? definition.UvScrollRate : legacyUvScroll
            };
        }
    }
}
