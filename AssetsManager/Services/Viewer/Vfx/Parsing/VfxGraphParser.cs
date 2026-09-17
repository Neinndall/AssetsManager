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
    public static class VfxGraphParser
    {
        private static class HashAlgorithms
        {
            public static uint Fnv1a(string text) =>
                text == null ? 0 : LeagueToolkit.Hashing.Fnv1a.HashLower(text);
        }

        // class hashes
        private static readonly uint SystemClass  = HashAlgorithms.Fnv1a("VfxSystemDefinitionData");
        private static readonly uint EmitterClass = HashAlgorithms.Fnv1a("VfxEmitterDefinitionData");

        // system fields
        private static readonly uint F_particleName = HashAlgorithms.Fnv1a("particleName");
        private static readonly uint F_particlePath = HashAlgorithms.Fnv1a("particlePath");
        private static readonly uint F_visibilityRadius = HashAlgorithms.Fnv1a("visibilityRadius");
        private static readonly uint F_transform = HashAlgorithms.Fnv1a("transform");
        private static readonly uint F_systemFlags = HashAlgorithms.Fnv1a("flags");
        private static readonly uint F_buildUpTime = HashAlgorithms.Fnv1a("buildUpTime");
        private const int DefaultSystemFlags = 0xd4;
        private const int AnalyticDragMotionFlag = 0x100;
        private static readonly uint[] EmitterLists =
        {
            HashAlgorithms.Fnv1a("complexEmitterDefinitionData"),
            HashAlgorithms.Fnv1a("simpleEmitterDefinitionData")
        };
        private static readonly uint F_materialOverrideDefinitions = HashAlgorithms.Fnv1a("materialOverrideDefinitions");
        private static readonly uint F_assetRemappingTable = HashAlgorithms.Fnv1a("assetRemappingTable");

        // emitter fields
        private static readonly uint F_emitterName   = HashAlgorithms.Fnv1a("emitterName");
        private static readonly uint F_rate          = HashAlgorithms.Fnv1a("rate");
        private static readonly uint F_particleLife  = HashAlgorithms.Fnv1a("particleLifetime");
        private static readonly uint F_lifetime      = HashAlgorithms.Fnv1a("lifetime");
        private static readonly uint F_particleLinger= HashAlgorithms.Fnv1a("particleLinger");
        private static readonly uint F_particleLingerType = HashAlgorithms.Fnv1a("particleLingerType");
        private static readonly uint F_emitterLinger = HashAlgorithms.Fnv1a("emitterLinger");
        private static readonly uint F_linger = HashAlgorithms.Fnv1a("Linger");
        private static readonly uint F_useLingerRotation = HashAlgorithms.Fnv1a("UseLingerRotation");
        private static readonly uint F_lingerRotation = HashAlgorithms.Fnv1a("LingerRotation");
        private static readonly uint F_useLingerScale = HashAlgorithms.Fnv1a("UseLingerScale");
        private static readonly uint F_lingerScale = HashAlgorithms.Fnv1a("LingerScale");
        private static readonly uint F_useLingerColor = HashAlgorithms.Fnv1a("UseSeparateLingerColor");
        private static readonly uint F_lingerColor = HashAlgorithms.Fnv1a("SeparateLingerColor");
        private static readonly uint F_useLingerAcceleration = HashAlgorithms.Fnv1a("UseKeyedLingerAcceleration");
        private static readonly uint F_lingerAcceleration = HashAlgorithms.Fnv1a("KeyedLingerAcceleration");
        private static readonly uint F_useLingerVelocity = HashAlgorithms.Fnv1a("UseKeyedLingerVelocity");
        private static readonly uint F_lingerVelocity = HashAlgorithms.Fnv1a("KeyedLingerVelocity");
        private static readonly uint F_useLingerDrag = HashAlgorithms.Fnv1a("UseKeyedLingerDrag");
        private static readonly uint F_lingerDrag = HashAlgorithms.Fnv1a("KeyedLingerDrag");
        private static readonly uint F_timeBefore    = HashAlgorithms.Fnv1a("timeBeforeFirstEmission");
        private static readonly uint F_isSingle      = HashAlgorithms.Fnv1a("isSingleParticle");
        private static readonly uint F_disabled      = HashAlgorithms.Fnv1a("disabled");
        private static readonly uint F_importance    = HashAlgorithms.Fnv1a("importance");
        private static readonly uint F_miscRenderFlags = HashAlgorithms.Fnv1a("miscRenderFlags");
        private static readonly uint F_meshRenderFlags = HashAlgorithms.Fnv1a("meshRenderFlags");
        private static readonly uint F_useNavmeshMask = HashAlgorithms.Fnv1a("useNavmeshMask");
        private static readonly uint F_depthBiasFactors = HashAlgorithms.Fnv1a("depthBiasFactors");
        private static readonly uint F_depthPushPull = HashAlgorithms.Fnv1a("DepthPushPull");
        private static readonly uint F_isRotationEnabled = HashAlgorithms.Fnv1a("isRotationEnabled");
        private static readonly uint F_rateIsPeriod  = HashAlgorithms.Fnv1a("rateIsPeriod");
        private static readonly uint F_birthTimePeriod = HashAlgorithms.Fnv1a("birthTimePeriod");
        private static readonly uint F_isLoop        = HashAlgorithms.Fnv1a("isLoop");
        private static readonly uint F_blendMode     = HashAlgorithms.Fnv1a("blendMode");
        private static readonly uint F_renderPass    = HashAlgorithms.Fnv1a("pass");
        private static readonly uint F_alphaRef      = HashAlgorithms.Fnv1a("alphaRef");
        private static readonly uint F_texAddress    = HashAlgorithms.Fnv1a("texAddressModeBase");
        private static readonly uint F_uvScrollClamp = HashAlgorithms.Fnv1a("uvScrollClamp");
        private static readonly uint F_textureFlipU  = HashAlgorithms.Fnv1a("TextureFlipU");
        private static readonly uint F_textureFlipV  = HashAlgorithms.Fnv1a("TextureFlipV");
        private static readonly uint F_disableCull   = HashAlgorithms.Fnv1a("disableBackfaceCull");
        private static readonly uint F_birthScale0   = HashAlgorithms.Fnv1a("birthScale0");
        private static readonly uint F_birthScale1   = HashAlgorithms.Fnv1a("birthScale1");
        private static readonly uint F_scale0        = HashAlgorithms.Fnv1a("scale0");
        private static readonly uint F_birthColor    = HashAlgorithms.Fnv1a("birthColor");
        private static readonly uint F_color         = HashAlgorithms.Fnv1a("color");
        private static readonly uint F_modulationFactor = HashAlgorithms.Fnv1a("modulationFactor");
        private static readonly uint F_particleColorTex = HashAlgorithms.Fnv1a("particleColorTexture");
        private static readonly uint F_colorLookUpX  = HashAlgorithms.Fnv1a("colorLookUpTypeX");
        private static readonly uint F_colorLookUpY  = HashAlgorithms.Fnv1a("colorLookUpTypeY");
        private static readonly uint F_colorLookUpOffsets = HashAlgorithms.Fnv1a("colorLookUpOffsets");
        private static readonly uint F_colorLookUpScales = HashAlgorithms.Fnv1a("colorLookUpScales");
        private static readonly uint F_colorRenderFlags = HashAlgorithms.Fnv1a("colorRenderFlags");
        private static readonly uint F_isTexturePixelated = HashAlgorithms.Fnv1a("isTexturePixelated");
        private static readonly uint F_birthVelocity = HashAlgorithms.Fnv1a("birthVelocity");
        private static readonly uint F_velocity      = HashAlgorithms.Fnv1a("velocity");
        private static readonly uint F_birthAccel    = HashAlgorithms.Fnv1a("birthAcceleration");
        private static readonly uint F_accel         = HashAlgorithms.Fnv1a("acceleration");
        private static readonly uint F_birthOrbital  = HashAlgorithms.Fnv1a("birthOrbitalVelocity");
        private static readonly uint F_worldAccel    = HashAlgorithms.Fnv1a("worldAcceleration");
        private static readonly uint F_birthDrag     = HashAlgorithms.Fnv1a("birthDrag");
        private static readonly uint F_drag          = HashAlgorithms.Fnv1a("drag");
        private static readonly uint F_birthRotation = HashAlgorithms.Fnv1a("birthRotation0");
        private static readonly uint F_rotation      = HashAlgorithms.Fnv1a("rotation0");
        private static readonly uint F_rotation1     = HashAlgorithms.Fnv1a("rotation1");
        private static readonly uint F_birthRotVel0  = HashAlgorithms.Fnv1a("birthRotationalVelocity0");
        private static readonly uint F_birthRotAccel = HashAlgorithms.Fnv1a("birthRotationalAcceleration");
        private static readonly uint F_emitterPos    = HashAlgorithms.Fnv1a("emitterPosition");
        private static readonly uint F_isEmitterSpace = HashAlgorithms.Fnv1a("IsEmitterSpace");
        private static readonly uint F_isLocalOrientation = HashAlgorithms.Fnv1a("isLocalOrientation");
        private static readonly uint F_particleIsLocalOrientation = HashAlgorithms.Fnv1a("particleIsLocalOrientation");
        private static readonly uint F_isFollowingTerrain = HashAlgorithms.Fnv1a("isFollowingTerrain");
        private static readonly uint F_isGroundLayer = HashAlgorithms.Fnv1a("isGroundLayer");
        private static readonly uint F_isUniformScale = HashAlgorithms.Fnv1a("isUniformScale");
        private static readonly uint F_uvMode        = HashAlgorithms.Fnv1a("uvMode");
        private static readonly uint F_bindWeight    = HashAlgorithms.Fnv1a("bindWeight");
        private static readonly uint F_flexShapeDefinition = HashAlgorithms.Fnv1a("FlexShapeDefinition");
        private static readonly uint F_scaleBirthScaleByBoundObjectSize =
            HashAlgorithms.Fnv1a("scaleBirthScaleByBoundObjectSize");
        private static readonly uint F_scaleEmitOffsetByBoundObjectSize =
            HashAlgorithms.Fnv1a("scaleEmitOffsetByBoundObjectSize");
        private static readonly uint F_directionVelocityScale = HashAlgorithms.Fnv1a("directionVelocityScale");
        private static readonly uint F_directionVelocityMinScale = HashAlgorithms.Fnv1a("directionVelocityMinScale");
        private static readonly uint F_paletteDefinition = HashAlgorithms.Fnv1a("paletteDefinition");
        private static readonly uint F_paletteCount = HashAlgorithms.Fnv1a("paletteCount");
        private static readonly uint F_paletteSelector = HashAlgorithms.Fnv1a("paletteSelector");
        private static readonly uint F_paletteTexture = HashAlgorithms.Fnv1a("paletteTexture");
        private static readonly uint F_paletteSourceMixColor = HashAlgorithms.Fnv1a("paletteSrcMixColor");
        private static readonly uint F_palleteSourceMixColor = HashAlgorithms.Fnv1a("palleteSrcMixColor");
        private static readonly uint F_paletteScrollU = HashAlgorithms.Fnv1a("PaletteUAnimationCurve");
        private static readonly uint F_paletteScrollV = HashAlgorithms.Fnv1a("PaletteVAnimationCurve");
        private static readonly uint F_paletteAddressMode = HashAlgorithms.Fnv1a("PaletteTextureAddressMode");
        private static readonly uint F_audio = HashAlgorithms.Fnv1a("Audio");
        private static readonly uint F_soundOnCreate = HashAlgorithms.Fnv1a("SoundOnCreate");
        private static readonly uint F_particlesShareRandomValue = HashAlgorithms.Fnv1a("ParticlesShareRandomValue");
        private static readonly uint F_filtering = HashAlgorithms.Fnv1a("Filtering");
        private static readonly uint F_keywordsExcluded = HashAlgorithms.Fnv1a("keywordsExcluded");
        private static readonly uint F_customMaterial = HashAlgorithms.Fnv1a("CustomMaterial");
        private static readonly uint F_stencilMode = HashAlgorithms.Fnv1a("stencilMode");
        private static readonly uint F_stencilRef = HashAlgorithms.Fnv1a("stencilRef");
        private static readonly uint F_stencilReferenceId = HashAlgorithms.Fnv1a("StencilReferenceId");
        private static readonly uint F_writeAlphaOnly = HashAlgorithms.Fnv1a("WriteAlphaOnly");
        private static readonly uint F_renderPhaseOverride = HashAlgorithms.Fnv1a("renderPhaseOverride");
        private static readonly uint F_sortEmittersByPos = HashAlgorithms.Fnv1a("SortEmittersByPos");
        private static readonly uint F_emissionMeshName = HashAlgorithms.Fnv1a("emissionMeshName");
        private static readonly uint F_emissionSurfaceDefinition = HashAlgorithms.Fnv1a("emissionSurfaceDefinition");
        private static readonly uint F_useEmissionMeshNormal = HashAlgorithms.Fnv1a("useEmissionMeshNormalForBirth");
        private static readonly uint F_translationOverride = HashAlgorithms.Fnv1a("translationOverride");
        private static readonly uint F_rotationOverride = HashAlgorithms.Fnv1a("rotationOverride");
        private static readonly uint F_scaleOverride = HashAlgorithms.Fnv1a("scaleOverride");
        private static readonly uint F_period = HashAlgorithms.Fnv1a("period");
        private static readonly uint F_timeActiveDuringPeriod = HashAlgorithms.Fnv1a("timeActiveDuringPeriod");
        private const uint F_spawnShape              = 0x3bf0b4ed; // SpawnShape
        private static readonly uint F_emitOffset    = HashAlgorithms.Fnv1a("emitOffset");
        private static readonly uint F_birthTranslation = HashAlgorithms.Fnv1a("birthTranslation");
        private static readonly uint F_emitRotAxes   = HashAlgorithms.Fnv1a("emitRotationAxes");
        private static readonly uint F_emitRotAngles = HashAlgorithms.Fnv1a("emitRotationAngles");
        private static readonly uint F_shapeSize      = HashAlgorithms.Fnv1a("Size");
        private static readonly uint F_shapeRadius    = HashAlgorithms.Fnv1a("radius");
        private static readonly uint F_shapeHeight    = HashAlgorithms.Fnv1a("height");
        private static readonly uint F_shapeFlags     = HashAlgorithms.Fnv1a("flags");
        private static readonly uint F_direction     = HashAlgorithms.Fnv1a("isDirectionOriented");
        private static readonly uint F_texture       = HashAlgorithms.Fnv1a("texture");
        private static readonly uint F_textureMult   = HashAlgorithms.Fnv1a("textureMult");
        private static readonly uint F_emitterUvScroll = HashAlgorithms.Fnv1a("emitterUvScrollRate");
        private static readonly uint F_texDiv        = HashAlgorithms.Fnv1a("texDiv");
        private static readonly uint F_texDivMult    = HashAlgorithms.Fnv1a("texDivMult");
        private static readonly uint F_numFrames     = HashAlgorithms.Fnv1a("numFrames");
        private static readonly uint F_randomStart   = HashAlgorithms.Fnv1a("isRandomStartFrame");
        private static readonly uint F_birthFrameRate= HashAlgorithms.Fnv1a("birthFrameRate");
        private static readonly uint F_frameRate     = HashAlgorithms.Fnv1a("frameRate");
        private static readonly uint F_birthUvScrollMult = HashAlgorithms.Fnv1a("birthUvScrollRateMult");
        private static readonly uint F_birthUvOffsetMult = HashAlgorithms.Fnv1a("birthUVOffsetMult");
        private static readonly uint F_particleUvScroll = HashAlgorithms.Fnv1a("particleUVScrollRate");
        private static readonly uint F_particleUvRotate = HashAlgorithms.Fnv1a("particleUVRotateRate");
        private static readonly uint F_birthUvRotate = HashAlgorithms.Fnv1a("birthUvRotateRate");
        private static readonly uint F_particleUvScrollMult = HashAlgorithms.Fnv1a("ParticleIntegratedUvScrollMult");
        private static readonly uint F_particleUvRotateMult = HashAlgorithms.Fnv1a("ParticleIntegratedUvRotateMult");
        private static readonly uint F_birthUvRotateMult = HashAlgorithms.Fnv1a("birthUvRotateRateMult");
        private static readonly uint F_uvScaleMult = HashAlgorithms.Fnv1a("uvScaleMult");
        private static readonly uint F_uvRotationMult = HashAlgorithms.Fnv1a("UvRotationMult");
        private static readonly uint F_texAddressMult = HashAlgorithms.Fnv1a("texAddressModeMult");
        private static readonly uint F_textureMultFlipV = HashAlgorithms.Fnv1a("TextureMultFilpV");
        private static readonly uint F_textureMultFlipU = HashAlgorithms.Fnv1a("TextureMultFilpU");
        private static readonly uint F_textureMultTransformCenter = HashAlgorithms.Fnv1a("uvTransformCenterMult");
        private static readonly uint F_textureMultClampUv = HashAlgorithms.Fnv1a("uvScrollClampMult");
        private static readonly uint F_textureMultEmitterUvScroll = HashAlgorithms.Fnv1a("emitterUvScrollRateMult");
        private static readonly uint F_birthUvOffset = HashAlgorithms.Fnv1a("birthUVOffset");
        private static readonly uint F_uvScale       = HashAlgorithms.Fnv1a("uvScale");
        private static readonly uint F_uvRotation    = HashAlgorithms.Fnv1a("uvRotation");
        private static readonly uint F_uvTransformCenter = HashAlgorithms.Fnv1a("uvTransformCenter");
        private static readonly uint F_primitive     = HashAlgorithms.Fnv1a("primitive");
        private static readonly uint F_startFrame    = HashAlgorithms.Fnv1a("startFrame");
        private static readonly uint F_legacySimple  = HashAlgorithms.Fnv1a("LegacySimple");
        private static readonly uint F_legacyBirthScale = HashAlgorithms.Fnv1a("birthScale");
        private static readonly uint F_legacyScale = HashAlgorithms.Fnv1a("scale");
        private static readonly uint F_legacyBirthRotation = HashAlgorithms.Fnv1a("birthRotation");
        private static readonly uint F_legacyBirthRotVel = HashAlgorithms.Fnv1a("birthRotationalVelocity");
        private static readonly uint F_legacyScaleBias = HashAlgorithms.Fnv1a("scaleBias");
        private static readonly uint F_legacyRotation = HashAlgorithms.Fnv1a("rotation");
        private static readonly uint F_legacyLockedToEmitter = HashAlgorithms.Fnv1a("lockedToEmitter");
        private static readonly uint F_legacyOrientation = HashAlgorithms.Fnv1a("orientation");
        private static readonly uint F_legacyUvScrollRate = HashAlgorithms.Fnv1a("uvScrollRate");
        private static readonly uint F_legacyScaleUpFromOrigin = HashAlgorithms.Fnv1a("scaleUpFromOrigin");
        private static readonly uint F_shape = HashAlgorithms.Fnv1a("shape");
        private static readonly uint F_distortionDefinition = HashAlgorithms.Fnv1a("distortionDefinition");
        private static readonly uint F_distortion = HashAlgorithms.Fnv1a("distortion");
        private static readonly uint F_distortionMode = HashAlgorithms.Fnv1a("distortionMode");
        private static readonly uint F_normalMapTexture = HashAlgorithms.Fnv1a("normalMapTexture");
        private static readonly uint F_alphaErosionDefinition = HashAlgorithms.Fnv1a("alphaErosionDefinition");
        private static readonly uint F_erosionMapName = HashAlgorithms.Fnv1a("erosionMapName");
        private static readonly uint F_erosionDriveCurve = HashAlgorithms.Fnv1a("erosionDriveCurve");
        private static readonly uint F_useLingerErosionDrive = HashAlgorithms.Fnv1a("UseLingerErosionDriveCurve");
        private static readonly uint F_lingerErosionDrive = HashAlgorithms.Fnv1a("LingerErosionDriveCurve");
        private static readonly uint F_erosionDriveSource = HashAlgorithms.Fnv1a("erosionDriveSource");
        private static readonly uint F_erosionFeatherIn = HashAlgorithms.Fnv1a("erosionFeatherIn");
        private static readonly uint F_erosionFeatherOut = HashAlgorithms.Fnv1a("erosionFeatherOut");
        private static readonly uint F_erosionSliceWidth = HashAlgorithms.Fnv1a("erosionSliceWidth");
        private static readonly uint F_erosionMapAddressMode = HashAlgorithms.Fnv1a("erosionMapAddressMode");
        private static readonly uint F_erosionMapChannelMixer = HashAlgorithms.Fnv1a("erosionMapChannelMixer");
        private static readonly uint F_softParticleParams = HashAlgorithms.Fnv1a("softParticleParams");
        private static readonly uint F_softBeginIn = HashAlgorithms.Fnv1a("beginIn");
        private static readonly uint F_softDeltaIn = HashAlgorithms.Fnv1a("deltaIn");
        private static readonly uint F_softBeginOut = HashAlgorithms.Fnv1a("beginOut");
        private static readonly uint F_softDeltaOut = HashAlgorithms.Fnv1a("deltaOut");
        private static readonly uint F_reflectionDefinition = HashAlgorithms.Fnv1a("reflectionDefinition");
        private static readonly uint F_reflectionOpacityDirect = HashAlgorithms.Fnv1a("reflectionOpacityDirect");
        private static readonly uint F_reflectionOpacityGlancing = HashAlgorithms.Fnv1a("reflectionOpacityGlancing");
        private static readonly uint F_reflectionFresnel = HashAlgorithms.Fnv1a("reflectionFresnel");
        private static readonly uint F_fresnel = HashAlgorithms.Fnv1a("fresnel");
        private static readonly uint F_fresnelColor = HashAlgorithms.Fnv1a("fresnelColor");
        private static readonly uint F_reflectionFresnelColor = HashAlgorithms.Fnv1a("reflectionFresnelColor");
        private static readonly uint F_reflectionMapTexture = HashAlgorithms.Fnv1a("reflectionMapTexture");
        private static readonly uint F_childParticleSet = HashAlgorithms.Fnv1a("childParticleSetDefinition");
        private static readonly uint F_childrenIdentifiers = HashAlgorithms.Fnv1a("childrenIdentifiers");
        private static readonly uint F_boneToSpawnAt = HashAlgorithms.Fnv1a("boneToSpawnAt");
        private static readonly uint F_childEmitOnDeath = HashAlgorithms.Fnv1a("childEmitOnDeath");
        private static readonly uint F_childrenProbability = HashAlgorithms.Fnv1a("childrenProbability");
        private static readonly uint F_parentInheritance = HashAlgorithms.Fnv1a("ParentInheritanceDefinition");
        private static readonly uint F_relativeOffset = HashAlgorithms.Fnv1a("RelativeOffset");
        private static readonly uint F_inheritanceMode = HashAlgorithms.Fnv1a("Mode");
        private static readonly uint F_effectName = HashAlgorithms.Fnv1a("effectName");
        private static readonly uint F_effect = HashAlgorithms.Fnv1a("effect");
        private static readonly uint F_effectKey = HashAlgorithms.Fnv1a("effectKey");
        private static readonly uint F_fieldCollection = HashAlgorithms.Fnv1a("fieldCollectionDefinition");
        private static readonly uint F_fieldAccelerationDefinitions = HashAlgorithms.Fnv1a("fieldAccelerationDefinitions");
        private static readonly uint F_fieldAttractionDefinitions = HashAlgorithms.Fnv1a("fieldAttractionDefinitions");
        private static readonly uint F_fieldDragDefinitions = HashAlgorithms.Fnv1a("fieldDragDefinitions");
        private static readonly uint F_fieldOrbitalDefinitions = HashAlgorithms.Fnv1a("fieldOrbitalDefinitions");
        private static readonly uint F_fieldNoiseDefinitions = HashAlgorithms.Fnv1a("fieldNoiseDefinitions");
        private static readonly uint F_isLocalSpace = HashAlgorithms.Fnv1a("isLocalSpace");
        private static readonly uint F_position = HashAlgorithms.Fnv1a("position");
        private static readonly uint F_radius = HashAlgorithms.Fnv1a("radius");
        private static readonly uint F_strength = HashAlgorithms.Fnv1a("strength");
        private static readonly uint F_directionField = HashAlgorithms.Fnv1a("direction");
        private static readonly uint F_frequency = HashAlgorithms.Fnv1a("frequency");
        private static readonly uint F_velocityDelta = HashAlgorithms.Fnv1a("velocityDelta");
        private static readonly uint F_axisFraction = HashAlgorithms.Fnv1a("axisFraction");

        // Value* / dynamics inner fields
        private static readonly uint F_constantValue = HashAlgorithms.Fnv1a("constantValue");
        private static readonly uint F_dynamics      = HashAlgorithms.Fnv1a("dynamics");
        private static readonly uint F_times         = HashAlgorithms.Fnv1a("times");
        private static readonly uint F_values        = HashAlgorithms.Fnv1a("values");
        private static readonly uint F_probTables    = HashAlgorithms.Fnv1a("probabilityTables");
        private static readonly uint F_keyTimes      = HashAlgorithms.Fnv1a("keyTimes");
        private static readonly uint F_keyValues     = HashAlgorithms.Fnv1a("keyValues");
        private static readonly uint F_singleValue   = HashAlgorithms.Fnv1a("singleValue");
        private static readonly uint F_meshDef       = 0x0d89732d; // VfxPrimitiveMesh's VfxMeshDefinitionData field
        private static readonly uint F_simpleMesh    = HashAlgorithms.Fnv1a("mSimpleMeshName");
        private static readonly uint F_meshName      = HashAlgorithms.Fnv1a("mMeshName");
        private static readonly uint F_meshAlignPitch = HashAlgorithms.Fnv1a("AlignPitchToCamera");
        private static readonly uint F_meshAlignYaw = HashAlgorithms.Fnv1a("AlignYawToCamera");
        private static readonly uint F_submeshesToDrawAlways = HashAlgorithms.Fnv1a("mSubmeshesToDrawAlways");
        private static readonly uint F_submeshesToDraw = HashAlgorithms.Fnv1a("mSubmeshesToDraw");
        private static readonly uint F_birthUvScroll = HashAlgorithms.Fnv1a("birthUvScrollRate");
        private static readonly uint F_meshSkeleton  = 0x90595a15; // VfxMeshDefinitionData skeleton field
        private static readonly uint F_meshAnim      = HashAlgorithms.Fnv1a("mAnimationName");
        private static readonly uint F_trailDefinition = HashAlgorithms.Fnv1a("mTrail");
        private static readonly uint F_beamDefinition = HashAlgorithms.Fnv1a("mBeam");
        private static readonly uint F_trailBirthTilingSize = HashAlgorithms.Fnv1a("mBirthTilingSize");
        private static readonly uint F_trailSmoothingMode = HashAlgorithms.Fnv1a("mSmoothingMode");
        private static readonly uint F_trailMode = HashAlgorithms.Fnv1a("mMode");
        private static readonly uint F_trailMaxAddedPerFrame = HashAlgorithms.Fnv1a("mMaxAddedPerFrame");
        private static readonly uint F_trailCutoff = HashAlgorithms.Fnv1a("mCutoff");
        private static readonly uint F_beamTrailMode = HashAlgorithms.Fnv1a("mTrailMode");
        private static readonly uint F_beamSegments = HashAlgorithms.Fnv1a("mSegments");
        private static readonly uint F_beamColor = HashAlgorithms.Fnv1a("mAnimatedColorWithDistance");
        private static readonly uint F_beamColorBound = HashAlgorithms.Fnv1a("mIsColorBindedWithDistance");
        private static readonly uint F_beamSourceOffset = HashAlgorithms.Fnv1a("mLocalSpaceSourceOffset");
        private static readonly uint F_beamTargetOffset = HashAlgorithms.Fnv1a("mLocalSpaceTargetOffset");

        // primitive class hashes we treat as "mesh"
        private static readonly uint PrimMesh = HashAlgorithms.Fnv1a("VfxPrimitiveMesh");
        private static readonly uint PrimAttachedMesh = HashAlgorithms.Fnv1a("VfxPrimitiveAttachedMesh");
        private static readonly uint PrimArbitraryQuad = HashAlgorithms.Fnv1a("VfxPrimitiveArbitraryQuad");
        private static readonly uint PrimCameraQuad = HashAlgorithms.Fnv1a("VfxPrimitiveCameraQuad");
        private static readonly uint PrimCameraUnitQuad = HashAlgorithms.Fnv1a("VfxPrimitiveCameraUnitQuad");
        private static readonly uint PrimCameraTrail = HashAlgorithms.Fnv1a("VfxPrimitiveCameraTrail");
        private static readonly uint PrimArbitraryTrail = HashAlgorithms.Fnv1a("VfxPrimitiveArbitraryTrail");
        private static readonly uint PrimRay = HashAlgorithms.Fnv1a("VfxPrimitiveRay");
        private static readonly uint PrimBeam = HashAlgorithms.Fnv1a("VfxPrimitiveBeam");
        private static readonly uint PrimCameraSegmentBeam = HashAlgorithms.Fnv1a("VfxPrimitiveCameraSegmentBeam");
        private static readonly uint PrimPlanarProjection = HashAlgorithms.Fnv1a("VfxPrimitivePlanarProjection");
        private static readonly uint ShapeLegacy = HashAlgorithms.Fnv1a("VfxShapeLegacy");
        private static readonly uint ShapeOld = HashAlgorithms.Fnv1a("VfxShape");
        private static readonly uint ShapeBox = HashAlgorithms.Fnv1a("VfxShapeBox");
        private static readonly uint ShapeSphere = HashAlgorithms.Fnv1a("VfxShapeSphere");
        private static readonly uint ShapeCylinder = HashAlgorithms.Fnv1a("VfxShapeCylinder");

        private static readonly uint ResolverClass = HashAlgorithms.Fnv1a("ResourceResolver");
        private static readonly uint F_resourceMap = HashAlgorithms.Fnv1a("resourceMap");
        private static readonly uint F_mResourceMap = HashAlgorithms.Fnv1a("mResourceMap");

        // AnimationGraph clip event fields
        private static readonly uint SubmeshVisibilityEventClass = 0xbcf56e70;
        private static readonly uint ParticleEventClass = HashAlgorithms.Fnv1a("ParticleEventData");
        private static readonly uint JointSnapEventClass = 0xb5c1b6ad;
        private static readonly uint ConformToPathEventClass = 0x82377a1d;
        private static readonly uint F_eventDataMap = HashAlgorithms.Fnv1a("mEventDataMap");
        private static readonly uint F_clipDataMap = HashAlgorithms.Fnv1a("mClipDataMap");
        private static readonly uint F_clipTickDuration = HashAlgorithms.Fnv1a("mTickDuration");
        private static readonly uint F_clipStartFrame = HashAlgorithms.Fnv1a("startFrame");
        private static readonly uint F_clipEndFrame = HashAlgorithms.Fnv1a("EndFrame");
        private static readonly uint SkinCharacterDataPropertiesClass = HashAlgorithms.Fnv1a("SkinCharacterDataProperties");
        private static readonly uint F_skinMeshProperties = HashAlgorithms.Fnv1a("skinMeshProperties");
        private static readonly uint F_simpleSkin = HashAlgorithms.Fnv1a("simpleSkin");
        private static readonly uint F_ownerSkeleton = HashAlgorithms.Fnv1a("skeleton");
        private static readonly uint F_skinScale = HashAlgorithms.Fnv1a("skinScale");
        private static readonly uint F_initialSubmeshToHide = HashAlgorithms.Fnv1a("initialSubmeshToHide");
        private static readonly uint F_skinAnimationProperties = HashAlgorithms.Fnv1a("skinAnimationProperties");
        private static readonly uint F_animationGraphData = HashAlgorithms.Fnv1a("animationGraphData");
        private static readonly uint F_eventName = HashAlgorithms.Fnv1a("mName");
        private static readonly uint F_eventStartFrame = HashAlgorithms.Fnv1a("mStartFrame");
        private static readonly uint F_eventEndFrame = HashAlgorithms.Fnv1a("mEndFrame");
        private static readonly uint F_eventIsSelfOnly = HashAlgorithms.Fnv1a("mIsSelfOnly");
        private static readonly uint F_eventFireIfAnimationEndsEarly = HashAlgorithms.Fnv1a("mFireIfAnimationEndsEarly");
        private static readonly uint F_eventEffectKey = HashAlgorithms.Fnv1a("mEffectKey");
        private static readonly uint F_eventEnemyEffectKey = HashAlgorithms.Fnv1a("mEnemyEffectKey");
        private static readonly uint F_eventEffectName = HashAlgorithms.Fnv1a("mEffectName");
        private static readonly uint F_eventIsLoop = HashAlgorithms.Fnv1a("mIsLoop");
        private static readonly uint F_eventIsKill = HashAlgorithms.Fnv1a("mIsKillEvent");
        private static readonly uint F_eventIsDetachable = HashAlgorithms.Fnv1a("mIsDetachable");
        private static readonly uint F_eventSkipIfPastEndFrame = HashAlgorithms.Fnv1a("SkipIfPastEndFrame");
        private static readonly uint F_eventScalePlaySpeed = HashAlgorithms.Fnv1a("mScalePlaySpeedWithAnimation");
        private static readonly uint F_eventScale = HashAlgorithms.Fnv1a("scale");
        private static readonly uint F_eventPairList = HashAlgorithms.Fnv1a("mParticleEventDataPairList");
        private static readonly uint F_eventSourceBone = HashAlgorithms.Fnv1a("mBoneName");
        private static readonly uint F_eventTargetBone = HashAlgorithms.Fnv1a("mTargetBoneName");
        private static readonly uint F_eventShowSubmeshes = 0x6d4d42d0;
        private static readonly uint F_eventHideSubmeshes = 0xbb41a45b;
        private static readonly uint F_eventJoint = 0xac70ab62;
        private static readonly uint F_eventSnapTo = 0xf6e6d893;
        private static readonly uint F_eventOffset = 0x14c8d3ca;
        private static readonly uint F_eventMaskDataName = 0x0359739b;
        private static readonly uint F_eventBlendIn = 0xdf2f42a9;
        private static readonly uint F_eventBlendOut = 0xa8c578b4;
        private static readonly uint F_parametricPairs = 0x2ec3ba66;
        private static readonly uint F_parametricPairValue = 0x24f2ec89;
        private static readonly uint F_idleParticlesEffects = 0x84186f3c;
        private static readonly uint F_idlePosition = 0x934f4e0a;
        private static readonly uint F_animationResourceData = 0xb49f754e;
        private static readonly uint F_animationFilePath = 0x0329f1d7;
        private static readonly uint F_clipName = HashAlgorithms.Fnv1a("mClipName");
        private static readonly uint F_trackDataName = HashAlgorithms.Fnv1a("mTrackDataName");
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
                ExtractOwnerSceneContext(tree),
                ExtractIdleEffects(tree));
        }

        private static IReadOnlyList<VfxIdleEffectDefinition> ExtractIdleEffects(BinTree tree)
        {
            var idleEffects = new List<VfxIdleEffectDefinition>();
            foreach (BinTreeObject owner in tree.Objects.Values)
            {
                if (owner.ClassHash != SkinCharacterDataPropertiesClass) continue;
                var prop = Get(owner.Properties, F_idleParticlesEffects);
                if (prop is not BinTreeContainer container) continue;
                foreach (var elem in container.Elements.OfType<BinTreeStruct>())
                {
                    uint effectKey = AsU32(Get(elem.Properties, F_eventEffectKey)) ?? 0u;
                    string effectName = GetString(elem.Properties, F_eventEffectName) ?? string.Empty;
                    string boneName = GetString(elem.Properties, F_eventSourceBone) ?? string.Empty;
                    uint boneNameHash = AsU32(Get(elem.Properties, F_eventSourceBone)) ?? (string.IsNullOrEmpty(boneName) ? 0u : HashAlgorithms.Fnv1a(boneName));
                    string targetBoneName = GetString(elem.Properties, F_eventTargetBone) ?? string.Empty;
                    uint targetBoneNameHash = AsU32(Get(elem.Properties, F_eventTargetBone)) ?? (string.IsNullOrEmpty(targetBoneName) ? 0u : HashAlgorithms.Fnv1a(targetBoneName));
                    Vector3 position = AsVec3(Get(elem.Properties, F_idlePosition)) ?? Vector3.Zero;

                    idleEffects.Add(new VfxIdleEffectDefinition(
                        effectKey,
                        effectName,
                        boneName,
                        boneNameHash,
                        targetBoneName,
                        targetBoneNameHash,
                        position));
                }
            }
            return idleEffects;
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

                string meshPath = ReadAsset(meshProperties.Properties, F_simpleSkin, ".skn");
                if (string.IsNullOrWhiteSpace(meshPath)) continue;

                uint animationGraphPathHash = 0u;
                if (Get(owner.Properties, F_skinAnimationProperties) is BinTreeStruct animationProperties)
                    animationGraphPathHash = AsU32(Get(animationProperties.Properties, F_animationGraphData)) ?? 0u;

                return new VfxOwnerSceneContext(
                    meshPath,
                    ReadAsset(meshProperties.Properties, F_ownerSkeleton, ".skl") ?? string.Empty,
                    Math.Max(0.01f, GetF32(meshProperties.Properties, F_skinScale) ?? 1f),
                    animationGraphPathHash,
                    ReadSubmeshNameHashes(GetString(meshProperties.Properties, F_initialSubmeshToHide)));
            }
            return null;
        }

        private static IReadOnlyList<AnimationClipDefinition> ExtractEventSequences(BinTree tree)
        {
            var sequences = new List<AnimationClipDefinition>();
            foreach (BinTreeObject owner in tree.Objects.Values)
            {
                AddEventSequence(sequences, owner.PathHash, owner.ClassHash, owner.Properties);
                if (Get(owner.Properties, F_clipDataMap) is not BinTreeMap clipMap) continue;
                foreach (var clipPair in clipMap)
                {
                    if (clipPair.Value is not BinTreeStruct clip) continue;

                    uint clipHash = AsU32(clipPair.Key) ?? 0u;
                    // LTK identifies a clip by the mClipDataMap key. If the hash table cannot
                    // spell that key, the clip stays a hex hash; the .anm filename is not its identity.
                    string clipName = clipPair.Key switch
                    {
                        BinTreeString s when !string.IsNullOrWhiteSpace(s.Value) => s.Value,
                        _ when clipHash != 0u => $"0x{clipHash:x8}",
                        _ => string.Empty
                    };

                    string animFilePath = null;
                    if (Get(clip.Properties, F_animationResourceData) is BinTreeStruct animResource)
                    {
                        animFilePath = ReadAsset(animResource.Properties, F_animationFilePath, ".anm");
                    }

                    AddEventSequence(
                        sequences,
                        clipHash,
                        clip.ClassHash,
                        clip.Properties,
                        clipName,
                        animFilePath,
                        owner.PathHash,
                        ReadClipChildren(clip.Properties),
                        ReadClipParameters(clip.Properties),
                        includeEmpty: true);
                }
            }
            return sequences;
        }

        private static void AddEventSequence(
            ICollection<AnimationClipDefinition> sequences,
            uint ownerPathHash,
            uint ownerClassHash,
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            string clipName = null,
            string animationFilePath = null,
            uint graphPathHash = 0,
            IReadOnlyList<uint> childClipHashes = null,
            IReadOnlyList<float> childParameters = null,
            bool includeEmpty = false)
        {
            var events = new List<AnimationClipEventDefinition>();
            if (Get(properties, F_eventDataMap) is BinTreeMap eventMap)
            {
                foreach (var pair in eventMap)
                {
                    if (pair.Value is not BinTreeStruct eventData) continue;
                    events.Add(ParseClipEvent(AsU32(pair.Key) ?? 0u, eventData));
                }
            }
            if (events.Count == 0 && !includeEmpty) return;
            sequences.Add(new AnimationClipDefinition(
                ownerPathHash,
                ownerClassHash,
                GetF32(properties, F_clipTickDuration) is { } tick && float.IsFinite(tick) && tick > 0 ? tick : 0,
                GetF32(properties, F_clipStartFrame) ?? 0f,
                GetF32(properties, F_clipEndFrame) ?? -1f,
                events,
                clipName,
                animationFilePath,
                graphPathHash,
                childClipHashes,
                childParameters));
        }

        private static IReadOnlyList<uint> ReadClipChildren(IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            var children = new List<uint>();
            void Add(BinTreeProperty property)
            {
                if (AsU32(property) is uint hash && hash != 0u) children.Add(hash);
            }
            foreach (var (field, itemField) in new (uint, uint)[]
            {
                (0x2ec3ba66, 0xca2b847d), (0x512c9525, 0xca2b847d),
                (0x4d7a54c0, 0), (0x24af5ac1, 0), (0x2329eec5, 0xca2b847d),
                (0x078cafd9, 0), (0xd188b400, 0x68c14f60), (0x9a7f92cb, 0),
                (0x8e5e6618, 0), (0x21328a43, 0xc6f291ed), (0x778d6dee, 0x68c14f60)
            })
            {
                BinTreeProperty value = Get(properties, field);
                if (value is BinTreeContainer container)
                {
                    foreach (BinTreeProperty item in container.Elements)
                        Add(itemField != 0 && item is BinTreeStruct pair ? Get(pair.Properties, itemField) : item);
                }
                else Add(value);
            }
            return children;
        }

        private static IReadOnlyList<float> ReadClipParameters(IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            if (Get(properties, F_parametricPairs) is not BinTreeContainer pairs)
                return Array.Empty<float>();

            var values = new List<float>(pairs.Elements.Count);
            foreach (BinTreeStruct pair in pairs.Elements.OfType<BinTreeStruct>())
                values.Add(GetF32(pair.Properties, F_parametricPairValue) ?? 0f);
            return values;
        }

        private static AnimationClipEventDefinition ParseClipEvent(uint eventHash, BinTreeStruct eventData)
        {
            IReadOnlyDictionary<uint, BinTreeProperty> properties = eventData.Properties;
            float startFrame = GetF32(properties, F_eventStartFrame) ?? 0f;
            float endFrame = GetF32(properties, F_eventEndFrame) ?? -1f;

            if (eventData.ClassHash == ParticleEventClass)
                return ParseParticleEvent(eventHash, eventData);

            if (eventData.ClassHash == SubmeshVisibilityEventClass)
            {
                return new AnimationSubmeshVisibilityEventDefinition(
                    eventHash,
                    startFrame,
                    endFrame,
                    ReadHashList(Get(properties, F_eventShowSubmeshes)),
                    ReadHashList(Get(properties, F_eventHideSubmeshes)));
            }

            if (eventData.ClassHash == JointSnapEventClass)
            {
                return new AnimationJointSnapEventDefinition(
                    eventHash,
                    startFrame,
                    endFrame,
                    AsU32(Get(properties, F_eventJoint)) ?? 0u,
                    AsU32(Get(properties, F_eventSnapTo)) ?? 0u,
                    AsVec3(Get(properties, F_eventOffset)) ?? Vector3.Zero);
            }

            if (eventData.ClassHash == ConformToPathEventClass)
            {
                return new AnimationConformToPathEventDefinition(
                    eventHash,
                    startFrame,
                    endFrame,
                    AsU32(Get(properties, F_eventMaskDataName)) ?? 0u,
                    GetF32(properties, F_eventBlendIn) ?? 0f,
                    GetF32(properties, F_eventBlendOut) ?? 0f);
            }

            return new AnimationOtherClipEventDefinition(
                eventHash,
                startFrame,
                endFrame,
                eventData.ClassHash);
        }

        private static IReadOnlyList<uint> ReadHashList(BinTreeProperty property)
        {
            if (property is not BinTreeContainer container)
                return AsU32(property) is uint single && single != 0u ? new[] { single } : Array.Empty<uint>();

            return container.Elements
                .Select(AsU32)
                .Where(value => value.HasValue && value.Value != 0u)
                .Select(value => value.Value)
                .ToArray();
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
                GetBool(properties, F_eventIsLoop),
                GetBool(properties, F_eventIsKill),
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
                if (prop is not BinTreeMap entries) continue;
                foreach (var kv in entries)
                {
                    uint key = AsU32(kv.Key) ?? 0u;
                    uint value = AsU32(kv.Value) ?? 0u;
                    if (key != 0 && value != 0) map.TryAdd(key, value);
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
            foreach (uint listHash in EmitterLists)
            {
                if (Get(o.Properties, listHash) is not BinTreeContainer c) continue;
                foreach (var el in c.Elements)
                    if (el is BinTreeStruct s && s.ClassHash == EmitterClass)
                        emitters.Add(ParseEmitter(s));
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

        private static VfxEmitterDefinition ParseEmitter(BinTreeStruct s)
        {
            var p = s.Properties;

            var legacy = Get(p, F_legacySimple) as BinTreeStruct;
            var legacyBirthScale = legacy is null ? null : ReadCurveF(legacy.Properties, F_legacyBirthScale);
            Vector2 legacyScaleBias = legacy is null
                ? Vector2.One
                : GetVec2(legacy.Properties, F_legacyScaleBias) ?? Vector2.One;
            VfxCurveF? legacyScaleCurve = legacy is null ? null : ReadCurveF(legacy.Properties, F_legacyScale);
            VfxCurveF? legacyRotationCurve = legacy is null ? null : ReadCurveF(legacy.Properties, F_legacyRotation);
            bool legacyLockedToEmitter = legacy is not null && GetBool(legacy.Properties, F_legacyLockedToEmitter);
            byte legacyOrientation = legacy is null ? (byte)0 : (byte)(GetU8(legacy.Properties, F_legacyOrientation) ?? 0);
            Vector2 legacyUvScroll = legacy is null
                ? Vector2.Zero
                : GetVec2(legacy.Properties, F_legacyUvScrollRate) ?? Vector2.Zero;
            bool legacyScaleUpFromOrigin = legacy is not null && GetBool(legacy.Properties, F_legacyScaleUpFromOrigin);
            var birthScale = ReadCurve3(p, F_birthScale0)
                ?? (legacyBirthScale is { } lbs ? ScalarSizeCurve(lbs) : VfxCurve3.Const(Vector3.One));
            var birthScale1 = ReadCurve3(p, F_birthScale1);
            var scaleOverLife = ReadCurve3(p, F_scale0);
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
            uint primitiveClass = prim is BinTreeStruct primitive ? primitive.ClassHash : PrimCameraQuad;
            VfxPrimitiveKind primitiveKind = GetPrimitiveKind(primitiveClass);
            bool isMesh = primitiveKind is VfxPrimitiveKind.Mesh or VfxPrimitiveKind.AttachedMesh;
            bool isArbitraryQuad = prim is BinTreeStruct aq && aq.ClassHash == PrimArbitraryQuad;
            string meshPath = null, meshSkl = null, meshAnm = null;
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
                if (!IsNoMeshPath(skinnedMesh) && !IsNoMeshPath(skeleton))
                {
                    meshPath = skinnedMesh;
                    meshSkl = skeleton;
                    meshIsSkinned = true;
                }
                else
                {
                    string simpleMesh = ReadAsset(md.Properties, F_simpleMesh, ".scb");
                    if (!IsNoMeshPath(simpleMesh)) meshPath = simpleMesh;
                }

                meshAnm = ReadAsset(md.Properties, F_meshAnim, ".anm");
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
            if (primitiveKind is VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam &&
                prim is BinTreeStruct beamPrimitive)
            {
                IReadOnlyDictionary<uint, BinTreeProperty> bp =
                    Get(beamPrimitive.Properties, F_beamDefinition) is BinTreeStruct beamData
                        ? beamData.Properties
                        : new Dictionary<uint, BinTreeProperty>();
                beam = new VfxBeamDefinition(
                    GetU8(bp, F_trailMode) ?? GetI32(bp, F_trailMode) ?? 0,
                    GetU8(bp, F_beamTrailMode) ?? GetI32(bp, F_beamTrailMode) ?? 0,
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
            if (Get(p, F_textureMult) is BinTreeStruct textureMult)
            {
                textureMultPath = ReadAsset(textureMult.Properties, F_textureMult, ".tex");
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
                    ReadCurveF(ep, F_erosionDriveCurve) ?? VfxCurveF.Const(1f),
                    GetF32(ep, F_erosionFeatherIn) ?? 0.1f,
                    GetF32(ep, F_erosionFeatherOut) ?? 0.1f,
                    samplerAddress,
                    ReadCurve4(ep, F_erosionMapChannelMixer),
                    GetF32(ep, F_erosionSliceWidth) ?? 1.5f,
                    GetBool(ep, F_useLingerErosionDrive)
                        ? ReadCurveF(ep, F_lingerErosionDrive) ?? VfxCurveF.Const(1f)
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
                BlendMode: GetU8(p, F_blendMode) ?? (byte?)(AsU32(Get(p, F_blendMode))) ?? VfxAuthoredDefaults.BlendMode,
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
                BirthFrameRate: ReadCurveF(p, F_birthFrameRate),
                FrameRate: GetF32(p, F_frameRate),
                TextureMultPath: textureMultPath,
                TextureMultTexDiv: textureMultTexDiv,
                TextureMultUvScrollRate: textureMultUvScroll,
                StartFrame: GetU16(p, F_startFrame) ?? 0,
                UseTextureAspect: legacy is not null,
                Distortion: distortion,
                ParticleColorTexturePath: ReadAsset(p, F_particleColorTex, ".tex"),
                ColorLookUpTypeX: GetU8(p, F_colorLookUpX) ?? VfxAuthoredDefaults.ColorLookUpTypeX,
                ColorLookUpTypeY: GetU8(p, F_colorLookUpY) ?? VfxAuthoredDefaults.ColorLookUpTypeY,
                RenderState: new VfxEmitterRenderState(
                    RenderPass: GetI16(p, F_renderPass) ?? 0,
                    AlphaReference: (byte)(GetU8(p, F_alphaRef) ?? VfxAuthoredDefaults.AlphaReference),
                    TextureAddressMode: GetU8(p, F_texAddress) ?? 0,
                    ClampUvScroll: GetBool(p, F_uvScrollClamp),
                    FlipU: GetBool(p, F_textureFlipU),
                    FlipV: GetBool(p, F_textureFlipV),
                    DisableBackfaceCull: GetBool(p, F_disableCull),
                    RenderPhase: (byte)(GetU8(p, F_renderPhaseOverride) ?? VfxAuthoredDefaults.RenderPhaseOverride),
                    StencilMode: (byte)(GetU8(p, F_stencilMode) ?? VfxAuthoredDefaults.StencilMode),
                    StencilReference: (byte)(GetU8(p, F_stencilRef) ?? VfxAuthoredDefaults.StencilReference),
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
                UvScale: ReadCurve2(p, F_uvScale),
                UvRotation: ReadCurveF(p, F_uvRotation),
                AlphaErosion: alphaErosion,
                ChildParticleSet: childParticleSet,
                Fields: fields,
                ParticleLingerType: (byte)(GetU8(p, F_particleLingerType) ?? 0),
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
                Importance: (byte)(GetU8(p, F_importance) ?? VfxAuthoredDefaults.Importance),
                BirthScale1: birthScale1,
                Rotation1: ReadCurve3(p, F_rotation1),
                UvMode: (byte)(GetU8(p, F_uvMode) ?? 0),
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
                DepthPushPull: GetF32(p, F_depthPushPull) ?? 0f,
                Beam: beam,
                Linger: linger,
                SubmeshesToDraw: submeshesToDraw,
                SubmeshesToDrawAlways: submeshesToDrawAlways,
                AttachedSubmeshHashes: attachedSubmeshHashes,
                AuthoredFeatures: new VfxEmitterAuthoredFeatures(
                    PrimitiveClassHash: primitiveClass,
                    HasCustomMaterial: HasValue(p, F_customMaterial),
                    HasStencil: (GetU8(p, F_stencilMode) ?? 0) != 0 ||
                        (GetU8(p, F_stencilRef) ?? 0) != 0 ||
                        (AsU32(Get(p, F_stencilReferenceId)) ?? 0u) != 0,
                    HasEmissionMesh: HasValue(p, F_emissionMeshName),
                    HasEmissionSurface: HasValue(p, F_emissionSurfaceDefinition),
                    UsesEmissionMeshNormal: GetBool(p, F_useEmissionMeshNormal),
                    HasTranslationOverride: HasValue(p, F_translationOverride),
                    HasRotationOverride: HasValue(p, F_rotationOverride),
                    HasScaleOverride: HasValue(p, F_scaleOverride),
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

        private static VfxLingerDefinition ReadLinger(
            IReadOnlyDictionary<uint, BinTreeProperty> emitterProperties)
        {
            if (Get(emitterProperties, F_linger) is not BinTreeStruct linger) return null;
            var p = linger.Properties;
            VfxCurve3? rotation = GetBool(p, F_useLingerRotation)
                ? ReadCurve3(p, F_lingerRotation) ?? VfxCurve3.Const(Vector3.Zero)
                : null;
            VfxCurve3? scale = GetBool(p, F_useLingerScale)
                ? ReadCurve3(p, F_lingerScale) ?? VfxCurve3.Const(Vector3.One)
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
            VfxCurve4? sourceMixColor = ReadCurve4(palette.Properties, F_paletteSourceMixColor)
                ?? ReadCurve4(palette.Properties, F_palleteSourceMixColor);
            return new VfxPaletteDefinition(
                Math.Max(1, GetI32(palette.Properties, F_paletteCount) ?? 1),
                ReadCurve3(palette.Properties, F_paletteSelector) ?? VfxCurve3.Const(Vector3.Zero),
                ReadAsset(palette.Properties, F_paletteTexture, ".tex"),
                sourceMixColor?.Constant ?? new Vector4(0.299f, 0.587f, 0.114f, 0f),
                ReadCurveF(palette.Properties, F_paletteScrollU) ?? VfxCurveF.Zero,
                ReadCurveF(palette.Properties, F_paletteScrollV) ?? VfxCurveF.Zero,
                GetU8(palette.Properties, F_paletteAddressMode) ?? 1);
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
                foreach (BinTreeProperty item in identifiers.Elements)
                {
                    if (item is not BinTreeStruct identifier)
                    {
                        // LTK preserves unresolved child slots so childrenProbability keeps its authored indices.
                        children.Add(null);
                        continue;
                    }

                    string name = GetString(identifier.Properties, F_effectName)
                               ?? GetString(identifier.Properties, F_effectKey) ?? string.Empty;
                    uint systemHash = AsU32(Get(identifier.Properties, F_effect)) ?? 0u;
                    uint effectKey = AsU32(Get(identifier.Properties, F_effectKey))
                                  ?? (!string.IsNullOrEmpty(name) ? HashAlgorithms.Fnv1a(name) : 0u);
                    children.Add(!string.IsNullOrEmpty(name) || systemHash != 0 || effectKey != 0
                        ? new VfxChildSystemReference(name, systemHash, effectKey)
                        : null);
                }
            }

            IReadOnlyList<string> bones = Get(childData.Properties, F_boneToSpawnAt) is BinTreeContainer boneList
                ? boneList.Elements.OfType<BinTreeString>()
                    .Select(static value => value.Value)
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
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
                ReadCurveF(childData.Properties, F_childrenProbability) ?? VfxCurveF.Const(1f),
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

            var offset = ReadCurve3Property(Get(shape.Properties, F_emitOffset)) ?? VfxCurve3.Const(Vector3.Zero);
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
                if (pc.Elements[tableIndex] is not BinTreeStruct s) continue;

                // LTK treats the table itself as present even with no keys. In that case
                // singleValue (schema default 1) is the multiplier for the whole channel.
                float single = GetF32(s.Properties, F_singleValue) ?? 1f;
                var tc = Get(s.Properties, F_keyTimes) as BinTreeContainer;
                var vc = Get(s.Properties, F_keyValues) as BinTreeContainer;

                if (tc is not null && vc is not null && tc.Elements.Count > 0 && tc.Elements.Count != vc.Elements.Count)
                {
                    // The engine reads mismatched keyed tables as zero instead of truncating
                    // to the shorter list.
                    tables[tableIndex] = new VfxProbTable(null, null, 0f, IsPresent: true);
                    any = true;
                    continue;
                }

                int n = tc is not null && vc is not null ? Math.Min(tc.Elements.Count, vc.Elements.Count) : 0;
                float[] times = n > 0 ? new float[n] : null;
                float[] vals = n > 0 ? new float[n] : null;
                for (int i = 0; i < n; i++)
                {
                    times[i] = AsF32(tc.Elements[i]) ?? 0f;
                    vals[i] = AsF32(vc.Elements[i]) ?? 0f;
                }

                tables[tableIndex] = new VfxProbTable(times, vals, single, IsPresent: true);
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

        private static IReadOnlyList<uint> ReadSubmeshNameHashes(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<uint>();
            return value
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(token => token.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(HashAlgorithms.Fnv1a)
                .Distinct()
                .ToArray();
        }

        private static string ReadAsset(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash, string extension)
        {
            var prop = Get(p, hash);
            if (prop is BinTreeOptional opt)
                prop = opt.Value;

            return prop switch
            {
                BinTreeString text => string.IsNullOrWhiteSpace(text.Value) ? null : text.Value,
                BinTreeWadChunkLink link when link.Value != 0 => $"{link.Value:x16}{extension}",
                BinTreeU64 u64 when u64.Value != 0 => $"{u64.Value:x16}{extension}",
                BinTreeHash h when h.Value != 0 => $"{h.Value:x8}{extension}",
                BinTreeObjectLink ol when ol.Value != 0 => $"{ol.Value:x8}{extension}",
                BinTreeU32 u32 when u32.Value != 0 => $"{u32.Value:x8}{extension}",
                _ => null
            };
        }

        private static bool IsNoMeshPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            string fileName = Path.GetFileName(path.Replace('\\', '/'));
            return fileName.StartsWith("doesnotexist.", StringComparison.OrdinalIgnoreCase);
        }

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
            BinTreeString text => HashAlgorithms.Fnv1a(text.Value),
            _ => null
        };

        private static int? GetI16(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeI16 value ? value.Value : null;

        private static int? GetI32(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeI32 value ? value.Value : null;

    }
}
