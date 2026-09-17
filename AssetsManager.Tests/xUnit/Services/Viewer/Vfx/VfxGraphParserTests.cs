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

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
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
            Assert.False(parsed.TextureMultFlipV);
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
            Assert.Equal(0f, parsed.Rate.Constant);
            Assert.Equal(3f, parsed.ParticleLifetime.Constant);
            Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 0.4f), parsed.BirthColor.Constant);
            Assert.Equal(0.5f, parsed.UvTransformCenter.X);
            Assert.Equal(0.5f, parsed.TextureMultTransformCenter.Y);
            Assert.Equal(0.1f, parsed.AlphaErosion.FeatherIn);
            Assert.Equal(0.1f, parsed.AlphaErosion.FeatherOut);
            Assert.Equal(1, parsed.AlphaErosion.AddressMode);
        }

        [Fact]
        public void ProbabilityTablesHonorSingleValueAndMismatchedKeyListsLikeLtk()
        {
            var singleTable = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxProbabilityTableData"),
                new BinTreeProperty[]
                {
                    new BinTreeF32(Fnv1a.HashLower("singleValue"), 0.25f)
                });
            var mismatchedTable = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxProbabilityTableData"),
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("keyTimes"),
                        BinPropertyType.F32,
                        new BinTreeProperty[] { new BinTreeF32(0, 0f), new BinTreeF32(0, 1f) }),
                    new BinTreeContainer(
                        Fnv1a.HashLower("keyValues"),
                        BinPropertyType.F32,
                        new BinTreeProperty[] { new BinTreeF32(0, 2f) }),
                    new BinTreeF32(Fnv1a.HashLower("singleValue"), 0.75f)
                });
            var dynamics = new BinTreeStruct(
                Fnv1a.HashLower("dynamics"),
                Fnv1a.HashLower("VfxProbabilityTables"),
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("probabilityTables"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { singleTable, mismatchedTable })
                });
            var birthScale = new BinTreeStruct(
                Fnv1a.HashLower("birthScale0"),
                Fnv1a.HashLower("ValueVector3"),
                new BinTreeProperty[]
                {
                    new BinTreeVector3(Fnv1a.HashLower("constantValue"), new Vector3(2f, 3f, 4f)),
                    dynamics
                });
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[] { birthScale });
            var system = new BinTreeObject(
                "Effects/Probability",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);

            VfxEmitterDefinition parsed = Assert.Single(Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);
            Assert.NotNull(parsed.BirthScale.Prob);
            Assert.False(parsed.BirthScale.Prob[0].IsEmpty);
            Assert.Equal(0.25f, parsed.BirthScale.Prob[0].Single);
            Assert.False(parsed.BirthScale.Prob[1].IsEmpty);
            Assert.Equal(0f, parsed.BirthScale.Prob[1].Single);

            Vector3 drawn = parsed.BirthScale.SampleBirth(0f, new System.Random(1), sharedRoll: 0.5f);
            Assert.Equal(0.5f, drawn.X);
            Assert.Equal(0f, drawn.Y);
            Assert.Equal(4f, drawn.Z);
        }

        [Fact]
        public void MeshPrimitivePrefersSkinnedPairAndKeepsCameraAlignmentFlags()
        {
            var meshDefinition = new BinTreeStruct(
                0x0d89732d,
                Fnv1a.HashLower("VfxMeshDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("mSimpleMeshName"), "Effects/Simple.scb"),
                    new BinTreeString(Fnv1a.HashLower("mMeshName"), "Effects/Skinned.skn"),
                    new BinTreeString(0x90595a15, "Effects/Skinned.skl")
                });
            var primitive = new BinTreeStruct(
                Fnv1a.HashLower("primitive"),
                Fnv1a.HashLower("VfxPrimitiveMesh"),
                new BinTreeProperty[]
                {
                    meshDefinition,
                    new BinTreeBool(Fnv1a.HashLower("AlignPitchToCamera"), true),
                    new BinTreeBool(Fnv1a.HashLower("AlignYawToCamera"), true)
                });
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[] { primitive });
            var system = new BinTreeObject(
                "Effects/Mesh",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);

            VfxEmitterDefinition parsed = Assert.Single(Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);
            Assert.Equal("Effects/Skinned.skn", parsed.MeshPath);
            Assert.Equal("Effects/Skinned.skl", parsed.MeshSkeletonPath);
            Assert.True(parsed.MeshIsSkinned);
            Assert.True(parsed.MeshAlignPitchToCamera);
            Assert.True(parsed.MeshAlignYawToCamera);
        }

        [Fact]
        public void SimpleMeshUsesOnlyExtensionsAcceptedByLtk()
        {
            BinTreeStruct EmitterFor(string path) => new(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeStruct(
                        Fnv1a.HashLower("primitive"),
                        Fnv1a.HashLower("VfxPrimitiveMesh"),
                        new BinTreeProperty[]
                        {
                            new BinTreeStruct(
                                0x0d89732d,
                                Fnv1a.HashLower("VfxMeshDefinitionData"),
                                new BinTreeProperty[]
                                {
                                    new BinTreeString(Fnv1a.HashLower("mSimpleMeshName"), path)
                                })
                        })
                });

            var system = new BinTreeObject(
                "Effects/SimpleMeshExtensions",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[]
                        {
                            EmitterFor("Effects/One.scb"),
                            EmitterFor("Effects/Two.tmesh"),
                            EmitterFor("Effects/Three.GMESH"),
                            EmitterFor("Effects/Wrong.sco")
                        })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);

            IReadOnlyList<VfxEmitterDefinition> emitters = Assert.Single(
                VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters;

            Assert.Equal("Effects/One.scb", emitters[0].MeshPath);
            Assert.Equal("Effects/Two.tmesh", emitters[1].MeshPath);
            Assert.Equal("Effects/Three.GMESH", emitters[2].MeshPath);
            Assert.Null(emitters[3].MeshPath);
        }

        [Fact]
        public void BeamPreservesNamedMeshSoItsRibbonCanBeSuppressed()
        {
            var meshDefinition = new BinTreeStruct(
                0x0d89732d,
                Fnv1a.HashLower("VfxMeshDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("mSimpleMeshName"), "Effects/BeamMesh.scb")
                });
            var primitive = new BinTreeStruct(
                Fnv1a.HashLower("primitive"),
                Fnv1a.HashLower("VfxPrimitiveBeam"),
                new BinTreeProperty[] { meshDefinition });
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[] { primitive });
            var system = new BinTreeObject(
                "Effects/BeamMesh",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);

            VfxEmitterDefinition parsed = Assert.Single(
                Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);
            Assert.Equal(VfxPrimitiveKind.Beam, parsed.PrimitiveKind);
            Assert.NotNull(parsed.Beam);
            Assert.Equal("Effects/BeamMesh.scb", parsed.MeshPath);
            Assert.True(parsed.SuppressesBeamRibbon);
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
        public void InvalidEnumBytesFallBackLikeLtkAndPaletteCountKeepsAuthoredZero()
        {
            var legacy = new BinTreeStruct(
                Fnv1a.HashLower("LegacySimple"),
                Fnv1a.HashLower("VfxEmitterLegacySimple"),
                new BinTreeProperty[]
                {
                    new BinTreeU8(Fnv1a.HashLower("orientation"), 99)
                });
            var palette = new BinTreeStruct(
                Fnv1a.HashLower("paletteDefinition"),
                Fnv1a.HashLower("VfxPaletteDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeI32(Fnv1a.HashLower("paletteCount"), 0),
                    new BinTreeU8(Fnv1a.HashLower("PaletteTextureAddressMode"), 99)
                });
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeU8(Fnv1a.HashLower("blendMode"), 99),
                    new BinTreeU8(Fnv1a.HashLower("texAddressModeBase"), 99),
                    new BinTreeU8(Fnv1a.HashLower("stencilMode"), 99),
                    new BinTreeU8(Fnv1a.HashLower("stencilRef"), 7),
                    new BinTreeU8(Fnv1a.HashLower("particleLingerType"), 99),
                    new BinTreeU8(Fnv1a.HashLower("uvMode"), 99),
                    new BinTreeU8(Fnv1a.HashLower("colorLookUpTypeX"), 99),
                    new BinTreeU8(Fnv1a.HashLower("colorLookUpTypeY"), 99),
                    legacy,
                    palette
                });
            var system = new BinTreeObject(
                "Effects/InvalidEnums",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);

            VfxEmitterDefinition parsed = Assert.Single(
                Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);

            Assert.Equal(0, parsed.BlendMode);
            Assert.Equal(0, parsed.RenderState.TextureAddressMode);
            Assert.Equal((byte)0, parsed.RenderState.StencilMode);
            Assert.Equal((byte)0, parsed.RenderState.StencilReference);
            Assert.False(parsed.RenderState.HasStencil);
            Assert.Equal((byte)0, parsed.ParticleLingerType);
            Assert.Equal((byte)0, parsed.UvMode);
            Assert.Equal(VfxAuthoredDefaults.ColorLookUpTypeX, parsed.ColorLookUpTypeX);
            Assert.Equal(VfxAuthoredDefaults.ColorLookUpTypeY, parsed.ColorLookUpTypeY);
            Assert.Equal((byte)0, parsed.LegacyOrientation);
            Assert.Equal(0, parsed.PaletteDefinition.PaletteCount);
            Assert.Equal(1, parsed.PaletteDefinition.AddressMode);
        }

        [Fact]
        public void NumericFieldsAcceptAllBinIntegerLeafTypesLikeLtkNumber()
        {
            var palette = new BinTreeStruct(
                Fnv1a.HashLower("paletteDefinition"),
                Fnv1a.HashLower("VfxPaletteDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeU16(Fnv1a.HashLower("paletteCount"), 6)
                });
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeI32(Fnv1a.HashLower("rate"), 7),
                    new BinTreeU32(Fnv1a.HashLower("particleLifetime"), 4),
                    new BinTreeI16(Fnv1a.HashLower("texAddressModeBase"), 2),
                    palette
                });
            var system = new BinTreeObject(
                "Effects/NumericLeaves",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);

            VfxEmitterDefinition parsed = Assert.Single(
                Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);

            Assert.Equal(7f, parsed.Rate.Constant);
            Assert.Equal(4f, parsed.ParticleLifetime.Constant);
            Assert.Equal(2, parsed.RenderState.TextureAddressMode);
            Assert.Equal(6, parsed.PaletteDefinition.PaletteCount);
        }

        [Fact]
        public void TrailPrimitiveKeepsLtkDefaultsWhenTrailBlockIsMissing()
        {
            var primitive = new BinTreeStruct(
                Fnv1a.HashLower("primitive"),
                Fnv1a.HashLower("VfxPrimitiveCameraTrail"),
                System.Array.Empty<BinTreeProperty>());
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[] { primitive });
            var system = new BinTreeObject(
                "Effects/TrailDefaults",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);

            VfxEmitterDefinition parsed = Assert.Single(
                Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);

            Assert.NotNull(parsed.Trail);
            Assert.Equal(0, parsed.Trail.Mode);
            Assert.Equal(0, parsed.Trail.SmoothingMode);
            Assert.Equal(0, parsed.Trail.MaxAddedPerFrame);
            Assert.Equal(0f, parsed.Trail.Cutoff);
            Assert.Equal(Vector3.Zero, parsed.Trail.BirthTilingSize.Constant);
        }

        [Fact]
        public void FieldDefaultsMatchLtkMotionReader()
        {
            var acceleration = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxFieldAccelerationDefinitionData"),
                System.Array.Empty<BinTreeProperty>());
            var orbital = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxFieldOrbitalDefinitionData"),
                System.Array.Empty<BinTreeProperty>());
            var noise = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxFieldNoiseDefinitionData"),
                System.Array.Empty<BinTreeProperty>());
            var fields = new BinTreeStruct(
                Fnv1a.HashLower("fieldCollectionDefinition"),
                Fnv1a.HashLower("VfxFieldCollectionDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("fieldAccelerationDefinitions"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { acceleration }),
                    new BinTreeContainer(
                        Fnv1a.HashLower("fieldOrbitalDefinitions"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { orbital }),
                    new BinTreeContainer(
                        Fnv1a.HashLower("fieldNoiseDefinitions"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { noise })
                });
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[] { fields });
            var system = new BinTreeObject(
                "Effects/FieldDefaults",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);

            VfxEmitterDefinition parsed = Assert.Single(
                Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);

            Assert.NotNull(parsed.Fields);
            Assert.True(Assert.Single(parsed.Fields.Acceleration).LocalSpace);
            VfxOrbitalField parsedOrbital = Assert.Single(parsed.Fields.Orbital);
            Assert.True(parsedOrbital.LocalSpace);
            Assert.Equal(Vector3.UnitY, parsedOrbital.Direction.Constant);
            Assert.Equal(Vector3.Zero, Assert.Single(parsed.Fields.Noise).AxisFraction);
        }

        [Fact]
        public void EmptyFieldCollectionReadsAsNullLikeLtk()
        {
            var fields = new BinTreeStruct(
                Fnv1a.HashLower("fieldCollectionDefinition"),
                Fnv1a.HashLower("VfxFieldCollectionDefinitionData"),
                System.Array.Empty<BinTreeProperty>());
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[] { fields });
            var system = new BinTreeObject(
                "Effects/EmptyFields",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);

            VfxEmitterDefinition parsed = Assert.Single(
                Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);

            Assert.Null(parsed.Fields);
        }

        [Fact]
        public void SimpleEmitterListPreservesIdentityForLingerSemantics()
        {
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeF32(Fnv1a.HashLower("emitterLinger"), 30f),
                    new BinTreeF32(Fnv1a.HashLower("particleLinger"), 30f)
                });
            var system = new BinTreeObject(
                "Effects/SimpleIdentity",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("simpleEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);

            VfxEmitterDefinition parsed = Assert.Single(
                Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);

            Assert.True(parsed.IsSimpleEmitter);
            Assert.Equal(10f, AssetsManager.Services.Viewer.Vfx.Runtime.VfxPlaybackRuntime.StopWaitSeconds(parsed));
            Assert.Equal(10f, AssetsManager.Services.Viewer.Vfx.Runtime.VfxPlaybackRuntime.LingerSeconds(parsed));
        }

        [Fact]
        public void PresentCurveStructsUseEachFieldsLtkFallbackConstant()
        {
            static BinTreeStruct EmptyCurve(string field, string valueClass) => new(
                Fnv1a.HashLower(field),
                Fnv1a.HashLower(valueClass),
                System.Array.Empty<BinTreeProperty>());

            var erosion = new BinTreeStruct(
                Fnv1a.HashLower("alphaErosionDefinition"),
                Fnv1a.HashLower("VfxAlphaErosionDefinitionData"),
                new BinTreeProperty[]
                {
                    EmptyCurve("erosionDriveCurve", "ValueFloat"),
                    EmptyCurve("erosionMapChannelMixer", "ValueColor")
                });
            var palette = new BinTreeStruct(
                Fnv1a.HashLower("paletteDefinition"),
                Fnv1a.HashLower("VfxPaletteDefinitionData"),
                new BinTreeProperty[]
                {
                    EmptyCurve("palleteSrcMixColor", "ValueColor")
                });
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[]
                {
                    EmptyCurve("particleLifetime", "ValueFloat"),
                    EmptyCurve("birthScale0", "ValueVector3"),
                    EmptyCurve("scale0", "ValueVector3"),
                    EmptyCurve("uvScale", "ValueVector2"),
                    EmptyCurve("birthFrameRate", "ValueFloat"),
                    erosion,
                    palette
                });
            var system = new BinTreeObject(
                "Effects/CurveFallbacks",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);

            VfxEmitterDefinition parsed = Assert.Single(
                Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters);

            Assert.Equal(3f, parsed.ParticleLifetime.Constant);
            Assert.Equal(Vector3.One, parsed.BirthScale.Constant);
            Assert.Equal(Vector3.One, parsed.ScaleOverLife.Value.Constant);
            Assert.Equal(Vector2.One, parsed.UvScale.Value.Constant);
            Assert.Equal(1f, parsed.BirthFrameRate.Value.Constant);
            Assert.Equal(1f, parsed.AlphaErosion.Drive.Constant);
            Assert.Equal(new Vector4(0f, 0f, 0f, 1f), parsed.AlphaErosion.ChannelMixer.Value.Constant);
            Assert.Equal(new Vector4(0.299f, 0.587f, 0.114f, 0f), parsed.PaletteDefinition.PaletteSourceMixColor.Value);
        }

        [Fact]
        public void ChildSetKeepsLtkDefaultIndexAndBoneSlots()
        {
            static BinTreeStruct Child(string name) => new(
                0,
                Fnv1a.HashLower("VfxChildIdentifier"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("effectKey"), name)
                });

            var childSet = new BinTreeStruct(
                Fnv1a.HashLower("childParticleSetDefinition"),
                Fnv1a.HashLower("VfxChildParticleSetDefinitionData"),
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("childrenIdentifiers"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[]
                        {
                            Child("Effects/A"),
                            Child("Effects/B"),
                            Child("Effects/C")
                        }),
                    new BinTreeContainer(
                        Fnv1a.HashLower("boneToSpawnAt"),
                        BinPropertyType.String,
                        new BinTreeProperty[]
                        {
                            new BinTreeString(0, "Root"),
                            new BinTreeString(0, string.Empty),
                            new BinTreeString(0, "Hand")
                        })
                });
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[] { childSet });
            var system = new BinTreeObject(
                "Effects/ChildSlots",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);

            VfxChildParticleSetDefinition parsed = Assert.Single(
                Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters).ChildParticleSet;

            Assert.NotNull(parsed);
            Assert.Equal(0f, parsed.Probability.Constant);
            Assert.Equal(3, parsed.Children.Count);
            Assert.Equal(3, parsed.Bones.Count);
            Assert.Equal("Root", parsed.Bones[0]);
            Assert.Equal(string.Empty, parsed.Bones[1]);
            Assert.Equal("Hand", parsed.Bones[2]);
        }

        [Fact]
        public void LegacyShapeRotationAnglesKeepOneSlotPerElementLikeLtk()
        {
            var shape = new BinTreeStruct(
                Fnv1a.HashLower("SpawnShape"),
                Fnv1a.HashLower("VfxShapeLegacy"),
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("emitRotationAxes"),
                        BinPropertyType.Vector3,
                        new BinTreeProperty[]
                        {
                            new BinTreeVector3(0, Vector3.UnitX),
                            new BinTreeVector3(0, Vector3.UnitY)
                        }),
                    new BinTreeContainer(
                        Fnv1a.HashLower("emitRotationAngles"),
                        BinPropertyType.String,
                        new BinTreeProperty[]
                        {
                            new BinTreeString(0, "invalid"),
                            new BinTreeString(0, "invalid")
                        })
                });
            var emitter = new BinTreeStruct(
                0,
                Fnv1a.HashLower("VfxEmitterDefinitionData"),
                new BinTreeProperty[] { shape });
            var system = new BinTreeObject(
                "Effects/ShapeSlots",
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeContainer(
                        Fnv1a.HashLower("complexEmitterDefinitionData"),
                        BinPropertyType.Struct,
                        new BinTreeProperty[] { emitter })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { system }, System.Array.Empty<string>()).Write(stream);

            VfxSpawnShape parsed = Assert.Single(
                Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).Systems).Value.Emitters).SpawnShape;

            Assert.NotNull(parsed);
            Assert.Equal(VfxSpawnShapeKind.Legacy, parsed.Kind);
            Assert.Equal(2, parsed.RotationAxes.Count);
            Assert.Equal(2, parsed.RotationAngles.Count);
            Assert.All(parsed.RotationAngles, angle => Assert.Equal(0f, angle.Constant));
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
                    new BinTreeStruct(
                        Fnv1a.HashLower("LegacySimple"),
                        Fnv1a.HashLower("VfxEmitterLegacySimple"),
                        new BinTreeProperty[]
                        {
                            new BinTreeBitBool(Fnv1a.HashLower("hasFixedOrbit"), true),
                            new BinTreeU8(Fnv1a.HashLower("fixedOrbitType"), 5),
                            new BinTreeVector2(Fnv1a.HashLower("particleBind"), new Vector2(0.25f, 0.75f))
                        }),
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
            Assert.True(parsedEmitter.AuthoredFeatures.HasLegacySimple);
            Assert.True(parsedEmitter.LegacyHasFixedOrbit);
            Assert.Equal((byte)5, parsedEmitter.LegacyFixedOrbitType);
            Assert.Equal(new Vector2(0.25f, 0.75f), parsedEmitter.LegacyParticleBind);
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
                            new BinTreeF32(Fnv1a.HashLower("skinScale"), 1.25f),
                            new BinTreeString(Fnv1a.HashLower("initialSubmeshToHide"), "Cape Hair")
                        }),
                    new BinTreeStruct(
                        Fnv1a.HashLower("skinAnimationProperties"),
                        Fnv1a.HashLower("SkinAnimationProperties"),
                        new BinTreeProperty[]
                        {
                            new BinTreeObjectLink(Fnv1a.HashLower("animationGraphData"), 0x12345678u)
                        })
                });
            using var stream = new MemoryStream();
            new BinTree(new[] { owner, system }, System.Array.Empty<string>()).Write(stream);

            VfxBinDocument document = VfxGraphParser.ParseDocument(stream.ToArray());

            Assert.Equal("Characters/Test/Test.skn", document.OwnerSceneContext.MeshPath);
            Assert.Equal("Characters/Test/Test.skl", document.OwnerSceneContext.SkeletonPath);
            Assert.Equal(1.25f, document.OwnerSceneContext.SkinScale);
            Assert.Equal(0x12345678u, document.OwnerSceneContext.AnimationGraphPathHash);
            Assert.Equal(
                new[] { Fnv1a.HashLower("Cape"), Fnv1a.HashLower("Hair") },
                document.OwnerSceneContext.InitialHiddenSubmeshHashes);
            VfxEmitterDefinition parsed = Assert.Single(Assert.Single(document.Systems).Value.Emitters);
            Assert.Equal(new uint[] { 22, 33 }, parsed.SubmeshesToDraw);
            Assert.Equal(new uint[] { 11, 22 }, parsed.SubmeshesToDrawAlways);
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

            AnimationClipDefinition sequence = Assert.Single(document.EventSequences);
            VfxParticleEventDefinition parsed = Assert.Single(sequence.ParticleEvents);
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
        public void PreservesTypedVisualClipEventsAndUnknownEvents()
        {
            var visibility = new BinTreeStruct(
                0,
                0xbcf56e70,
                new BinTreeProperty[]
                {
                    new BinTreeF32(Fnv1a.HashLower("mStartFrame"), 2f),
                    new BinTreeF32(Fnv1a.HashLower("mEndFrame"), 6f),
                    new BinTreeContainer(
                        Fnv1a.HashLower("mShowSubmeshList"),
                        BinPropertyType.Hash,
                        new BinTreeProperty[] { new BinTreeHash(0, 11) }),
                    new BinTreeContainer(
                        Fnv1a.HashLower("mHideSubmeshList"),
                        BinPropertyType.Hash,
                        new BinTreeProperty[] { new BinTreeHash(0, 22) })
                });
            var snap = new BinTreeStruct(
                0,
                0xb5c1b6ad,
                new BinTreeProperty[]
                {
                    new BinTreeF32(Fnv1a.HashLower("mStartFrame"), 3f),
                    new BinTreeF32(Fnv1a.HashLower("mEndFrame"), 7f),
                    new BinTreeHash(Fnv1a.HashLower("mJointNameToOverride"), 33),
                    new BinTreeHash(Fnv1a.HashLower("mJointNameToSnapTo"), 44),
                    new BinTreeVector3(Fnv1a.HashLower("offset"), new Vector3(1f, 2f, 3f))
                });
            var conform = new BinTreeStruct(
                0,
                0x82377a1d,
                new BinTreeProperty[]
                {
                    new BinTreeF32(Fnv1a.HashLower("mStartFrame"), 4f),
                    new BinTreeHash(Fnv1a.HashLower("mMaskDataName"), 55),
                    new BinTreeF32(Fnv1a.HashLower("mBlendInTime"), 0.2f),
                    new BinTreeF32(Fnv1a.HashLower("mBlendOutTime"), 0.4f)
                });
            var unknown = new BinTreeStruct(
                0,
                0x12345678,
                new BinTreeProperty[]
                {
                    new BinTreeF32(Fnv1a.HashLower("mStartFrame"), 5f)
                });
            var eventMap = new BinTreeMap(
                Fnv1a.HashLower("mEventDataMap"),
                BinPropertyType.Hash,
                BinPropertyType.Struct,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 101), visibility),
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 102), snap),
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 103), conform),
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeHash(0, 104), unknown)
                });
            var clip = new BinTreeObject(
                "Animations/VisualEvents",
                "AtomicClipData",
                new BinTreeProperty[] { eventMap });
            using var stream = new MemoryStream();
            new BinTree(new[] { clip }, System.Array.Empty<string>()).Write(stream);

            AnimationClipDefinition parsed = Assert.Single(VfxGraphParser.ParseDocument(stream.ToArray()).EventSequences);

            var parsedVisibility = Assert.IsType<AnimationSubmeshVisibilityEventDefinition>(
                parsed.Events.Single(item => item.EventHash == 101));
            Assert.Equal(new uint[] { 11 }, parsedVisibility.ShowSubmeshHashes);
            Assert.Equal(new uint[] { 22 }, parsedVisibility.HideSubmeshHashes);
            Assert.Equal(6f, parsedVisibility.EndFrame);

            var parsedSnap = Assert.IsType<AnimationJointSnapEventDefinition>(
                parsed.Events.Single(item => item.EventHash == 102));
            Assert.Equal(33u, parsedSnap.JointHash);
            Assert.Equal(44u, parsedSnap.SnapToHash);
            Assert.Equal(new Vector3(1f, 2f, 3f), parsedSnap.Offset);

            var parsedConform = Assert.IsType<AnimationConformToPathEventDefinition>(
                parsed.Events.Single(item => item.EventHash == 103));
            Assert.Equal(55u, parsedConform.MaskHash);
            Assert.Equal(0.2f, parsedConform.BlendInSeconds);
            Assert.Equal(0.4f, parsedConform.BlendOutSeconds);

            var parsedUnknown = Assert.IsType<AnimationOtherClipEventDefinition>(
                parsed.Events.Single(item => item.EventHash == 104));
            Assert.Equal(0x12345678u, parsedUnknown.ClassHash);
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

            AnimationClipDefinition sequence = Assert.Single(
                VfxGraphParser.ParseDocument(stream.ToArray()).EventSequences);

            Assert.Equal(123u, sequence.OwnerPathHash);
            Assert.Equal(0.025f, sequence.TickDuration);
            Assert.Equal(4f, Assert.Single(sequence.Events).StartFrame);
        }

        [Fact]
        public void ParsesHashBasedAssetReferencesIntoExtensionPaths()
        {
            var meshStruct = new BinTreeStruct(
                Fnv1a.HashLower("SkinMeshDataProperties"),
                Fnv1a.HashLower("SkinMeshDataProperties"),
                new BinTreeProperty[]
                {
                    new BinTreeWadChunkLink(Fnv1a.HashLower("simpleSkin"), 0xa1b2c3d4e5f60718ul),
                    new BinTreeU64(Fnv1a.HashLower("skeleton"), 0x1122334455667788ul),
                });
            var skinMeshProperties = new BinTreeStruct(
                Fnv1a.HashLower("skinMeshProperties"),
                Fnv1a.HashLower("SkinMeshDataProperties"),
                new BinTreeProperty[]
                {
                    new BinTreeWadChunkLink(Fnv1a.HashLower("simpleSkin"), 0xa1b2c3d4e5f60718ul),
                    new BinTreeU64(Fnv1a.HashLower("skeleton"), 0x1122334455667788ul),
                });
            var skinObj = new BinTreeObject(
                "SkinData/TestSkin",
                "SkinCharacterDataProperties",
                new BinTreeProperty[]
                {
                    skinMeshProperties
                });

            var animResource = new BinTreeStruct(
                Fnv1a.HashLower("mAnimationResourceData"),
                Fnv1a.HashLower("AnimationResourceData"),
                new BinTreeProperty[]
                {
                    new BinTreeU64(Fnv1a.HashLower("mAnimationFilePath"), 0x9988776655443322ul)
                });
            var clip = new BinTreeStruct(
                Fnv1a.HashLower("AtomicClipData"),
                Fnv1a.HashLower("AtomicClipData"),
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("mClipName"), "Attack_01"),
                    animResource
                });
            var clipMap = new BinTreeMap(
                Fnv1a.HashLower("mClipDataMap"),
                BinPropertyType.String,
                BinPropertyType.Struct,
                new[]
                {
                    new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                        new BinTreeString(0, "Attack_01"),
                        clip)
                });
            var animObj = new BinTreeObject(
                "Animations/TestAnim",
                "AnimationGraphData",
                new BinTreeProperty[] { clipMap });

            using var stream = new MemoryStream();
            new BinTree(new[] { skinObj, animObj }, System.Array.Empty<string>()).Write(stream);

            VfxBinDocument doc = VfxGraphParser.ParseDocument(stream.ToArray());

            Assert.NotNull(doc.OwnerSceneContext);
            Assert.Equal("a1b2c3d4e5f60718.skn", doc.OwnerSceneContext.MeshPath);
            Assert.Equal("1122334455667788.skl", doc.OwnerSceneContext.SkeletonPath);

            var seq = Assert.Single(doc.EventSequences);
            Assert.Equal("9988776655443322.anm", seq.AnimationFilePath);
        }
    }
}
