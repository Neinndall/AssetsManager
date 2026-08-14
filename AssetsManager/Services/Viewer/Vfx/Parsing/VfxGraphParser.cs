using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace AssetsManager.Services.Viewer.Vfx.Parsing
{
    /// <summary>
    /// Parses VfxSystemDefinitionData objects out of a companion .bin and exposes
    /// them keyed by object path-hash.
    /// </summary>
    public static partial class VfxGraphParser
    {
        private static BinTree ParseTree(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            using var stream = new MemoryStream(data, writable: false);
            return new BinTree(stream);
        }


        internal static VfxBinDocument ParseDocument(byte[] data)
        {
            BinTree tree = ParseTree(data);
            return new VfxBinDocument(
                ExtractAll(tree),
                ExtractResourceMap(tree),
                tree.Dependencies.ToArray(),
                ExtractEventSequences(tree),
                ExtractOwnerSceneContext(tree));
        }

        private static VfxOwnerSceneContext ExtractOwnerSceneContext(BinTree tree)
        {
            foreach (BinTreeObject owner in tree.Objects.Values)
            {
                if (owner.ClassHash != SkinCharacterDataPropertiesClass ||
                    Get(owner.Properties, F_skinMeshProperties) is not BinTreeStruct meshProperties)
                {
                    continue;
                }

                string meshPath = GetString(meshProperties.Properties, F_simpleSkin);
                if (string.IsNullOrWhiteSpace(meshPath)) continue;
                return new VfxOwnerSceneContext(
                    meshPath,
                    GetString(meshProperties.Properties, F_ownerSkeleton) ?? string.Empty,
                    Math.Max(0.01f, GetF32(meshProperties.Properties, F_skinScale) ?? 1f));
            }
            return null;
        }

        private static IReadOnlyList<VfxEventSequenceDefinition> ExtractEventSequences(BinTree tree)
        {
            var sequences = new List<VfxEventSequenceDefinition>();
            foreach (BinTreeObject owner in tree.Objects.Values)
            {
                AddEventSequence(sequences, owner.PathHash, owner.ClassHash, owner.Properties);
                if (Get(owner.Properties, F_clipDataMap) is not BinTreeMap clipMap) continue;
                foreach (var clipPair in clipMap)
                {
                    if (clipPair.Value is not BinTreeStruct clip) continue;
                    AddEventSequence(
                        sequences,
                        AsU32(clipPair.Key) ?? owner.PathHash,
                        clip.ClassHash,
                        clip.Properties);
                }
            }
            return sequences;
        }

        private static void AddEventSequence(
            ICollection<VfxEventSequenceDefinition> sequences,
            uint ownerPathHash,
            uint ownerClassHash,
            IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            if (Get(properties, F_eventDataMap) is not BinTreeMap eventMap) return;
            var events = new List<VfxParticleEventDefinition>();
            foreach (var pair in eventMap)
            {
                if (pair.Value is not BinTreeStruct eventData || eventData.ClassHash != ParticleEventClass)
                    continue;
                events.Add(ParseParticleEvent(AsU32(pair.Key) ?? 0u, eventData));
            }
            if (events.Count == 0) return;
            sequences.Add(new VfxEventSequenceDefinition(
                ownerPathHash,
                ownerClassHash,
                Math.Max(0.0001f, GetF32(properties, F_clipTickDuration) ?? (1f / 30f)),
                GetF32(properties, F_clipStartFrame) ?? 0f,
                GetF32(properties, F_clipEndFrame) ?? -1f,
                events));
        }

        private static VfxParticleEventDefinition ParseParticleEvent(uint eventHash, BinTreeStruct eventData)
        {
            IReadOnlyDictionary<uint, BinTreeProperty> properties = eventData.Properties;
            var attachments = new List<VfxParticleEventAttachment>();
            if (Get(properties, F_eventPairList) is BinTreeContainer pairs)
            {
                foreach (BinTreeStruct pair in pairs.Elements.OfType<BinTreeStruct>())
                {
                    attachments.Add(new VfxParticleEventAttachment(
                        AsU32(Get(pair.Properties, F_eventSourceBone)) ?? 0u,
                        AsU32(Get(pair.Properties, F_eventTargetBone)) ?? 0u));
                }
            }

            return new VfxParticleEventDefinition(
                eventHash,
                AsU32(Get(properties, F_eventName)) ?? 0u,
                GetF32(properties, F_eventStartFrame) ?? 0f,
                GetF32(properties, F_eventEndFrame) ?? -1f,
                AsU32(Get(properties, F_eventEffectKey)) ?? 0u,
                AsU32(Get(properties, F_eventEnemyEffectKey)) ?? 0u,
                GetString(properties, F_eventEffectName) ?? string.Empty,
                GetBool(properties, F_eventIsLoop, defaultValue: true),
                GetBool(properties, F_eventIsKill, defaultValue: true),
                GetBool(properties, F_eventIsDetachable),
                GetBool(properties, F_eventIsSelfOnly),
                GetBool(properties, F_eventFireIfAnimationEndsEarly),
                GetBool(properties, F_eventSkipIfPastEndFrame),
                GetBool(properties, F_eventScalePlaySpeed),
                GetF32(properties, F_eventScale) ?? 1f,
                attachments);
        }

        private static IReadOnlyDictionary<uint, uint> ExtractResourceMap(BinTree tree)
        {
            var map = new Dictionary<uint, uint>();
            foreach (var o in tree.Objects.Values)
            {
                if (o.ClassHash != ResolverClass) continue;
                if (!o.Properties.TryGetValue(F_resourceMap, out var prop)
                    && !o.Properties.TryGetValue(F_mResourceMap, out prop)) continue;
                if (prop is not System.Collections.IEnumerable entries || prop is BinTreeString) continue;
                foreach (var kv in entries)
                {
                    var kvType = kv.GetType();
                    var key = kvType.GetProperty("Key")?.GetValue(kv);
                    var val = kvType.GetProperty("Value")?.GetValue(kv);
                    uint kh = key switch { BinTreeHash h => h.Value, BinTreeU32 u => u.Value, _ => 0u };
                    uint vh = val switch { BinTreeObjectLink ol => ol.Value, BinTreeHash h => h.Value, BinTreeU32 u => u.Value, _ => 0u };
                    if (kh != 0 && vh != 0) map[kh] = vh;
                }
            }
            return map;
        }


        private static IReadOnlyDictionary<uint, VfxSystemDefinition> ExtractAll(BinTree bin)
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

        private static VfxSystemDefinition ParseSystem(BinTreeObject o)
        {
            string name = GetString(o.Properties, F_particleName) ?? $"0x{o.PathHash:x8}";
            string path = GetString(o.Properties, F_particlePath) ?? "";

            var emitters = new List<VfxEmitterDefinition>();
            foreach (var (_, prop) in o.Properties)
            {
                if (prop is not BinTreeContainer c) continue;
                foreach (var el in c.Elements)
                    if (el is BinTreeStruct s && s.ClassHash == EmitterClass)
                        emitters.Add(ParseEmitter(s));
            }
            float radius = GetF32(o.Properties, F_visibilityRadius) ?? 0f;
            Matrix4x4? transform = Get(o.Properties, F_transform) is BinTreeMatrix44 matrix
                ? matrix.Value
                : null;
            return new VfxSystemDefinition(
                o.PathHash,
                name,
                path,
                emitters,
                radius,
                transform,
                new VfxSystemAuthoredFeatures(
                    HasMaterialOverrides: HasElements(o.Properties, F_materialOverrideDefinitions),
                    HasAssetRemapping: HasElements(o.Properties, F_assetRemappingTable)));
        }

        private static VfxEmitterDefinition ParseEmitter(BinTreeStruct s)
        {
            var p = s.Properties;

            var legacy = Get(p, F_legacySimple) as BinTreeStruct;
            var legacyBirthScale = legacy is null ? null : ReadCurveF(legacy.Properties, F_legacyBirthScale);
            var birthScale = ReadCurve3(p, F_birthScale0)
                ?? (legacyBirthScale is { } lbs ? ScalarSizeCurve(lbs) : VfxCurve3.Const(Vector3.One));
            var birthScale1 = ReadCurve3(p, F_birthScale1);
            var scaleOverLife = ReadCurve3(p, F_scale0);
            if (scaleOverLife is null && legacy is not null && ReadCurveF(legacy.Properties, F_legacyScale) is { } legacyScale)
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
            uint primitiveClass = prim is BinTreeStruct primitive ? primitive.ClassHash : PrimCameraQuad;
            VfxPrimitiveKind primitiveKind = GetPrimitiveKind(primitiveClass);
            bool isMesh = primitiveKind is VfxPrimitiveKind.Mesh or VfxPrimitiveKind.AttachedMesh;
            bool isArbitraryQuad = prim is BinTreeStruct aq && aq.ClassHash == PrimArbitraryQuad;
            string meshPath = null, meshSkl = null, meshAnm = null;
            IReadOnlyList<uint> attachedSubmeshHashes = Array.Empty<uint>();
            VfxTrailDefinition trail = null;
            if (isMesh && prim is BinTreeStruct ps2 && Get(ps2.Properties, F_meshDef) is BinTreeStruct md)
            {
                meshPath = GetString(md.Properties, F_simpleMesh) ?? GetString(md.Properties, F_meshName);
                meshSkl = GetString(md.Properties, F_meshSkeleton);
                meshAnm = GetString(md.Properties, F_meshAnim);
                attachedSubmeshHashes = ReadHashContainer(Get(md.Properties, F_submeshesToDrawAlways))
                    .Concat(ReadHashContainer(Get(md.Properties, F_submeshesToDraw)))
                    .Distinct()
                    .ToArray();
            }
            if (primitiveKind is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail &&
                prim is BinTreeStruct trailPrimitive &&
                Get(trailPrimitive.Properties, F_trailDefinition) is BinTreeStruct trailData)
            {
                trail = new VfxTrailDefinition(
                    ReadCurve3(trailData.Properties, F_trailBirthTilingSize) ?? VfxCurve3.Const(Vector3.Zero),
                    GetU8(trailData.Properties, F_trailSmoothingMode) ?? 0,
                    GetU8(trailData.Properties, F_trailMode) ?? 0,
                    GetI32(trailData.Properties, F_trailMaxAddedPerFrame) ?? 0,
                    GetF32(trailData.Properties, F_trailCutoff) ?? 0f);
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
            bool textureMultFlipV = true;
            bool textureMultFlipU = false;
            bool textureMultRandomStart = true;
            bool textureMultClampUv = false;
            bool textureMultScrollAlpha = false;
            Vector2 textureMultTransformCenter = new(0.5f, 0.5f);
            Vector2 textureMultEmitterUvScroll = Vector2.Zero;
            if (Get(p, F_textureMult) is BinTreeStruct textureMult)
            {
                textureMultPath = GetString(textureMult.Properties, F_textureMult);
                textureMultTexDiv = ReadValueVec2(Get(textureMult.Properties, F_texDivMult)) ?? Vector2.One;
                textureMultBirthUvScroll = ReadCurve2(textureMult.Properties, F_birthUvScrollMult);
                textureMultUvScroll = textureMultBirthUvScroll?.Constant ?? Vector2.Zero;
                textureMultBirthUvOffset = ReadCurve2(textureMult.Properties, F_birthUvOffsetMult);
                textureMultParticleUvScroll = ReadCurve2(textureMult.Properties, F_particleUvScrollMult);
                textureMultUvScale = ReadCurve2(textureMult.Properties, F_uvScaleMult);
                textureMultUvRotation = ReadCurveF(textureMult.Properties, F_uvRotationMult);
                textureMultBirthUvRotate = ReadCurveF(textureMult.Properties, F_birthUvRotateMult);
                textureMultParticleUvRotate = ReadCurveF(textureMult.Properties, F_particleUvRotateMult);
                textureMultAddressMode = GetU8(textureMult.Properties, F_texAddressMult) ?? 0;
                textureMultFlipV = GetBool(textureMult.Properties, F_textureMultFlipV, defaultValue: true);
                textureMultFlipU = GetBool(textureMult.Properties, F_textureMultFlipU);
                textureMultRandomStart = GetBool(textureMult.Properties, F_textureMultRandomStart, defaultValue: true);
                textureMultTransformCenter =
                    GetVec2(textureMult.Properties, F_textureMultTransformCenter) ?? new Vector2(0.5f, 0.5f);
                textureMultClampUv = GetBool(textureMult.Properties, F_textureMultClampUv);
                textureMultEmitterUvScroll =
                    GetVec2(textureMult.Properties, F_textureMultEmitterUvScroll) ?? Vector2.Zero;
                textureMultScrollAlpha = GetBool(textureMult.Properties, F_textureMultScrollAlpha);
            }

            VfxCurve2? birthUvScrollRate = ReadCurve2(p, F_birthUvScroll);

            VfxDistortionDefinition distortion = null;
            if (Get(p, F_distortionDefinition) is BinTreeStruct distortionData)
            {
                var dp = distortionData.Properties;
                distortion = new VfxDistortionDefinition(
                    GetF32(dp, F_distortion) ?? 0f,
                    GetU8(dp, F_distortionMode) ?? 0,
                    GetString(dp, F_normalMapTexture));
            }

            VfxAlphaErosionDefinition alphaErosion = null;
            if (Get(p, F_alphaErosionDefinition) is BinTreeStruct erosionData)
            {
                var ep = erosionData.Properties;
                alphaErosion = new VfxAlphaErosionDefinition(
                    GetString(ep, F_erosionMapName),
                    ReadCurveF(ep, F_erosionDriveCurve) ?? VfxCurveF.Const(1f),
                    GetF32(ep, F_erosionFeatherIn) ?? 0.1f,
                    GetF32(ep, F_erosionFeatherOut) ?? 0.1f,
                    GetU8(ep, F_erosionMapAddressMode) ?? 2,
                    ReadCurve4(ep, F_erosionMapChannelMixer));
            }
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
                    GetString(rp, F_reflectionMapTexture));
            }

            bool isSingle = GetBool(p, F_isSingle);
            return new VfxEmitterDefinition(
                Name: GetString(p, F_emitterName) ?? "(emitter)",
                Rate: ReadCurveF(p, F_rate) ?? (isSingle ? VfxCurveF.Zero : VfxCurveF.Const(1f)),
                ParticleLifetime: ReadCurveF(p, F_particleLife) ?? VfxCurveF.Const(1f),
                EmitterLifetime: GetOptionalF32(p, F_lifetime),
                ParticleLinger: GetOptionalF32(p, F_particleLinger) ?? 0f,
                TimeBeforeFirstEmission: GetF32(p, F_timeBefore) ?? 0f,
                IsSingleParticle: isSingle,
                Disabled: GetBool(p, F_disabled),
                RateIsPeriod: GetBool(p, F_rateIsPeriod),
                BirthTimePeriod: GetF32(p, F_birthTimePeriod) ?? 0f,
                IsLoop: GetBool(p, F_isLoop),
                BlendMode: GetU8(p, F_blendMode) ?? (byte?)(AsU32(Get(p, F_blendMode))) ?? 1,
                BirthScale: birthScale,
                ScaleOverLife: scaleOverLife,
                BirthColor: birthColor,
                ColorOverLife: ReadCurve4(p, F_color),
                BirthVelocity: ReadCurve3(p, F_birthVelocity),
                Acceleration: ReadCurve3(p, F_worldAccel),
                BirthRotationalVelocity: birthRotationalVelocity,
                EmitterPosition: ReadCurve3(p, F_emitterPos) ?? VfxCurve3.Const(Vector3.Zero),
                TexturePath: GetString(p, F_texture),
                TexDiv: GetVec2(p, F_texDiv) ?? Vector2.One,
                NumFrames: GetU16(p, F_numFrames) ?? 1,
                RandomStartFrame: GetBool(p, F_randomStart),
                IsMeshPrimitive: isMesh,
                MeshPath: meshPath,
                UvScrollRate: birthUvScrollRate?.Constant ?? Vector2.Zero,
                MeshSkeletonPath: meshSkl,
                MeshAnimationPath: meshAnm,
                SpawnShape: ReadSpawnShape(p),
                BirthAcceleration: ReadCurve3(p, F_birthAccel) ?? ReadCurve3(p, F_accel),
                BirthOrbitalVelocity: ReadCurve3(p, F_birthOrbital),
                BirthDrag: ReadCurve3(p, F_birthDrag),
                DragOverLife: ReadCurve3(p, F_drag),
                BirthRotation: birthRotation,
                IsDirectionOriented: GetBool(p, F_direction),
                IsArbitraryQuad: isArbitraryQuad,
                BirthFrameRate: ReadCurveF(p, F_birthFrameRate),
                FrameRate: GetF32(p, F_frameRate),
                TextureMultPath: textureMultPath,
                TextureMultTexDiv: textureMultTexDiv,
                TextureMultUvScrollRate: textureMultUvScroll,
                StartFrame: GetU16(p, F_startFrame) ?? 0,
                UseTextureAspect: legacy is not null,
                Distortion: distortion,
                ParticleColorTexturePath: GetString(p, F_particleColorTex),
                ColorLookUpTypeX: GetU8(p, F_colorLookUpX) ?? 0,
                ColorLookUpTypeY: GetU8(p, F_colorLookUpY) ?? 0,
                RenderState: new VfxEmitterRenderState(
                    RenderPass: GetI16(p, F_renderPass) ?? 0,
                    AlphaReference: (byte)(GetU8(p, F_alphaRef) ?? 0),
                    TextureAddressMode: GetU8(p, F_texAddress) ?? 0,
                    ClampUvScroll: GetBool(p, F_uvScrollClamp),
                    FlipU: GetBool(p, F_textureFlipU),
                    FlipV: GetBool(p, F_textureFlipV),
                    DisableBackfaceCull: GetBool(p, F_disableCull)),
                MiscRenderFlags: (byte)(GetU8(p, F_miscRenderFlags) ?? 0),
                MeshRenderFlags: (byte)(GetU8(p, F_meshRenderFlags) ?? 0),
                UseNavmeshMask: GetBool(p, F_useNavmeshMask),
                DepthBiasFactors: GetVec2(p, F_depthBiasFactors),
                IsRotationEnabled: GetBool(p, F_isRotationEnabled),
                PrimitiveKind: primitiveKind,
                VelocityOverLife: ReadCurve3(p, F_velocity),
                RotationOverLife: ReadCurve3(p, F_rotation),
                BirthUvOffset: ReadCurve2(p, F_birthUvOffset),
                UvScale: ReadCurve2(p, F_uvScale),
                UvRotation: ReadCurveF(p, F_uvRotation),
                AlphaErosion: alphaErosion,
                ChildParticleSet: childParticleSet,
                Fields: fields,
                ParticleLingerType: (byte)(GetU8(p, F_particleLingerType) ?? 0),
                EmitterLinger: GetOptionalF32(p, F_emitterLinger) ?? 0f,
                IsEmitterSpace: GetBool(p, F_isEmitterSpace),
                IsLocalOrientation: GetBool(p, F_isLocalOrientation),
                ParticleIsLocalOrientation: GetBool(p, F_particleIsLocalOrientation),
                IsFollowingTerrain: GetBool(p, F_isFollowingTerrain),
                IsGroundLayer: GetBool(p, F_isGroundLayer),
                IsUniformScale: GetBool(p, F_isUniformScale),
                EmitterUvScrollRate: GetVec2(p, F_emitterUvScroll) ?? Vector2.Zero,
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
                TextureMultRandomStartFrame: textureMultRandomStart,
                TextureMultTransformCenter: textureMultTransformCenter,
                TextureMultClampUvScroll: textureMultClampUv,
                TextureMultEmitterUvScrollRate: textureMultEmitterUvScroll,
                TextureMultScrollAlpha: textureMultScrollAlpha,
                SoftParticle: softParticle,
                Reflection: reflection,
                Importance: (byte)(GetU8(p, F_importance) ?? 0),
                BirthScale1: birthScale1,
                Rotation1: ReadCurve3(p, F_rotation1),
                UvMode: (byte)(GetU8(p, F_uvMode) ?? 0),
                BindWeight: ReadCurveF(p, F_bindWeight),
                FlexShape: flexShape,
                PaletteDefinition: palette,
                DirectionVelocityScale: GetF32(p, F_directionVelocityScale) ?? 0f,
                RateByVelocityFunction: ReadCurve2(p, F_rateByVelocityFunction),
                HasPostRotateOrientation: GetBool(p, F_hasPostRotateOrientation),
                ParticlesShareRandomValue: GetBool(p, F_particlesShareRandomValue),
                FalloffTexturePath: GetString(p, F_falloffTexture),
                AudioSoundOnCreate: audioSoundOnCreate,
                FilteringKeywordsExcluded: filteringKeywords,
                AttachedSubmeshHashes: attachedSubmeshHashes,
                AuthoredFeatures: new VfxEmitterAuthoredFeatures(
                    PrimitiveClassHash: primitiveClass,
                    HasCustomMaterial: HasValue(p, F_customMaterial),
                    HasStencil: (GetU8(p, F_stencilMode) ?? 0) != 0 || (GetU8(p, F_stencilRef) ?? 0) != 0,
                    HasEmissionMesh: HasValue(p, F_emissionMeshName),
                    HasEmissionSurface: HasValue(p, F_emissionSurfaceDefinition),
                    UsesEmissionMeshNormal: GetBool(p, F_useEmissionMeshNormal),
                    HasTranslationOverride: HasValue(p, F_translationOverride),
                    HasRotationOverride: HasValue(p, F_rotationOverride),
                    HasScaleOverride: HasValue(p, F_scaleOverride),
                    HasPostRotateOrientationAxis: HasValue(p, F_postRotateOrientationAxis),
                    HasPeriodControl: HasValue(p, F_period) || HasValue(p, F_timeActiveDuringPeriod)));
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

        private static VfxPaletteDefinition ReadPalette(
            IReadOnlyDictionary<uint, BinTreeProperty> emitterProperties)
        {
            if (Get(emitterProperties, F_paletteDefinition) is not BinTreeStruct palette) return null;
            VfxCurve4? sourceMixColor = ReadCurve4(palette.Properties, F_paletteSourceMixColor)
                ?? ReadCurve4(palette.Properties, F_palleteSourceMixColor);
            return new VfxPaletteDefinition(
                Math.Max(1, GetI32(palette.Properties, F_paletteCount) ?? 1),
                ReadCurve3(palette.Properties, F_paletteSelector) ?? VfxCurve3.Const(Vector3.Zero),
                GetString(palette.Properties, F_paletteTexture),
                sourceMixColor?.Constant ?? Vector4.UnitX);
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
                    GetBool(value.Properties, F_isLocalSpace))).ToArray();
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
                    ReadCurve3(value.Properties, F_directionField) ?? VfxCurve3.Const(Vector3.Zero),
                    GetBool(value.Properties, F_isLocalSpace))).ToArray();
            var noise = ReadStructContainer(fieldData.Properties, F_fieldNoiseDefinitions)
                .Select(value => new VfxNoiseField(
                    ReadCurveF(value.Properties, F_frequency) ?? VfxCurveF.Zero,
                    ReadCurveF(value.Properties, F_velocityDelta) ?? VfxCurveF.Zero,
                    ReadCurve3(value.Properties, F_position) ?? VfxCurve3.Const(Vector3.Zero),
                    ReadCurveF(value.Properties, F_radius) ?? VfxCurveF.Zero,
                    AsVec3(Get(value.Properties, F_axisFraction)) ?? Vector3.One)).ToArray();
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
                foreach (BinTreeStruct identifier in identifiers.Elements.OfType<BinTreeStruct>())
                {
                    string name = GetString(identifier.Properties, F_effectName)
                               ?? GetString(identifier.Properties, F_effectKey) ?? string.Empty;
                    uint systemHash = AsU32(Get(identifier.Properties, F_effect)) ?? 0u;
                    uint effectKey = AsU32(Get(identifier.Properties, F_effectKey))
                                  ?? (!string.IsNullOrEmpty(name) ? HashAlgorithms.Fnv1a(name) : 0u);
                    if (!string.IsNullOrEmpty(name) || systemHash != 0 || effectKey != 0)
                        children.Add(new VfxChildSystemReference(name, systemHash, effectKey));
                }
            }

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
                ReadCurveF(childData.Properties, F_childrenProbability) ?? VfxCurveF.Const(1f),
                relativeOffset,
                inheritanceMode);
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
            var value when value == PrimBeam || value == PrimCameraSegmentBeam => VfxPrimitiveKind.Beam,
            var value when value == PrimPlanarProjection => VfxPrimitiveKind.PlanarProjection,
            _ => VfxPrimitiveKind.Unsupported
        };

        private static VfxSpawnShape ReadSpawnShape(IReadOnlyDictionary<uint, BinTreeProperty> emitterProps)
        {
            if ((Get(emitterProps, F_spawnShape) ?? Get(emitterProps, F_shape)) is not BinTreeStruct shape) return null;

            var offset = ReadCurve3Property(Get(shape.Properties, F_emitOffset)) ?? VfxCurve3.Const(Vector3.Zero);
            var axes = ReadVector3Container(Get(shape.Properties, F_emitRotAxes));
            var angles = ReadCurveFContainer(Get(shape.Properties, F_emitRotAngles));
            VfxSpawnShapeKind kind = shape.ClassHash switch
            {
                var value when value == ShapeBox => VfxSpawnShapeKind.Box,
                var value when value == ShapeSphere => VfxSpawnShapeKind.Sphere,
                var value when value == ShapeCylinder => VfxSpawnShapeKind.Cylinder,
                var value when value == ShapeLegacy => VfxSpawnShapeKind.Legacy,
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
                (byte)(GetU8(shape.Properties, F_shapeFlags) ?? 0));
        }

        private static VfxCurve3 ScalarSizeCurve(VfxCurveF curve) => new(
            new Vector3(curve.Constant, curve.Constant, 0f), curve.Times,
            curve.Values?.Select(static v => new Vector3(v, v, 0f)).ToArray());

        private static VfxCurve3 ScalarScaleCurve(VfxCurveF curve) => new(
            new Vector3(curve.Constant, curve.Constant, 1f), curve.Times,
            curve.Values?.Select(static v => new Vector3(v, v, 1f)).ToArray());

        private static VfxCurve3 ScalarRotationCurve(VfxCurveF curve) => new(
            new Vector3(curve.Constant, 0f, 0f), curve.Times,
            curve.Values?.Select(static v => new Vector3(v, 0f, 0f)).ToArray());

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
                if (ReadCurveFProperty(el) is { } value) values.Add(value);
            return values;
        }

        private static VfxCurveF? ReadCurveF(IReadOnlyDictionary<uint, BinTreeProperty> p, uint field)
        {
            return p.TryGetValue(field, out var prop) ? ReadCurveFProperty(prop) : null;
        }

        private static VfxCurveF? ReadCurveFProperty(BinTreeProperty prop)
        {
            if (prop is BinTreeStruct v)
            {
                float c = AsF32(Get(v.Properties, F_constantValue)) ?? 0f;
                var (times, vals) = ReadDynamics(v.Properties, AsF32);
                return new VfxCurveF(c, times, vals, ReadNestedProbTables(v.Properties));
            }
            return AsF32(prop) is { } scalar ? VfxCurveF.Const(scalar) : null;
        }

        private static VfxCurve3? ReadCurve3(IReadOnlyDictionary<uint, BinTreeProperty> p, uint field)
        {
            return p.TryGetValue(field, out var prop) ? ReadCurve3Property(prop) : null;
        }

        private static VfxCurve2? ReadCurve2(IReadOnlyDictionary<uint, BinTreeProperty> p, uint field)
        {
            if (!p.TryGetValue(field, out var prop)) return null;
            if (prop is BinTreeStruct value)
            {
                var constant = AsVec2(Get(value.Properties, F_constantValue)) ?? Vector2.Zero;
                var (times, values) = ReadDynamics(value.Properties, AsVec2);
                return new VfxCurve2(constant, times, values, ReadNestedProbTables(value.Properties));
            }
            return AsVec2(prop) is { } vector ? VfxCurve2.Const(vector) : null;
        }

        private static VfxCurve3? ReadCurve3Property(BinTreeProperty prop)
        {
            if (prop is BinTreeStruct v)
            {
                var c = AsVec3(Get(v.Properties, F_constantValue)) ?? Vector3.Zero;
                var (times, vals) = ReadDynamics(v.Properties, AsVec3);
                return new VfxCurve3(c, times, vals, ReadNestedProbTables(v.Properties));
            }
            return AsVec3(prop) is { } vector ? VfxCurve3.Const(vector) : null;
        }

        private static VfxCurve4? ReadCurve4(IReadOnlyDictionary<uint, BinTreeProperty> p, uint field)
        {
            if (!p.TryGetValue(field, out var prop)) return null;
            if (prop is BinTreeStruct v)
            {
                var c = AsVec4(Get(v.Properties, F_constantValue)) ?? Vector4.One;
                var (times, vals) = ReadDynamics(v.Properties, AsVec4);
                return new VfxCurve4(c, times, vals, ReadNestedProbTables(v.Properties));
            }
            return AsVec4(prop) is { } vector ? VfxCurve4.Const(vector) : null;
        }

        private static VfxProbTable[] ReadProbTables(IReadOnlyDictionary<uint, BinTreeProperty> valueProps)
        {
            if (Get(valueProps, F_probTables) is not BinTreeContainer pc || pc.Elements.Count == 0) return null;
            var tables = new VfxProbTable[pc.Elements.Count];
            bool any = false;
            for (int tableIndex = 0; tableIndex < pc.Elements.Count; tableIndex++)
            {
                var el = pc.Elements[tableIndex];
                if (el is not BinTreeStruct s) continue;
                if (Get(s.Properties, F_keyTimes) is not BinTreeContainer tc ||
                    Get(s.Properties, F_keyValues) is not BinTreeContainer vc) continue;
                int n = Math.Min(tc.Elements.Count, vc.Elements.Count);
                if (n == 0) continue;
                var times = new float[n]; var vals = new float[n];
                for (int i = 0; i < n; i++)
                {
                    times[i] = AsF32(tc.Elements[i]) ?? 0f;
                    vals[i] = AsF32(vc.Elements[i]) ?? 0f;
                }
                tables[tableIndex] = new VfxProbTable(times, vals);
                any = true;
            }
            return any ? tables : null;
        }

        private static VfxProbTable[] ReadNestedProbTables(IReadOnlyDictionary<uint, BinTreeProperty> valueProps)
        {
            if (Get(valueProps, F_dynamics) is BinTreeStruct dynamics &&
                ReadProbTables(dynamics.Properties) is { } nested)
                return nested;
            return ReadProbTables(valueProps);
        }

        private static (float[], T[]) ReadDynamics<T>(IReadOnlyDictionary<uint, BinTreeProperty> valueProps, Func<BinTreeProperty, T?> conv)
            where T : struct
        {
            if (Get(valueProps, F_dynamics) is not BinTreeStruct dyn) return (null, null);
            if (Get(dyn.Properties, F_times) is not BinTreeContainer tc) return (null, null);
            if (Get(dyn.Properties, F_values) is not BinTreeContainer vc) return (null, null);

            int n = Math.Min(tc.Elements.Count, vc.Elements.Count);
            if (n == 0) return (null, null);
            var times = new float[n];
            var vals = new T[n];
            for (int i = 0; i < n; i++)
            {
                times[i] = AsF32(tc.Elements[i]) ?? 0f;
                vals[i] = conv(vc.Elements[i]) ?? default;
            }
            return (times, vals);
        }

        private static BinTreeProperty Get(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => p.TryGetValue(hash, out var v) ? v : null;

        private static string GetString(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeString s ? s.Value : null;

        private static float? GetF32(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash) => AsF32(Get(p, hash));

        private static float? GetOptionalF32(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeOptional o ? AsF32(o.Value) : AsF32(Get(p, hash));

        private static int? GetU8(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeU8 u ? u.Value : null;

        private static int? GetU16(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeU16 u ? u.Value : null;

        private static Vector2? GetVec2(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeVector2 v ? v.Value : null;

        private static Vector4? GetVec4(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => AsVec4(Get(p, hash));

        private static Vector2? ReadValueVec2(BinTreeProperty p) => p switch
        {
            BinTreeStruct value => AsVec2(Get(value.Properties, F_constantValue)),
            _ => AsVec2(p),
        };

        private static bool GetBool(
            IReadOnlyDictionary<uint, BinTreeProperty> p,
            uint hash,
            bool defaultValue = false) => Get(p, hash) switch
        {
            BinTreeBool b => b.Value,
            BinTreeBitBool bb => bb.Value,
            _ => defaultValue
        };

        private static float? AsF32(BinTreeProperty p) => p switch
        {
            BinTreeF32 f => f.Value,
            BinTreeU8 u => u.Value,
            BinTreeU16 u => u.Value,
            _ => null
        };

        private static Vector3? AsVec3(BinTreeProperty p) => p switch
        {
            BinTreeVector3 v => v.Value,
            BinTreeVector2 v => new Vector3(v.Value, 0f),
            BinTreeF32 f => new Vector3(f.Value),
            _ => null
        };

        private static Vector2? AsVec2(BinTreeProperty p) => p switch
        {
            BinTreeVector2 v => v.Value,
            BinTreeVector3 v => new Vector2(v.Value.X, v.Value.Y),
            BinTreeF32 f => new Vector2(f.Value),
            _ => null
        };

        private static Vector4? AsVec4(BinTreeProperty p) => p switch
        {
            BinTreeVector4 v => v.Value,
            BinTreeColor c => c.Value,
            BinTreeVector3 v => new Vector4(v.Value, 1f),
            _ => null
        };

        private static uint? AsU32(BinTreeProperty p) => p switch
        {
            BinTreeU32 u => u.Value,
            BinTreeHash h => h.Value,
            BinTreeObjectLink ol => ol.Value,
            _ => null
        };

        private static int? GetI16(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeI16 value ? value.Value : null;

        private static int? GetI32(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeI32 value ? value.Value : null;

    }
}
