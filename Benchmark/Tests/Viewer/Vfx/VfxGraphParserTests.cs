using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer.Vfx
{
    public sealed class VfxGraphParserTests
    {
        [Fact]
        public void ParsesEveryVfxProjectionFromOneBinDocument()
        {
            var effectObject = new BinTreeObject(
                "Effects/Test",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("particleName"), "TestEffect"),
                    new BinTreeString(Fnv1a.HashLower("particlePath"), "Effects/Test"),
                    new BinTreeMatrix44(
                        Fnv1a.HashLower("transform"),
                        Matrix4x4.CreateScale(0.5f)),
                });
            var tree = new BinTree(new[] { effectObject }, new[] { "data/effects/shared.bin" });
            using var stream = new MemoryStream();
            tree.Write(stream);
            byte[] bytes = stream.ToArray();

            VfxBinDocument document = VfxGraphParser.ParseDocument(bytes);

            var system = Assert.Single(document.Systems).Value;
            Assert.Equal("TestEffect", system.Name);
            Assert.Equal("Effects/Test", system.ParticlePath);
            Assert.Equal(Matrix4x4.CreateScale(0.5f), system.Transform);
            Assert.Empty(system.Emitters);
            Assert.Empty(document.ResourceMap);
            Assert.Equal("data/effects/shared.bin", Assert.Single(document.Dependencies));
        }

        [Fact]
        public void AppliesRiotEmitterDefaultsWhenOptionalBinFieldsAreAbsent()
        {
            uint textureMultHash = Fnv1a.HashLower("textureMult");
            var textureMult = new BinTreeStruct(
                textureMultHash,
                Fnv1a.HashLower("VfxTextureMultDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(textureMultHash, "Effects/TestMult.dds")
                });
            var alphaErosion = new BinTreeStruct(
                Fnv1a.HashLower("alphaErosionDefinition"),
                Fnv1a.HashLower("VfxAlphaErosionDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("erosionMapName"), "Effects/TestErosion.dds")
                });
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("emitterName"), "DefaultEmitter"),
                    new BinTreeU8(Fnv1a.HashLower("blendMode"), 2),
                    new BinTreeU8(Fnv1a.HashLower("importance"), 3),
                    new BinTreeU8(Fnv1a.HashLower("colorRenderFlags"), 1),
                    new BinTreeU8(Fnv1a.HashLower("miscRenderFlags"), 1),
                    new BinTreeU8(Fnv1a.HashLower("meshRenderFlags"), 2),
                    new BinTreeBool(Fnv1a.HashLower("useNavmeshMask"), true),
                    new BinTreeVector2(Fnv1a.HashLower("depthBiasFactors"), new Vector2(-1f, -200f)),
                    new BinTreeBool(Fnv1a.HashLower("isRotationEnabled"), false),
                    new BinTreeU8(Fnv1a.HashLower("uvMode"), 2),
                    new BinTreeF32(Fnv1a.HashLower("directionVelocityScale"), 0.002f),
                    new BinTreeVector4(
                        Fnv1a.HashLower("modulationFactor"),
                        new Vector4(0.25f, 0.5f, 0.75f, 0.8f)),
                    new BinTreeStruct(
                        Fnv1a.HashLower("FlexShapeDefinition"),
                        Fnv1a.HashLower("VfxFlexShapeDefinitionData"),
                        new BinTreeProperty[]
                        {
                            new BinTreeF32(Fnv1a.HashLower("scaleBirthScaleByBoundObjectSize"), 0.004f)
                        }),
                    new BinTreeStruct(
                        Fnv1a.HashLower("paletteDefinition"),
                        Fnv1a.HashLower("VfxPaletteDefinitionData"),
                        new BinTreeProperty[]
                        {
                            new BinTreeString(Fnv1a.HashLower("paletteTexture"), "Effects/TestPalette.dds"),
                            new BinTreeI32(Fnv1a.HashLower("paletteCount"), 16),
                            new BinTreeStruct(
                                Fnv1a.HashLower("paletteSelector"),
                                Fnv1a.HashLower("ValueVector3"),
                                new BinTreeProperty[]
                                {
                                    new BinTreeVector3(Fnv1a.HashLower("constantValue"), new Vector3(4f, 0f, 0f))
                                }),
                            new BinTreeStruct(
                                Fnv1a.HashLower("paletteSrcMixColor"),
                                Fnv1a.HashLower("ValueColor"),
                                new BinTreeProperty[]
                                {
                                    new BinTreeVector4(
                                        Fnv1a.HashLower("constantValue"),
                                        new Vector4(0f, 1f, 0f, 0f))
                                })
                        }),
                    new BinTreeVector4(Fnv1a.HashLower("birthColor"), new Vector4(0.1f, 0.2f, 0.3f, 0.4f)),
                    textureMult,
                    alphaErosion
                });
            var effectObject = new BinTreeObject(
                "Effects/Defaults",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("particleName"), "Defaults"),
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { effectObject }, System.Array.Empty<string>()).Write(stream);

            VfxBinDocument document = VfxGraphParser.ParseDocument(stream.ToArray());

            var parsed = Assert.Single(Assert.Single(document.Systems).Value.Emitters);
            Assert.Null(parsed.ParticleColorTexturePath);
            Assert.Equal(VfxAuthoredDefaults.ColorLookUpTypeX, parsed.ColorLookUpTypeX);
            Assert.Equal(0, parsed.ColorLookUpTypeY);
            Assert.True(parsed.TextureMultFlipV);
            Assert.True(parsed.TextureMultRandomStartFrame);
            Assert.Equal(3, parsed.Importance);
            Assert.Equal(2, parsed.BlendMode);
            Assert.Equal(1, parsed.ColorRenderFlags);
            Assert.Equal(1, parsed.MiscRenderFlags);
            Assert.Equal(2, parsed.MeshRenderFlags);
            Assert.True(parsed.UseNavmeshMask);
            Assert.Equal(new Vector2(-1f, -200f), parsed.DepthBiasFactors);
            Assert.False(parsed.IsRotationEnabled);
            Assert.Equal(2, parsed.UvMode);
            Assert.Equal(0.002f, parsed.DirectionVelocityScale);
            Assert.Equal(
                new Vector4(0.25f, 0.5f, 0.75f, 0.8f),
                parsed.ModulationFactor);
            Assert.Equal(0.004f, parsed.FlexShape.ScaleBirthScaleByBoundObjectSize);
            Assert.Equal(16, parsed.PaletteDefinition.PaletteCount);
            Assert.Equal(4f, parsed.PaletteDefinition.PaletteSelector.Constant.X);
            Assert.Equal("Effects/TestPalette.dds", parsed.PaletteDefinition.PaletteTexturePath);
            Assert.Equal(new Vector4(0f, 1f, 0f, 0f), parsed.PaletteDefinition.PaletteSourceMixColor);
            Assert.Equal(1f, parsed.BirthScale.Constant.X);
            Assert.Equal(1f, parsed.Rate.Constant);
            Assert.Equal(1f, parsed.ParticleLifetime.Constant);
            Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 0.4f), parsed.BirthColor.Constant);
            Assert.Equal(0.5f, parsed.UvTransformCenter.X);
            Assert.Equal(0.5f, parsed.TextureMultTransformCenter.Y);
            Assert.Equal(0.1f, parsed.AlphaErosion.FeatherIn);
            Assert.Equal(0.1f, parsed.AlphaErosion.FeatherOut);
            Assert.Equal(2, parsed.AlphaErosion.AddressMode);
        }

        [Fact]
        public void LeavesModulationFactorNeutralWhenBinOmitsIt()
        {
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("emitterName"), "NoModulation")
                });
            var system = new BinTreeObject(
                "Effects/NoModulation",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("particleName"), "NoModulation"),
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);

            VfxEmitterDefinition parsed = Assert.Single(
                Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);

            Assert.Null(parsed.ModulationFactor);
        }

        [Fact]
        public void UsesAuthoredSchemaDefaultsWhenEmitterFieldsAreOmitted()
        {
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("emitterName"), "SchemaDefaults")
                });
            var effectObject = new BinTreeObject(
                "Effects/SchemaDefaults",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("particleName"), "SchemaDefaults"),
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { effectObject }, System.Array.Empty<string>()).Write(stream);

            VfxEmitterDefinition parsed = Assert.Single(
                Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);

            Assert.Equal(VfxAuthoredDefaults.BlendMode, parsed.BlendMode);
            Assert.Equal(VfxAuthoredDefaults.AlphaReference, parsed.RenderState.AlphaReference);
            Assert.Equal(VfxAuthoredDefaults.ColorLookUpTypeX, parsed.ColorLookUpTypeX);
            Assert.Equal(VfxAuthoredDefaults.ColorLookUpTypeY, parsed.ColorLookUpTypeY);
            Assert.Equal(VfxAuthoredDefaults.MeshRenderFlags, parsed.MeshRenderFlags);
            Assert.Equal(VfxAuthoredDefaults.Importance, parsed.Importance);
            Assert.Equal(VfxAuthoredDefaults.RenderPhaseOverride, parsed.RenderState.RenderPhase);
            Assert.False(parsed.RenderState.HasStencil);
            Assert.False(parsed.RenderState.WriteAlphaOnly);
            Assert.False(parsed.RenderState.SortEmittersByPosition);
        }

        [Fact]
        public void PreservesAuthoredFeaturesNeededForCompatibilityAnalysis()
        {
            uint primitiveHash = Fnv1a.HashLower("VfxPrimitiveAttachedMesh");
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("emitterName"), "OwnerMesh"),
                    new BinTreeStruct(Fnv1a.HashLower("primitive"), primitiveHash, System.Array.Empty<BinTreeProperty>()),
                    new BinTreeStruct(Fnv1a.HashLower("CustomMaterial"), Fnv1a.HashLower("VfxCustomMaterial"), System.Array.Empty<BinTreeProperty>()),
                    new BinTreeU8(Fnv1a.HashLower("stencilMode"), 1),
                    new BinTreeU8(Fnv1a.HashLower("stencilRef"), 4),
                    new BinTreeHash(Fnv1a.HashLower("StencilReferenceId"), 0x12345678),
                    new BinTreeU8(Fnv1a.HashLower("renderPhaseOverride"), 3),
                    new BinTreeBitBool(Fnv1a.HashLower("WriteAlphaOnly"), true),
                    new BinTreeBitBool(Fnv1a.HashLower("SortEmittersByPos"), true),
                    new BinTreeString(Fnv1a.HashLower("emissionMeshName"), "Body"),
                    new BinTreeStruct(Fnv1a.HashLower("rotationOverride"), Fnv1a.HashLower("ValueVector3"), System.Array.Empty<BinTreeProperty>()),
                    new BinTreeF32(Fnv1a.HashLower("period"), 2f)
                });
            var materialOverride = new BinTreeEmbedded(
                0,
                Fnv1a.HashLower("VfxMaterialOverrideDefinitionData"),
                System.Array.Empty<BinTreeProperty>());
            var system = new BinTreeObject(
                "Effects/AuthoredFeatures",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("particleName"), "AuthoredFeatures"),
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter }),
                    new BinTreeContainer(
                        Fnv1a.HashLower("materialOverrideDefinitions"),
                        BinPropertyType.Embedded,
                        new BinTreeProperty[] { materialOverride })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);

            VfxSystemDefinition parsedSystem = Assert.Single(
                VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value;
            VfxEmitterDefinition parsedEmitter = Assert.Single(parsedSystem.Emitters);

            Assert.True(parsedSystem.AuthoredFeatures.HasMaterialOverrides);
            Assert.Equal(primitiveHash, parsedEmitter.AuthoredFeatures.PrimitiveClassHash);
            Assert.True(parsedEmitter.AuthoredFeatures.HasCustomMaterial);
            Assert.True(parsedEmitter.AuthoredFeatures.HasStencil);
            Assert.Equal((byte)3, parsedEmitter.RenderState.RenderPhase);
            Assert.Equal((byte)1, parsedEmitter.RenderState.StencilMode);
            Assert.Equal((byte)4, parsedEmitter.RenderState.StencilReference);
            Assert.Equal(0x12345678u, parsedEmitter.RenderState.StencilReferenceId);
            Assert.True(parsedEmitter.RenderState.WriteAlphaOnly);
            Assert.True(parsedEmitter.RenderState.SortEmittersByPosition);
            Assert.True(parsedEmitter.AuthoredFeatures.HasEmissionMesh);
            Assert.True(parsedEmitter.AuthoredFeatures.HasRotationOverride);
            Assert.True(parsedEmitter.AuthoredFeatures.HasPeriodControl);
        }

        [Fact]
        public void ExtractsOwnerSceneAndAttachedSubmeshContext()
        {
            var meshDefinition = new BinTreeStruct(
                0x0d89732d,
                Fnv1a.HashLower("VfxMeshDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("mSubmeshesToDrawAlways"),
                        BinPropertyType.Hash,
                        new BinTreeProperty[] { new BinTreeHash(0, 11), new BinTreeHash(0, 22) }),
                    new BinTreeContainer(
                        Fnv1a.HashLower("mSubmeshesToDraw"),
                        BinPropertyType.Hash,
                        new BinTreeProperty[] { new BinTreeHash(0, 22), new BinTreeHash(0, 33) })
                });
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("emitterName"), "OwnerMesh"),
                    new BinTreeStruct(
                        Fnv1a.HashLower("primitive"),
                        Fnv1a.HashLower("VfxPrimitiveAttachedMesh"),
                        new BinTreeProperty[] { meshDefinition })
                });
            var system = new BinTreeObject(
                "Effects/OwnerMesh",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("particleName"), "OwnerMesh"),
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            var owner = new BinTreeObject(
                "Characters/Test/Skins/0",
                "SkinCharacterDataProperties",
                new BinTreeProperty[]
                {
                    new BinTreeStruct(
                        Fnv1a.HashLower("skinMeshProperties"),
                        Fnv1a.HashLower("SkinMeshDataProperties"),
                        new BinTreeProperty[]
                        {
                            new BinTreeString(Fnv1a.HashLower("simpleSkin"), "Characters/Test/Test.skn"),
                            new BinTreeString(Fnv1a.HashLower("skeleton"), "Characters/Test/Test.skl"),
                            new BinTreeF32(Fnv1a.HashLower("skinScale"), 1.25f)
                        })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { owner, system }, System.Array.Empty<string>()).Write(stream);

            VfxBinDocument document = VfxGraphParser.ParseDocument(stream.ToArray());

            Assert.Equal("Characters/Test/Test.skn", document.OwnerSceneContext.MeshPath);
            Assert.Equal("Characters/Test/Test.skl", document.OwnerSceneContext.SkeletonPath);
            Assert.Equal(1.25f, document.OwnerSceneContext.SkinScale);
            VfxEmitterDefinition parsed = Assert.Single(Assert.Single(document.Systems).Value.Emitters);
            Assert.Equal(new uint[] { 11, 22, 33 }, parsed.AttachedSubmeshHashes);
        }

        [Fact]
        public void ExtractsAnimationParticleEventsAndBoneAttachments()
        {
            var attachment = new BinTreeEmbedded(
                0,
                Fnv1a.HashLower("ParticleEventDataPair"),
                new BinTreeProperty[]
                {
                    new BinTreeHash(Fnv1a.HashLower("mBoneName"), 11),
                    new BinTreeHash(Fnv1a.HashLower("mTargetBoneName"), 22)
                });
            var particleEvent = new BinTreeStruct(
                0,
                Fnv1a.HashLower("ParticleEventData"),
                new BinTreeProperty[]
                {
                    new BinTreeF32(Fnv1a.HashLower("mStartFrame"), 3f),
                    new BinTreeF32(Fnv1a.HashLower("mEndFrame"), 9f),
                    new BinTreeHash(Fnv1a.HashLower("mEffectKey"), 33),
                    new BinTreeHash(Fnv1a.HashLower("mEnemyEffectKey"), 44),
                    new BinTreeString(Fnv1a.HashLower("mEffectName"), "Effects/Test"),
                    new BinTreeBool(Fnv1a.HashLower("mIsLoop"), false),
                    new BinTreeF32(Fnv1a.HashLower("scale"), 1.5f),
                    new BinTreeContainer(
                        Fnv1a.HashLower("mParticleEventDataPairList"),
                        BinPropertyType.Embedded,
                        new BinTreeProperty[] { attachment })
                });
            uint eventMapHash = Fnv1a.HashLower("mEventDataMap");
            var eventMap = new BinTreeMap(
                eventMapHash,
                BinPropertyType.Hash,
                BinPropertyType.Struct,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeHash(0, 55),
                        particleEvent)
                });
            var clip = new BinTreeObject(
                "Animations/TestClip",
                "SequencerClipData",
                new BinTreeProperty[] { eventMap });
            using var stream = new MemoryStream();
            new BinTree(new[] { clip }, System.Array.Empty<string>()).Write(stream);

            VfxBinDocument document = VfxGraphParser.ParseDocument(stream.ToArray());

            VfxEventSequenceDefinition sequence = Assert.Single(document.EventSequences);
            VfxParticleEventDefinition parsed = Assert.Single(sequence.Events);
            Assert.Equal(55u, parsed.EventHash);
            Assert.Equal(3f, parsed.StartFrame);
            Assert.Equal(9f, parsed.EndFrame);
            Assert.Equal(33u, parsed.EffectKey);
            Assert.Equal(44u, parsed.EnemyEffectKey);
            Assert.Equal("Effects/Test", parsed.EffectName);
            Assert.False(parsed.IsLoop);
            Assert.Equal(1.5f, parsed.Scale);
            VfxParticleEventAttachment parsedAttachment = Assert.Single(parsed.Attachments);
            Assert.Equal(11u, parsedAttachment.SourceBoneHash);
            Assert.Equal(22u, parsedAttachment.TargetBoneHash);
        }

        [Fact]
        public void ExtractsParticleEventsFromAnimationGraphClipMap()
        {
            var particleEvent = new BinTreeStruct(
                0,
                Fnv1a.HashLower("ParticleEventData"),
                new BinTreeProperty[]
                {
                    new BinTreeF32(Fnv1a.HashLower("mStartFrame"), 4f),
                    new BinTreeHash(Fnv1a.HashLower("mEffectKey"), 77)
                });
            var eventMap = new BinTreeMap(
                Fnv1a.HashLower("mEventDataMap"),
                BinPropertyType.Hash,
                BinPropertyType.Struct,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeHash(0, 88),
                        particleEvent)
                });
            var clip = new BinTreeStruct(
                0,
                Fnv1a.HashLower("AtomicClipData"),
                new BinTreeProperty[]
                {
                    new BinTreeF32(Fnv1a.HashLower("mTickDuration"), 0.025f),
                    eventMap
                });
            var clipMap = new BinTreeMap(
                Fnv1a.HashLower("mClipDataMap"),
                BinPropertyType.Hash,
                BinPropertyType.Struct,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeHash(0, 123),
                        clip)
                });
            var graph = new BinTreeObject(
                "Animations/TestGraph",
                "AnimationGraphData",
                new BinTreeProperty[] { clipMap });
            using var stream = new MemoryStream();
            new BinTree(new[] { graph }, System.Array.Empty<string>()).Write(stream);

            VfxEventSequenceDefinition sequence = Assert.Single(
                VfxGraphParser.ParseDocument(stream.ToArray()).EventSequences);

            Assert.Equal(123u, sequence.OwnerPathHash);
            Assert.Equal(0.025f, sequence.TickDuration);
            Assert.Equal(4f, Assert.Single(sequence.Events).StartFrame);
        }
    }
}
