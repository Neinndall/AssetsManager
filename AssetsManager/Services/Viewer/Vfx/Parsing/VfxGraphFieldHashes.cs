namespace AssetsManager.Services.Viewer.Vfx.Parsing
{
    public static partial class VfxGraphParser
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

        // emitter fields
        private static readonly uint F_emitterName   = HashAlgorithms.Fnv1a("emitterName");
        private static readonly uint F_rate          = HashAlgorithms.Fnv1a("rate");
        private static readonly uint F_particleLife  = HashAlgorithms.Fnv1a("particleLifetime");
        private static readonly uint F_lifetime      = HashAlgorithms.Fnv1a("lifetime");
        private static readonly uint F_particleLinger= HashAlgorithms.Fnv1a("particleLinger");
        private static readonly uint F_particleLingerType = HashAlgorithms.Fnv1a("particleLingerType");
        private static readonly uint F_emitterLinger = HashAlgorithms.Fnv1a("emitterLinger");
        private static readonly uint F_timeBefore    = HashAlgorithms.Fnv1a("timeBeforeFirstEmission");
        private static readonly uint F_isSingle      = HashAlgorithms.Fnv1a("isSingleParticle");
        private static readonly uint F_disabled      = HashAlgorithms.Fnv1a("disabled");
        private static readonly uint F_importance    = HashAlgorithms.Fnv1a("importance");
        private static readonly uint F_miscRenderFlags = HashAlgorithms.Fnv1a("miscRenderFlags");
        private static readonly uint F_meshRenderFlags = HashAlgorithms.Fnv1a("meshRenderFlags");
        private static readonly uint F_useNavmeshMask = HashAlgorithms.Fnv1a("useNavmeshMask");
        private static readonly uint F_depthBiasFactors = HashAlgorithms.Fnv1a("depthBiasFactors");
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
        private static readonly uint F_rateByVelocityFunction = HashAlgorithms.Fnv1a("rateByVelocityFunction");
        private static readonly uint F_paletteDefinition = HashAlgorithms.Fnv1a("paletteDefinition");
        private static readonly uint F_paletteCount = HashAlgorithms.Fnv1a("paletteCount");
        private static readonly uint F_paletteSelector = HashAlgorithms.Fnv1a("paletteSelector");
        private static readonly uint F_paletteTexture = HashAlgorithms.Fnv1a("paletteTexture");
        private static readonly uint F_paletteSourceMixColor = HashAlgorithms.Fnv1a("paletteSrcMixColor");
        private static readonly uint F_palleteSourceMixColor = HashAlgorithms.Fnv1a("palleteSrcMixColor");
        private static readonly uint F_audio = HashAlgorithms.Fnv1a("Audio");
        private static readonly uint F_soundOnCreate = HashAlgorithms.Fnv1a("SoundOnCreate");
        private static readonly uint F_hasPostRotateOrientation = HashAlgorithms.Fnv1a("hasPostRotateOrientation");
        private static readonly uint F_particlesShareRandomValue = HashAlgorithms.Fnv1a("ParticlesShareRandomValue");
        private static readonly uint F_falloffTexture = HashAlgorithms.Fnv1a("falloffTexture");
        private static readonly uint F_filtering = HashAlgorithms.Fnv1a("Filtering");
        private static readonly uint F_keywordsExcluded = HashAlgorithms.Fnv1a("keywordsExcluded");
        private const uint F_spawnShape              = 0x3bf0b4ed; // SpawnShape
        private static readonly uint F_emitOffset    = HashAlgorithms.Fnv1a("emitOffset");
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
        private static readonly uint F_textureMultRandomStart = HashAlgorithms.Fnv1a("isRandomStartFrameMult");
        private static readonly uint F_textureMultTransformCenter = HashAlgorithms.Fnv1a("uvTransformCenterMult");
        private static readonly uint F_textureMultClampUv = HashAlgorithms.Fnv1a("uvScrollClampMult");
        private static readonly uint F_textureMultEmitterUvScroll = HashAlgorithms.Fnv1a("emitterUvScrollRateMult");
        private static readonly uint F_textureMultScrollAlpha = HashAlgorithms.Fnv1a("uvScrollAlphaMult");
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
        private static readonly uint F_shape = HashAlgorithms.Fnv1a("shape");
        private static readonly uint F_distortionDefinition = HashAlgorithms.Fnv1a("distortionDefinition");
        private static readonly uint F_distortion = HashAlgorithms.Fnv1a("distortion");
        private static readonly uint F_distortionMode = HashAlgorithms.Fnv1a("distortionMode");
        private static readonly uint F_normalMapTexture = HashAlgorithms.Fnv1a("normalMapTexture");
        private static readonly uint F_alphaErosionDefinition = HashAlgorithms.Fnv1a("alphaErosionDefinition");
        private static readonly uint F_erosionMapName = HashAlgorithms.Fnv1a("erosionMapName");
        private static readonly uint F_erosionDriveCurve = HashAlgorithms.Fnv1a("erosionDriveCurve");
        private static readonly uint F_erosionFeatherIn = HashAlgorithms.Fnv1a("erosionFeatherIn");
        private static readonly uint F_erosionFeatherOut = HashAlgorithms.Fnv1a("erosionFeatherOut");
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
        private static readonly uint F_meshDef       = 0x0d89732d; // VfxPrimitiveMesh's VfxMeshDefinitionData field
        private static readonly uint F_simpleMesh    = HashAlgorithms.Fnv1a("mSimpleMeshName");
        private static readonly uint F_meshName      = HashAlgorithms.Fnv1a("mMeshName");
        private static readonly uint F_birthUvScroll = HashAlgorithms.Fnv1a("birthUvScrollRate");
        private static readonly uint F_meshSkeleton  = 0x90595a15; // VfxMeshDefinitionData skeleton field
        private static readonly uint F_meshAnim      = HashAlgorithms.Fnv1a("mAnimationName");
        private static readonly uint F_trailDefinition = HashAlgorithms.Fnv1a("mTrail");
        private static readonly uint F_trailBirthTilingSize = HashAlgorithms.Fnv1a("mBirthTilingSize");
        private static readonly uint F_trailSmoothingMode = HashAlgorithms.Fnv1a("mSmoothingMode");
        private static readonly uint F_trailMode = HashAlgorithms.Fnv1a("mMode");
        private static readonly uint F_trailMaxAddedPerFrame = HashAlgorithms.Fnv1a("mMaxAddedPerFrame");
        private static readonly uint F_trailCutoff = HashAlgorithms.Fnv1a("mCutoff");

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
        private static readonly uint ShapeBox = HashAlgorithms.Fnv1a("VfxShapeBox");
        private static readonly uint ShapeSphere = HashAlgorithms.Fnv1a("VfxShapeSphere");
        private static readonly uint ShapeCylinder = HashAlgorithms.Fnv1a("VfxShapeCylinder");

        private static readonly uint ResolverClass = HashAlgorithms.Fnv1a("ResourceResolver");
        private static readonly uint F_resourceMap = HashAlgorithms.Fnv1a("resourceMap");
        private static readonly uint F_mResourceMap = HashAlgorithms.Fnv1a("mResourceMap");

        /// <summary>Parses one physical BIN once and projects every VFX concern from the same tree.</summary>
    }
}
