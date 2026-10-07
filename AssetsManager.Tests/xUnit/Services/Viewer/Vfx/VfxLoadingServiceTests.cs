using AssetsManager.Services.Viewer.Resources;
using AssetsManager.Services.Viewer.Loading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CommunityToolkit.HighPerformance.Buffers;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Serilog;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxLoadingServiceTests
    {
        [Fact]
        public void ResourceIndexPreservesDataAndAssetsNamespaces()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxNamespaces", Guid.NewGuid().ToString("N"));
            string data = Path.Combine(root, "data", "shared");
            string assets = Path.Combine(root, "assets", "shared");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(assets);
            try
            {
                string dataPath = Path.Combine(data, "effect.bin");
                string assetPath = Path.Combine(assets, "effect.bin");
                File.WriteAllBytes(dataPath, Array.Empty<byte>());
                File.WriteAllBytes(assetPath, Array.Empty<byte>());
                var index = ProjectResourceIndex.Build(root);
                Assert.Equal(dataPath, Assert.Single(index.ResolveLinkedAll("DATA/Shared/Effect.bin", new[] { ".bin" })));
                Assert.Equal(assetPath, Assert.Single(index.ResolveLinkedAll("ASSETS/Shared/Effect.bin", new[] { ".bin" })));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Theory]
        [InlineData(".tex", false)]
        [InlineData(".skn", false)]
        [InlineData(".anm", false)]
        [InlineData(".tex", true)]
        [InlineData(".skn", true)]
        public void TruncatedBinDependencyDoesNotResolveOtherResourceTypes(string extension, bool collisionName)
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxDependencyTypes", Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "data", "characters", "hero");
            Directory.CreateDirectory(directory);
            try
            {
                string stem = collisionName ? "shared" : new string('c', 236);
                string extractedName = stem + (collisionName ? " (1)" : string.Empty) + extension;
                File.WriteAllBytes(Path.Combine(directory, extractedName), Array.Empty<byte>());
                var index = ProjectResourceIndex.Build(root);
                string dependency = $"data/characters/hero/{stem}_skins_skin28.bin";

                Assert.Empty(index.ResolveLinkedAll(dependency, new[] { ".bin" }));
                Assert.Null(index.Resolve(dependency, new[] { ".bin" }));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void NamedAssetDoesNotBorrowSameBasenameFromAnotherDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxStrictAssets", Guid.NewGuid().ToString("N"));
            string existingDirectory = Path.Combine(root, "assets", "characters", "hero", "skins", "skin0", "particles");
            Directory.CreateDirectory(existingDirectory);
            try
            {
                File.WriteAllBytes(Path.Combine(existingDirectory, "shared.tex"), new byte[] { 1 });
                var index = ProjectResourceIndex.Build(root);

                string resolved = index.Resolve(
                    "assets/characters/hero/skins/skin1/particles/shared.tex",
                    new[] { ".tex" });

                Assert.Null(resolved);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void BundleKeepsPrimaryResolverScopeOnLinkedDocuments()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxResolverScope", Guid.NewGuid().ToString("N"));
            string championDirectory = Path.Combine(root, "data", "characters", "hero");
            string skinsDirectory = Path.Combine(championDirectory, "skins");
            Directory.CreateDirectory(skinsDirectory);
            try
            {
                const uint key = 0x12345678;
                const uint unrelatedKey = 0x87654321;
                const string primarySystemPath = "Effects/Primary";
                const string linkedSystemPath = "Effects/Linked";
                const string dependency = "data/characters/hero/particles.bin";
                string skin = Path.Combine(skinsDirectory, "skin0.bin");
                string linked = Path.Combine(championDirectory, "particles.bin");
                uint primaryHash = Fnv1a.HashLower(primarySystemPath);
                uint linkedHash = Fnv1a.HashLower(linkedSystemPath);

                WriteBin(
                    skin,
                    new[]
                    {
                        CreateSystem(primarySystemPath, "Primary"),
                        CreateResolver("Resolvers/Primary", key, primaryHash),
                        CreateResolver("Resolvers/Other", unrelatedKey, linkedHash),
                        CreateSkin("SkinData", Fnv1a.HashLower("Resolvers/Primary"))
                    },
                    new[] { dependency });
                WriteBin(
                    linked,
                    new[]
                    {
                        CreateSystem(linkedSystemPath, "Linked"),
                        CreateResolver("Resolvers/Linked", key, linkedHash)
                    },
                    Array.Empty<string>());

                using var service = new VfxLoadingService();
                VfxLoadingService.Bundle bundle = service.Load(skin, null);

                Assert.Equal(primaryHash, bundle.ResourceMap[key]);
                Assert.False(bundle.ResourceMap.ContainsKey(unrelatedKey));
                Assert.Equal(primaryHash, bundle.Systems[primaryHash].ResourceMap[key]);
                Assert.Equal(linkedHash, bundle.Systems[primaryHash].ResourceMap[unrelatedKey]);
                Assert.Equal(linkedHash, bundle.Systems[linkedHash].ResourceMap[key]);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void LinkedSpellDeclarationsClassifyTheSkinsEffectWithoutAHashCatalog()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxRig", Guid.NewGuid().ToString("N"));
            string character = Path.Combine(root, "data", "characters", "hero");
            string skins = Path.Combine(character, "skins");
            Directory.CreateDirectory(skins);
            try
            {
                const uint key = 77;
                const string effectPath = "Effects/Return";
                uint systemHash = Fnv1a.HashLower(effectPath);
                string primary = Path.Combine(skins, "skin0.bin");
                WriteBin(primary, new[]
                {
                    CreateSystem(effectPath, "Return"),
                    CreateResolver("Resolvers/Skin", key, systemHash),
                    CreateSkin("Skin", Fnv1a.HashLower("Resolvers/Skin"))
                }, new[] { "data/characters/hero/hero.bin" });
                WriteBin(Path.Combine(character, "hero.bin"), new[]
                {
                    new BinTreeObject("Spells/Return", "SpellObject", new BinTreeProperty[]
                    {
                        new BinTreeStruct(Fnv1a.HashLower("mSpell"), Fnv1a.HashLower("SpellDataResource"),
                            new BinTreeProperty[] { new BinTreeHash(Fnv1a.HashLower("mMissileEffectKey"), key) })
                    })
                }, Array.Empty<string>());
                using var service = new VfxLoadingService();
                var bundle = service.Load(primary, null);
                Assert.Single(bundle.SpellPreviews);
                Assert.Equal(AssetsManager.Services.Viewer.Vfx.Session.VfxRigPreset.Missile,
                    VfxSystemRigResolver.Resolve(bundle.Systems[systemHash], bundle).Preset);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void LinkedBinTraversalStopsAtTheSharedCapPastTheShippedMaximum()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxLinkedCap", Guid.NewGuid().ToString("N"));
            string championDirectory = Path.Combine(root, "data", "characters", "hero");
            string skinsDirectory = Path.Combine(championDirectory, "skins");
            Directory.CreateDirectory(skinsDirectory);
            try
            {
                string skin = Path.Combine(skinsDirectory, "skin0.bin");
                var dependencies = new List<string>();
                // Shipped skins reach up to 40 linked files (Evelynn, Thresh); the cap stops one past it.
                int cap = AssetsManager.Services.Viewer.Loading.BinDocumentClosureLoader.MaximumLinkedBins;
                Assert.True(cap >= 40);
                for (int index = 0; index <= cap; index++)
                {
                    string dependency = $"data/characters/hero/linked{index:D2}.bin";
                    dependencies.Add(dependency);
                    WriteBin(
                        Path.Combine(championDirectory, $"linked{index:D2}.bin"),
                        new[] { CreateSystem($"Effects/Linked{index:D2}", $"Linked{index:D2}") },
                        Array.Empty<string>());
                }
                WriteBin(skin, Array.Empty<BinTreeObject>(), dependencies.ToArray());

                using var service = new VfxLoadingService();
                VfxLoadingService.Bundle bundle = service.Load(skin, null);

                Assert.Equal(cap + 1, bundle.LoadedBins.Count); // primary + cap linked documents
                Assert.Equal(cap, bundle.Systems.Count);
                Assert.True(bundle.Systems.ContainsKey(Fnv1a.HashLower($"Effects/Linked{cap - 1:D2}")));
                Assert.False(bundle.Systems.ContainsKey(Fnv1a.HashLower($"Effects/Linked{cap:D2}")));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void LoadsDependencyStoredUnderItsWadHash()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxHashed", Guid.NewGuid().ToString("N"));
            string skins = Path.Combine(root, "data", "characters", "hero", "skins");
            Directory.CreateDirectory(skins);
            try
            {
                const string dependency = "data/characters/hero/shared.bin";
                string hashedPath = Path.Combine(root, $"{XxHash64Ext.Hash(dependency):x16}.bin");
                string skin = Path.Combine(skins, "skin0.bin");
                WriteBin(hashedPath, new[] { CreateSystem("Effects/Hashed", "Hashed") }, Array.Empty<string>());
                WriteBin(skin, Array.Empty<BinTreeObject>(), new[] { dependency.ToUpperInvariant() });
                using var service = new VfxLoadingService();
                var bundle = service.Load(skin, null);
                Assert.Equal("Hashed", Assert.Single(bundle.Systems).Value.Name);
                Assert.Empty(bundle.MissingDependencies);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void FollowsDeclaredTruncatedDependencyWithoutLoadingSimilarSkinNames()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxLoading", Guid.NewGuid().ToString("N"));
            string championDirectory = Path.Combine(root, "data", "characters", "hero");
            string skinsDirectory = Path.Combine(championDirectory, "skins");
            Directory.CreateDirectory(skinsDirectory);

            string extractedStem = new string('a', 236);
            string sharedBin = Path.Combine(championDirectory, extractedStem + ".bin");
            string skin1Bin = Path.Combine(skinsDirectory, "skin1.bin");
            string skin11Bin = Path.Combine(skinsDirectory, "skin11.bin");

            WriteBin(sharedBin, new[] { CreateSystem("Effects/Shared", "Shared") }, Array.Empty<string>());
            WriteBin(skin1Bin, Array.Empty<BinTreeObject>(), new[]
            {
                $"DATA/Characters/Hero/{extractedStem}_skins_skin28.bin"
            });
            WriteBin(skin11Bin, new[] { CreateSystem("Effects/WrongSkin", "WrongSkin") }, Array.Empty<string>());

            try
            {
                using var logger = new LoggerConfiguration().CreateLogger();
                var service = new VfxLoadingService();
                VfxLoadingService.Bundle bundle = service.Load(skin1Bin, new LogService(logger));

                VfxSystemDefinition system = Assert.Single(bundle.Systems).Value;
                Assert.Equal("Shared", system.Name);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void LoadsInheritedSkinSystemsFromDeclaredDependency()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxInheritedSkin", Guid.NewGuid().ToString("N"));
            string championDirectory = Path.Combine(root, "data", "characters", "hero");
            string skinsDirectory = Path.Combine(championDirectory, "skins");
            Directory.CreateDirectory(skinsDirectory);

            string sharedBin = Path.Combine(championDirectory, "hero_skin1_multi_skins.bin");
            string skin2Bin = Path.Combine(skinsDirectory, "skin2.bin");
            WriteBin(
                sharedBin,
                new[] { CreateSystem("Characters/Hero/Skins/Skin1/Particles/Inherited", "Inherited") },
                Array.Empty<string>());
            WriteBin(
                skin2Bin,
                Array.Empty<BinTreeObject>(),
                new[] { "DATA/Characters/Hero/hero_skin1_multi_skins.bin" });

            try
            {
                using var logger = new LoggerConfiguration().CreateLogger();
                var service = new VfxLoadingService();
                VfxLoadingService.Bundle bundle = service.Load(skin2Bin, new LogService(logger));

                VfxSystemDefinition system = Assert.Single(bundle.Systems).Value;
                Assert.Equal("Inherited", system.Name);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void LoadsEveryCollisionSiblingForOneTruncatedDependency()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxCollisions", Guid.NewGuid().ToString("N"));
            string championDirectory = Path.Combine(root, "data", "characters", "hero");
            string skinsDirectory = Path.Combine(championDirectory, "skins");
            Directory.CreateDirectory(skinsDirectory);

            string extractedStem = new string('b', 236);
            string firstBin = Path.Combine(championDirectory, extractedStem + ".bin");
            string secondBin = Path.Combine(championDirectory, extractedStem + " (1).bin");
            string skinBin = Path.Combine(skinsDirectory, "skin28.bin");
            WriteBin(firstBin, new[] { CreateSystem("Effects/First", "First") }, Array.Empty<string>());
            WriteBin(secondBin, new[] { CreateSystem("Effects/Second", "Second") }, Array.Empty<string>());
            WriteBin(
                skinBin,
                Array.Empty<BinTreeObject>(),
                new[] { $"DATA/Characters/Hero/{extractedStem}_irreversibly_truncated.bin" });

            try
            {
                using var logger = new LoggerConfiguration().CreateLogger();
                var service = new VfxLoadingService();
                VfxLoadingService.Bundle bundle = service.Load(skinBin, new LogService(logger));

                Assert.Equal(2, bundle.Systems.Count);
                Assert.Contains(bundle.Systems.Values, system => system.Name == "First");
                Assert.Contains(bundle.Systems.Values, system => system.Name == "Second");
                Assert.Single(bundle.AmbiguousDependencies);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void DoesNotResolveLinkedBinByBasenameOutsideDeclaredPath()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxStrictLinks", Guid.NewGuid().ToString("N"));
            string championDirectory = Path.Combine(root, "data", "characters", "hero");
            string unrelatedDirectory = Path.Combine(root, "data", "characters", "other");
            string skinsDirectory = Path.Combine(championDirectory, "skins");
            Directory.CreateDirectory(skinsDirectory);
            Directory.CreateDirectory(unrelatedDirectory);

            string skinBin = Path.Combine(skinsDirectory, "skin0.bin");
            string unrelatedBin = Path.Combine(unrelatedDirectory, "missing.bin");
            WriteBin(
                skinBin,
                Array.Empty<BinTreeObject>(),
                new[] { "DATA/Characters/Hero/missing.bin" });
            WriteBin(
                unrelatedBin,
                new[] { CreateSystem("Effects/Unrelated", "Unrelated") },
                Array.Empty<string>());

            try
            {
                using var logger = new LoggerConfiguration().CreateLogger();
                var service = new VfxLoadingService();
                VfxLoadingService.Bundle bundle = service.Load(skinBin, new LogService(logger));

                Assert.Empty(bundle.Systems);
                Assert.Contains("DATA/Characters/Hero/missing.bin", bundle.MissingDependencies);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void DoesNotScanUnlinkedSiblingBinsWhenRootHasNoDependencies()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxNoLegacyScan", Guid.NewGuid().ToString("N"));
            string championDirectory = Path.Combine(root, "data", "characters", "hero");
            string skinsDirectory = Path.Combine(championDirectory, "skins");
            string animationsDirectory = Path.Combine(championDirectory, "animations");
            Directory.CreateDirectory(skinsDirectory);
            Directory.CreateDirectory(animationsDirectory);

            string skinBin = Path.Combine(skinsDirectory, "skin0.bin");
            WriteBin(skinBin, Array.Empty<BinTreeObject>(), Array.Empty<string>());
            WriteBin(
                Path.Combine(championDirectory, "hero.bin"),
                new[] { CreateSystem("Effects/UnlinkedChampion", "UnlinkedChampion") },
                Array.Empty<string>());
            WriteBin(
                Path.Combine(animationsDirectory, "skin0.bin"),
                new[] { CreateSystem("Effects/UnlinkedAnimation", "UnlinkedAnimation") },
                Array.Empty<string>());

            try
            {
                using var logger = new LoggerConfiguration().CreateLogger();
                var service = new VfxLoadingService();
                VfxLoadingService.Bundle bundle = service.Load(skinBin, new LogService(logger));

                Assert.Empty(bundle.Systems);
                Assert.Empty(bundle.MissingDependencies);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void MeshAvailabilityUsesSimpleFallbackAndRestoresMissingBeamRibbon()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxMeshAvailability", Guid.NewGuid().ToString("N"));
            string searchDirectory = Path.Combine(root, "data", "characters", "hero", "skins");
            string assetDirectory = Path.Combine(root, "assets", "effects");
            Directory.CreateDirectory(searchDirectory);
            Directory.CreateDirectory(assetDirectory);
            File.WriteAllBytes(Path.Combine(assetDirectory, "fallback.scb"), Array.Empty<byte>());

            try
            {
                VfxEmitterDefinition mesh = CreateEmitter(VfxPrimitiveKind.Mesh) with
                {
                    IsMeshPrimitive = true,
                    MeshPath = "assets/effects/missing.skn",
                    MeshSkeletonPath = "assets/effects/missing.skl",
                    MeshIsSkinned = true,
                    MeshFallbackPath = "assets/effects/fallback.scb"
                };
                VfxEmitterDefinition beam = CreateEmitter(VfxPrimitiveKind.Beam) with
                {
                    IsMeshPrimitive = false,
                    MeshPath = "assets/effects/missing.scb",
                    Beam = new VfxBeamDefinition(
                        0,
                        0,
                        0,
                        VfxCurve3.Const(Vector3.Zero),
                        VfxCurve4.Const(Vector4.One),
                        false,
                        Vector3.Zero,
                        Vector3.Zero)
                };
                var definition = new VfxSystemDefinition(1, "availability", "availability", new[] { mesh, beam });

                using var service = new VfxLoadingService();
                VfxSystemDefinition resolved = service.ResolveMeshAvailability(definition, searchDirectory);

                Assert.Equal("assets/effects/fallback.scb", resolved.Emitters[0].MeshPath);
                Assert.False(resolved.Emitters[0].MeshIsSkinned);
                Assert.Null(resolved.Emitters[0].MeshSkeletonPath);
                Assert.Null(resolved.Emitters[1].MeshPath);
                Assert.False(resolved.Emitters[1].SuppressesBeamRibbon);
                Assert.True(resolved.Emitters[1].DrawsAsBeam);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ResourceIndexLocatesLtkSimpleMeshExtensions()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxMeshExtensions", Guid.NewGuid().ToString("N"));
            string searchDirectory = Path.Combine(root, "data", "characters", "hero", "skins");
            string assetDirectory = Path.Combine(root, "assets", "effects");
            Directory.CreateDirectory(searchDirectory);
            Directory.CreateDirectory(assetDirectory);
            string tmesh = Path.Combine(assetDirectory, "first.tmesh");
            string gmesh = Path.Combine(assetDirectory, "second.gmesh");
            File.WriteAllBytes(tmesh, Array.Empty<byte>());
            File.WriteAllBytes(gmesh, Array.Empty<byte>());

            try
            {
                using var service = new VfxLoadingService();
                Assert.Equal(tmesh, service.ResolveAssetPath("assets/effects/first.tmesh", searchDirectory, ".tmesh"));
                Assert.Equal(gmesh, service.ResolveAssetPath("assets/effects/second.gmesh", searchDirectory, ".gmesh"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Theory]
        [InlineData("unsupported.gmesh", "GMSH")]
        [InlineData("unsupported.tmesh", "TMSH")]
        public void UnsupportedSimpleMeshIsResolvedThenFailsLocallyLikeLtk(string fileName, string magic)
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxUnsupportedMesh", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string meshPath = Path.Combine(root, fileName);
            byte[] marker = System.Text.Encoding.ASCII.GetBytes(magic);
            File.WriteAllBytes(meshPath, marker.Concat(new byte[] { 1, 0, 0, 0 }).ToArray());

            try
            {
                using var resolver = new VfxResourceResolver();

                // The BIN accepts TMESH/GMESH names even though the current decoder, like LTK's,
                // cannot turn either format into geometry. Resolution must still reach the asset.
                Assert.Equal(meshPath, resolver.ResolvePath(fileName, root, VfxMeshFormatSemantics.ResolverExtensions));
                Assert.Null(resolver.ResolveMesh(fileName, root));
                // The failed decode is cached just like an unresolved asset, so repeated draws
                // cannot repeatedly throw or reparse the same unsupported resource.
                Assert.Null(resolver.ResolveMesh(fileName, root));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void CorruptTextureFailsLocallyLikeLtk()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxCorruptTexture", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string texturePath = Path.Combine(root, "broken.dds");
            File.WriteAllBytes(texturePath, new byte[] { 1, 2, 3, 4, 5, 6 });

            try
            {
                using var resolver = new VfxResourceResolver();

                Assert.Null(resolver.ResolveTexture("broken.dds", root));
                Assert.Null(resolver.ResolveTexture("broken.dds", root));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void PngIsNotAVfxTextureSamplerInLtk()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxPngSampler", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string texturePath = Path.Combine(root, "valid.png");
            File.WriteAllBytes(
                texturePath,
                Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

            try
            {
                using var resolver = new VfxResourceResolver();
                Assert.Null(resolver.ResolveTexture("valid.png", root));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ExplicitAssetPathDoesNotBorrowAnotherExtensionLikeLtk()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxExactAsset", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string sidecar = Path.Combine(root, "spark.png");
            File.WriteAllBytes(sidecar, Array.Empty<byte>());

            try
            {
                using var resolver = new VfxResourceResolver();
                Assert.Null(resolver.ResolvePath(
                    "spark.dds",
                    root,
                    new[] { ".tex", ".dds", ".png", ".tga" }));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void SyntheticHashAssetCanProbeCandidateExtensions()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxHashAsset", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string sidecar = Path.Combine(root, "0123456789abcdef.dds");
            File.WriteAllBytes(sidecar, Array.Empty<byte>());

            try
            {
                using var resolver = new VfxResourceResolver();
                Assert.Equal(
                    sidecar,
                    resolver.ResolvePath(
                        "0123456789abcdef.tex",
                        root,
                        new[] { ".tex", ".dds" }));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void CorruptAttachedMeshFailsLocallyLikeLtk()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxCorruptAttachedMesh", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string meshPath = Path.Combine(root, "broken.skn");
            File.WriteAllBytes(meshPath, new byte[] { 1, 2, 3, 4, 5, 6 });

            try
            {
                using var resolver = new VfxResourceResolver();

                Assert.Null(resolver.ResolveAttachedMesh("broken.skn", Array.Empty<uint>(), root));
                Assert.Null(resolver.ResolveAttachedMesh("broken.skn", Array.Empty<uint>(), root));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void AttachedMeshWithoutPathDoesNotLoadASecondChampionBody()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerAttachedMesh", Guid.NewGuid().ToString("N"));
            string searchDirectory = Path.Combine(root, "data", "characters", "hero", "skins");
            string modelDirectory = Path.Combine(root, "assets", "characters", "hero", "skins", "skin30");
            Directory.CreateDirectory(searchDirectory);
            Directory.CreateDirectory(modelDirectory);
            File.WriteAllBytes(Path.Combine(modelDirectory, "hero_skin30.skn"), new byte[] { 1, 2, 3, 4 });

            var emitter = new VfxEmitterDefinition(
                Name: "Avatar",
                Rate: VfxCurveF.Const(1f),
                ParticleLifetime: VfxCurveF.Const(1f),
                EmitterLifetime: null,
                ParticleLinger: 0f,
                TimeBeforeFirstEmission: 0f,
                IsSingleParticle: true,
                Disabled: false,
                BlendMode: 4,
                BirthScale: VfxCurve3.Const(new Vector3(15f)),
                ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(Vector4.One),
                ColorOverLife: null,
                BirthVelocity: null,
                Acceleration: null,
                BirthRotationalVelocity: null,
                EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                TexturePath: string.Empty,
                TexDiv: Vector2.One,
                NumFrames: 1,
                RandomStartFrame: false,
                IsMeshPrimitive: true,
                PrimitiveKind: VfxPrimitiveKind.AttachedMesh,
                MeshPath: "assets/characters/hero/skins/skin30/hero_skin30.scb");
            Matrix4x4 authoredTransform = Matrix4x4.CreateScale(0.5f);
            var definition = new VfxSystemDefinition(
                1, "attached", "Characters/Hero/Skins/Skin30/Attached", new[] { emitter }, Transform: authoredTransform);

            try
            {
                using var logger = new LoggerConfiguration().CreateLogger();
                var service = new VfxLoadingService();
                VfxPlaybackRuntime runtime = service.PreparePlayback(
                    definition,
                    searchDirectory,
                    Matrix4x4.CreateTranslation(3f, 0f, 0f),
                    7,
                    new LogService(logger));

                var state = Assert.Single(runtime.Emitters);
                Assert.Null(state.PendingMesh);
                Assert.Equal(VfxPrimitiveKind.AttachedMesh, state.Def.PrimitiveKind);
                Assert.False(state.Def.IsVisual);
                Assert.Equal(Matrix4x4.CreateTranslation(3f, 0f, 0f), runtime.WorldTransform);
                Assert.Equal(authoredTransform, state.DefinitionTransform);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void PlaybackGraphAppliesAuthoredSystemTransformExactlyOnce()
        {
            Matrix4x4 authoredTransform = Matrix4x4.CreateScale(2f);
            Matrix4x4 placement = Matrix4x4.CreateTranslation(3f, 4f, 5f);
            var definition = new VfxSystemDefinition(
                1,
                "transform",
                "Effects/Transform",
                Array.Empty<VfxEmitterDefinition>(),
                Transform: authoredTransform);

            using var logger = new LoggerConfiguration().CreateLogger();
            using var service = new VfxLoadingService();
            VfxPlaybackGraphRuntime graph = service.PreparePlaybackGraph(
                definition,
                new Dictionary<uint, VfxSystemDefinition> { [1] = definition },
                new Dictionary<uint, uint>(),
                string.Empty,
                placement,
                7,
                new LogService(logger));

            Assert.Equal(placement, graph.Root.WorldTransform);
            Assert.Equal(authoredTransform, graph.Root.Definition.Transform);
        }

        [Fact]
        public void AttachedSubmeshSelectionKeepsDrawAlwaysSeparateFromOwnerVisibility()
        {
            uint body = Fnv1a.HashLower("Body");
            uint cape = Fnv1a.HashLower("Cape");
            uint hair = Fnv1a.HashLower("Hair");
            uint weapon = Fnv1a.HashLower("Weapon");
            uint missing = Fnv1a.HashLower("DoesNotExist");

            bool[] narrowed = VfxMeshDecoder.SelectAttachedSubmeshRanges(
                new[] { body, cape, hair, weapon },
                new[] { cape, missing },
                new[] { hair },
                new[] { cape, hair });
            Assert.Equal(new[] { false, false, true, false }, narrowed);

            bool[] staleDrawFallsBackToWholeSkin = VfxMeshDecoder.SelectAttachedSubmeshRanges(
                new[] { body, cape, hair, weapon },
                new[] { missing },
                new[] { cape },
                new[] { hair });
            Assert.Equal(new[] { true, true, false, true }, staleDrawFallsBackToWholeSkin);
        }

        [Fact]
        public void MeshSubmeshSelectionMatchesLtkDrawAndAlwaysSemantics()
        {
            uint body = Fnv1a.HashLower("Body");
            uint cape = Fnv1a.HashLower("Cape");
            uint glow = Fnv1a.HashLower("Glow");
            uint missing = Fnv1a.HashLower("DoesNotExist");

            Assert.Equal(
                new[] { true, true, true },
                VfxMeshDecoder.SelectMeshSubmeshRanges(
                    new[] { body, cape, glow },
                    new[] { missing },
                    new[] { cape }));

            Assert.Equal(
                new[] { true, true, false },
                VfxMeshDecoder.SelectMeshSubmeshRanges(
                    new[] { body, cape, glow },
                    new[] { body },
                    new[] { cape }));
        }

        [Fact]
        public void StaticMeshDecodePreservesAuthoredVertexColors()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxMesh", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string meshPath = Path.Combine(root, "gradient.sco");
            File.WriteAllLines(meshPath, new[]
            {
                "[ObjectBegin]",
                "Name= gradient",
                "CentralPoint= 0 0 0",
                "VertexColors= 1",
                "Verts= 3",
                "0 0 0",
                "1 0 0",
                "0 1 0",
                "255 128 0 64",
                "0 255 128 192",
                "64 0 255 255",
                "Faces= 1",
                "3 0 1 2 material 0 0 1 0 0 1"
            });

            try
            {
                var resolver = new VfxResourceResolver();
                var mesh = resolver.ResolveMesh("gradient.sco", root);

                Assert.True(mesh.HasValue);
                Assert.Equal(12, mesh.Value.Colors.Length);
                Assert.Equal(1f, mesh.Value.Colors[0]);
                Assert.Equal(128f / 255f, mesh.Value.Colors[1], precision: 6);
                Assert.Equal(64f / 255f, mesh.Value.Colors[3], precision: 6);
                Assert.Equal(192f / 255f, mesh.Value.Colors[7], precision: 6);
                Assert.Equal(1f, mesh.Value.Colors[10]);
                Assert.Equal(1f, mesh.Value.Colors[11]);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void StaticMeshDecodeGroupsInterleavedMaterialsAndAppliesSubmeshFiltersLikeLtk()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxStaticRanges", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string meshPath = Path.Combine(root, "interleaved.sco");
            File.WriteAllLines(meshPath, new[]
            {
                "[ObjectBegin]",
                "Name= interleaved",
                "CentralPoint= 0 0 0",
                "VertexColors= 0",
                "Verts= 6",
                "0 0 0",
                "1 0 0",
                "2 0 0",
                "3 0 0",
                "4 0 0",
                "5 0 0",
                "Faces= 3",
                "3 0 1 2 glow 0 0 1 0 0 1",
                "3 3 4 5 core 0 0 1 0 0 1",
                "3 1 2 3 glow 0 0 1 0 0 1"
            });

            try
            {
                using var resolver = new VfxResourceResolver();
                VfxMeshData whole = Assert.IsType<VfxMeshData>(resolver.ResolveMesh("interleaved.sco", root));
                Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, whole.Indices);
                Assert.Equal(new[] { 0f, 1f, 2f, 1f, 2f, 3f, 3f, 4f, 5f },
                    Enumerable.Range(0, whole.Positions.Length / 3).Select(i => whole.Positions[i * 3]).ToArray());

                uint core = Fnv1a.HashLower("core");
                VfxMeshData narrowed = Assert.IsType<VfxMeshData>(resolver.ResolveMesh(
                    "interleaved.sco",
                    new[] { core },
                    Array.Empty<uint>(),
                    root));
                Assert.Equal(new uint[] { 0, 1, 2 }, narrowed.Indices);
                Assert.Equal(new[] { 3f, 4f, 5f },
                    Enumerable.Range(0, narrowed.Positions.Length / 3).Select(i => narrowed.Positions[i * 3]).ToArray());
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void SkinnedMeshDecodeMakesEveryRangeIndexAbsoluteLikeLtk()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxSknRanges", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string meshPath = Path.Combine(root, "two_ranges.skn");
            try
            {
                WriteTwoRangeSkinnedMesh(meshPath);
                using var resolver = new VfxResourceResolver();
                VfxMeshData decoded = Assert.IsType<VfxMeshData>(resolver.ResolveMesh("two_ranges.skn", root));

                Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, decoded.Indices);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void SkinnedMeshDecodeAcceptsNormalizedIndicesFlagLikeLtk()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxNormalizedSkn", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string meshPath = Path.Combine(root, "normalized_ranges.skn");
            try
            {
                WriteTwoRangeSkinnedMesh(meshPath, normalizedIndices: true);
                using var resolver = new VfxResourceResolver();
                VfxMeshData decoded = Assert.IsType<VfxMeshData>(resolver.ResolveMesh("normalized_ranges.skn", root));

                Assert.Equal(new uint[] { 0, 1, 2, 3, 4, 5 }, decoded.Indices);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void SkinnedMeshDecodeRejectsAnInvalidRangeEvenWhenTheDrawListHidesIt()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxBadSknRange", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string meshPath = Path.Combine(root, "bad_range.skn");
            try
            {
                WriteTwoRangeSkinnedMesh(meshPath, new ushort[] { 3, 4, 9 });
                using var resolver = new VfxResourceResolver();
                uint body = Fnv1a.HashLower("body");

                Assert.Null(resolver.ResolveMesh(
                    "bad_range.skn",
                    new[] { body },
                    Array.Empty<uint>(),
                    root));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void ResourceIndexResolvesFileByItsXxHash64()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerXxHash", Guid.NewGuid().ToString("N"));
            string animDir = Path.Combine(root, "assets", "characters", "lulu", "skins", "base", "animations");
            Directory.CreateDirectory(animDir);
            try
            {
                string animPath = Path.Combine(animDir, "lulu_attack1.anm");
                File.WriteAllBytes(animPath, Array.Empty<byte>());

                var index = ProjectResourceIndex.Build(root);

                string relPath = "assets/characters/lulu/skins/base/animations/lulu_attack1.anm";
                ulong hash = XxHash64Ext.Hash(relPath);
                string hexHash = hash.ToString("x16");

                string resolved = index.Resolve($"{hexHash}.anm", new[] { ".anm" });
                Assert.Equal(animPath, resolved);

                resolved = index.Resolve(hexHash, new[] { ".anm" });
                Assert.Equal(animPath, resolved);

                resolved = index.Resolve($"0x{hexHash}.anm", new[] { ".anm" });
                Assert.Equal(animPath, resolved);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void FolderCatalogPrioritizesChampionOverCompanionPet()
        {
            string root = Path.Combine(Path.GetTempPath(), "Lulu.wad.client");
            string champSkinDir = Path.Combine(root, "data", "characters", "lulu", "skins");
            string petSkinDir = Path.Combine(root, "data", "characters", "jade_lulufaerie", "skins");
            Directory.CreateDirectory(champSkinDir);
            Directory.CreateDirectory(petSkinDir);
            try
            {
                string champSkin = Path.Combine(champSkinDir, "skin0.bin");
                string petSkin = Path.Combine(petSkinDir, "skin0.bin");
                WriteSkinBin(champSkin);
                WriteSkinBin(petSkin);

                var skins = StudioProjectCatalog.Scan(root, System.Threading.CancellationToken.None);
                Assert.Equal(2, skins.Count);
                Assert.Equal(champSkin, skins[0].BinPath);
                Assert.Equal(petSkin, skins[1].BinPath);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void FolderCatalogLabelsSkinsAndChromasFromTheirOwnAssetFolders()
        {
            string root = Path.Combine(Path.GetTempPath(), $"AssetsManagerSkinKinds_{Guid.NewGuid():N}");
            var cases = new[]
            {
                (Character: "kayn", Index: 0, Folder: "base", Kind: "Skin"),
                (Character: "kayn", Index: 1, Folder: "skin01", Kind: "Skin"),
                (Character: "kayn", Index: 2, Folder: "skin02", Kind: "Skin"),
                (Character: "kayn", Index: 3, Folder: "skin03", Kind: "Chroma"),
                (Character: "kayn", Index: 4, Folder: "skin04", Kind: (string)null),
                (Character: "other", Index: 3, Folder: "skin03", Kind: "Skin")
            };
            try
            {
                foreach (var item in cases)
                {
                    string bins = Path.Combine(root, "data", "characters", item.Character, "skins");
                    Directory.CreateDirectory(bins);
                    WriteSkinBin(Path.Combine(bins, $"skin{item.Index}.bin"));
                    if (item.Kind == null) continue;

                    string assets = Path.Combine(root, "assets", "characters", item.Character, "skins", item.Folder);
                    Directory.CreateDirectory(assets);
                    string extension = item.Kind == "Skin" ? ".skn" : ".tex";
                    File.WriteAllBytes(Path.Combine(assets, "model" + extension), Array.Empty<byte>());
                    if (item.Kind == "Chroma")
                    {
                        string particles = Path.Combine(assets, "particles");
                        Directory.CreateDirectory(particles);
                        File.WriteAllBytes(Path.Combine(particles, "effect.skn"), Array.Empty<byte>());
                    }
                }

                var entries = StudioProjectCatalog.Scan(root, System.Threading.CancellationToken.None);

                Assert.Equal(cases.Length, entries.Count);
                foreach (var item in cases)
                {
                    string bin = Path.Combine(root, "data", "characters", item.Character, "skins", $"skin{item.Index}.bin");
                    Assert.Equal(item.Kind, Assert.Single(entries, entry => entry.BinPath == bin).KindLabel);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void FolderCatalogListsEveryMapGeometryWithoutSelectingAPrimaryMap()
        {
            string parent = Path.Combine(Path.GetTempPath(), "AssetsManagerMapCatalog", Guid.NewGuid().ToString("N"));
            string root = Path.Combine(parent, "map11.wad.client");
            try
            {
                string map11 = Path.Combine(root, "data", "maps", "mapgeometry", "map11");
                string map12 = Path.Combine(root, "data", "maps", "mapgeometry", "map12");
                Directory.CreateDirectory(map11);
                Directory.CreateDirectory(map12);
                File.WriteAllBytes(Path.Combine(map11, "base_srx.mapgeo"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(map11, "base_srx.materials.bin"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(map12, "base_other.mapgeo"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(map12, "base_other.materials.bin"), Array.Empty<byte>());

                StudioProjectCatalog.BrowserCatalog catalog = StudioProjectCatalog.ScanBrowser(
                    root,
                    System.Threading.CancellationToken.None,
                    resolveBinEntry: null);

                Assert.Equal(2, catalog.MapSources.Count);
                Assert.Equal("Maps/MapGeometry/Map11/Base_SRX", catalog.MapSources[0].Map.Value, ignoreCase: true);
                Assert.Equal("Maps/MapGeometry/Map12/Base_Other", catalog.MapSources[1].Map.Value, ignoreCase: true);
                Assert.All(catalog.MapSources, source => Assert.Equal(Path.GetFullPath(root), source.ProjectRoot));

                StudioBrowserFolder mapRoot = Assert.Single(catalog.Roots);
                Assert.Equal("MapGeometry", mapRoot.Title);
                MapBrowserNode[] nodes = mapRoot.Children.OfType<MapBrowserNode>().ToArray();
                Assert.Equal(2, nodes.Length);
                Assert.All(nodes, node => Assert.Equal(MapBrowserNodeKind.MapFile, node.Kind));
                Assert.Equal("base_srx.mapgeo", nodes[0].Title, ignoreCase: true);
                Assert.Equal("base_other.mapgeo", nodes[1].Title, ignoreCase: true);
            }
            finally
            {
                if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
            }
        }

        [Fact]
        public void FolderCatalogPreservesAuthoredMapVariantsWithoutSelectingAMap()
        {
            string parent = Path.Combine(Path.GetTempPath(), "AssetsManagerMapVariants", Guid.NewGuid().ToString("N"));
            string root = Path.Combine(parent, "map11.wad.client");
            const string mapEntry = "Maps/Shipping/Map11";
            const string defaultSkin = "Maps/Shipping/Map11/MapSkins/Default";
            const string odysseySkin = "Maps/Shipping/Map11/MapSkins/Odyssey";
            const string defaultMap = "Maps/MapGeometry/Map11/Base_SRX";
            const string odysseyMap = "Maps/MapGeometry/Map11/Odyssey";
            try
            {
                string mapDirectory = Path.Combine(root, "data", "maps", "mapgeometry", "map11");
                Directory.CreateDirectory(mapDirectory);
                File.WriteAllBytes(Path.Combine(mapDirectory, "base_srx.mapgeo"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(mapDirectory, "base_srx.materials.bin"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(mapDirectory, "odyssey.mapgeo"), Array.Empty<byte>());
                File.WriteAllBytes(Path.Combine(mapDirectory, "odyssey.materials.bin"), Array.Empty<byte>());

                string declarationDirectory = Path.Combine(root, "data", "maps", "shipping");
                Directory.CreateDirectory(declarationDirectory);
                WriteBin(
                    Path.Combine(declarationDirectory, "map11.bin"),
                    new[]
                    {
                        new BinTreeObject(
                            Fnv1a.HashLower(mapEntry),
                            MapVariantParser.MapClass,
                            new BinTreeProperty[]
                            {
                                new BinTreeUnorderedContainer(
                                    MapVariantParser.MapSkinsField,
                                    BinPropertyType.ObjectLink,
                                    new BinTreeProperty[]
                                    {
                                        new BinTreeObjectLink(0, Fnv1a.HashLower(odysseySkin)),
                                        new BinTreeObjectLink(0, Fnv1a.HashLower(defaultSkin))
                                    })
                            }),
                        new BinTreeObject(
                            Fnv1a.HashLower(odysseySkin),
                            MapVariantParser.MapSkinClass,
                            new BinTreeProperty[]
                            {
                                new BinTreeString(MapVariantParser.SkinNameField, "Odyssey"),
                                new BinTreeString(MapVariantParser.ContainerLinkField, odysseyMap)
                            }),
                        new BinTreeObject(
                            Fnv1a.HashLower(defaultSkin),
                            MapVariantParser.MapSkinClass,
                            new BinTreeProperty[]
                            {
                                new BinTreeString(MapVariantParser.SkinNameField, "Default"),
                                new BinTreeString(MapVariantParser.ContainerLinkField, defaultMap)
                            })
                    },
                    Array.Empty<string>());

                StudioProjectCatalog.BrowserCatalog catalog = StudioProjectCatalog.ScanBrowser(
                    root,
                    System.Threading.CancellationToken.None,
                    resolveBinEntry: null);

                Assert.Equal(2, catalog.MapVariants.Count);
                Assert.Equal("Odyssey", catalog.MapVariants[0].Skin);
                Assert.Equal("Default", catalog.MapVariants[1].Skin);
                Assert.Equal(defaultMap, MapVariantData.Opening(catalog.MapVariants).Map.Value);
                Assert.Equal(2, catalog.MapSources.Count);
                Assert.Contains(catalog.MapSources, source => source.Map.Value.Equals(defaultMap, StringComparison.OrdinalIgnoreCase));
                Assert.Contains(catalog.MapSources, source => source.Map.Value.Equals(odysseyMap, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
            }
        }

        [Fact]
        public void FolderCatalogBuildsCharacterSkinAndSpellHierarchyWhileKeepingThemesAsSupportData()
        {
            string root = Path.Combine(Path.GetTempPath(), "Companions.wad.client");
            string yunara = Path.Combine(root, "data", "characters", "petchibiyunara");
            string zoe = Path.Combine(root, "data", "characters", "petchibizoe");
            Directory.CreateDirectory(Path.Combine(yunara, "skins"));
            Directory.CreateDirectory(Path.Combine(yunara, "themes", "spiritblossom"));
            Directory.CreateDirectory(Path.Combine(yunara, "spells"));
            Directory.CreateDirectory(Path.Combine(zoe, "skins"));

            const string spellPath = "Characters/PetChibiYunara/Spells/Q/Missile";
            uint spellHash = Fnv1a.HashLower(spellPath);
            string themeTier = Path.Combine(yunara, "themes", "spiritblossom", "tier1.bin");
            try
            {
                WriteSkinBin(Path.Combine(yunara, "skins", "root.bin"), previewable: false);
                WriteSkinBin(Path.Combine(yunara, "skins", "skin1.bin"));
                WriteSkinBin(themeTier);
                WriteSkinBin(Path.Combine(zoe, "skins", "skin2.bin"));
                WriteBin(
                    Path.Combine(yunara, "spells", "spells.bin"),
                    new[] { new BinTreeObject(spellPath, "SpellObject", Array.Empty<BinTreeProperty>()) },
                    Array.Empty<string>());

                StudioProjectCatalog.BrowserCatalog catalog = StudioProjectCatalog.ScanBrowser(
                    root,
                    System.Threading.CancellationToken.None,
                    hash => hash == spellHash ? spellPath : null);

                // Theme BINs stay indexed as support/resource data, but they are not duplicate
                // preview choices beside the authored Skin entries.
                Assert.Contains(catalog.Entries, entry => entry.BinPath == Path.GetFullPath(themeTier));

                StudioBrowserFolder characters = Assert.Single(catalog.Roots);
                Assert.Equal("Characters", characters.Title);
                Assert.True(characters.IsExpanded);
                Assert.Equal(2, characters.Children.Count);

                StudioBrowserFolder yunaraNode = Assert.IsType<StudioBrowserFolder>(characters.Children[0]);
                Assert.Equal("PetChibiYunara", yunaraNode.Title);
                Assert.Equal("Companion", yunaraNode.Subtitle);
                Assert.Equal(new[] { "Skins" },
                    yunaraNode.Children.Cast<StudioBrowserFolder>().Select(folder => folder.Title));

                StudioBrowserFolder skins = Assert.IsType<StudioBrowserFolder>(yunaraNode.Children[0]);
                StudioSkinItem skin = Assert.IsType<StudioSkinItem>(Assert.Single(skins.Children));
                Assert.Equal("Skin 1", skin.Title);
                Assert.Equal("PetChibiYunara", skin.OwnerName);
                Assert.Equal(new[] { "Systems", "Clips", "Spells" }, skin.Sections.Select(section => section.Title));

                StudioBrowserFolder spellGroup = Assert.IsType<StudioBrowserFolder>(Assert.Single(skin.SpellItems));
                Assert.Equal("Q", spellGroup.Title);
                StudioSpellBrowserItem spell = Assert.IsType<StudioSpellBrowserItem>(Assert.Single(spellGroup.Children));
                Assert.Equal("Missile", spell.Name);
                Assert.Equal(spellPath, spell.ObjectPath);

                StudioBrowserFolder zoeNode = Assert.IsType<StudioBrowserFolder>(characters.Children[1]);
                Assert.Equal("PetChibiZoe", zoeNode.Title);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Theory]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        [InlineData(false, 0)]
        [InlineData(false, GpuSkinningData.MaxBones + 1)]
        public void PreparePlaybackRequiresBothMeshAndSkeletonForEmissionSurface(bool missingSkeleton, int influences)
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxEmissionSurface", Guid.NewGuid().ToString("N"));
            string searchDirectory = Path.Combine(root, "data", "characters", "hero", "skins");
            string assetDirectory = Path.Combine(root, "assets", "effects");
            Directory.CreateDirectory(searchDirectory);
            Directory.CreateDirectory(assetDirectory);
            string meshPath = Path.Combine(assetDirectory, "surface.skn");
            WriteTwoRangeSkinnedMesh(meshPath);
            var rigBuilder = new LeagueToolkit.Core.Animation.Builders.RigResourceBuilder();
            for (int index = 0; index < Math.Max(1, influences); index++)
                rigBuilder.CreateJoint(index == 0 ? "Root" : $"Extra{index}").WithInfluence(influences > 0)
                    .WithLocalTransform(Matrix4x4.Identity).WithInverseBindTransform(Matrix4x4.Identity);
            var rig = rigBuilder.Build();
            using (var stream = File.Create(Path.Combine(assetDirectory, "surface.skl"))) rig.Write(stream);

            VfxEmitterDefinition emitter = CreateEmitter(VfxPrimitiveKind.CameraQuad) with
            {
                EmissionSurface = new VfxEmissionSurfaceDefinition(
                    VfxEmissionSurfaceKind.Mesh,
                    "assets/effects/surface.skn",
                    missingSkeleton ? null : "assets/effects/surface.skl",
                    Array.Empty<uint>(),
                    Array.Empty<uint>(),
                    Scale: 1f,
                    MaxJointWeights: 4,
                    UseNormal: false)
            };
            var definition = new VfxSystemDefinition(1, "surface", "Effects/Surface", new[] { emitter });

            try
            {
                using var logger = new LoggerConfiguration().CreateLogger();
                using var service = new VfxLoadingService();
                VfxPlaybackRuntime runtime = service.PreparePlayback(
                    definition,
                    searchDirectory,
                    Matrix4x4.Identity,
                    1234,
                    new LogService(logger));

                runtime.Update(0.02f);

                var particle = Assert.Single(Assert.Single(runtime.Emitters).Particles);
                if (missingSkeleton)
                {
                    Assert.Equal(Vector3.Zero, particle.Pos);
                    return;
                }
                using var resolver = new VfxResourceResolver();
                var gpuPose = resolver.ResolveMeshAnimation("assets/effects/surface.skn",
                    "assets/effects/surface.skl", null, searchDirectory);
                if (influences == 0 || influences > GpuSkinningData.MaxBones) Assert.Null(gpuPose);
                else Assert.NotNull(gpuPose);
                var cpuPose = resolver.ResolveMeshAnimation("assets/effects/surface.skn",
                    "assets/effects/surface.skl", null, searchDirectory, requireGpuSkinning: false);
                Assert.NotNull(cpuPose);
                Assert.Equal(influences, cpuPose.PaletteCount);
                Assert.Same(cpuPose, resolver.ResolveMeshAnimation("assets/effects/surface.skn",
                    "assets/effects/surface.skl", null, searchDirectory, requireGpuSkinning: false));
                Assert.Same(gpuPose, resolver.ResolveMeshAnimation("assets/effects/surface.skn",
                    "assets/effects/surface.skl", null, searchDirectory));
                Assert.NotEqual(Vector3.Zero, particle.Pos);
                Assert.Equal(particle.Pos.X * 2f, particle.Pos.Y, precision: 4);
                Assert.Equal(particle.Pos.X * 3f, particle.Pos.Z, precision: 4);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
        [Fact]
        public void EmissionPoseMatchesHostNamesAndKeepsMissingJointsInRestPose()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerBoundSurface", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string meshPath = Path.Combine(root, "surface.skn");
                string skeletonPath = Path.Combine(root, "surface.skl");
                WriteTwoRangeSkinnedMesh(meshPath);
                var builder = new LeagueToolkit.Core.Animation.Builders.RigResourceBuilder();
                builder.CreateJoint("Root").WithInfluence(true).WithLocalTransform(Matrix4x4.Identity)
                    .WithInverseBindTransform(Matrix4x4.Identity);
                builder.CreateJoint("Missing").WithLocalTransform(Matrix4x4.CreateTranslation(0, 9, 0))
                    .WithInverseBindTransform(Matrix4x4.CreateTranslation(0, -9, 0));
                using (var stream = File.Create(skeletonPath)) builder.Build().Write(stream);
                using var source = VfxAnimatedMesh.Load(meshPath, skeletonPath);
                float offset = .2f;
                var bound = source.CreateEmissionPose((time, name, hash) =>
                {
                    Assert.Equal(Fnv1a.HashLower(name), hash);
                    return name == "Root" ? Matrix4x4.CreateTranslation((time + offset) * 10, 0, 0) : null;
                });
                var unbound = source.CreateEmissionPose(null);
                var positions = new Vector3[2];
                bound.EvaluateJointPositions(.5f, positions);
                Assert.Equal(new Vector3(7, 0, 0), positions[0]);
                Assert.Equal(new Vector3(0, 9, 0), positions[1]);
                Assert.Equal(new Vector3(7, 0, 0), bound.EvaluatePalette(.5f)[0].Translation);
                Assert.Equal(Matrix4x4.Identity, unbound.EvaluatePalette(.5f)[0]);
                offset = .4f;
                Assert.Equal(new Vector3(9, 0, 0), bound.EvaluatePalette(.5f)[0].Translation);
                Assert.Equal(Matrix4x4.Identity, unbound.EvaluatePalette(.5f)[0]);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void LoadedEmissionSurfaceUsesRunTimeAcrossDelayedRootsAndPoseReplay()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerSurfaceReplay", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                WriteTwoRangeSkinnedMesh(Path.Combine(root, "surface.skn"));
                var builder = new LeagueToolkit.Core.Animation.Builders.RigResourceBuilder();
                builder.CreateJoint("Root").WithInfluence(true).WithLocalTransform(Matrix4x4.Identity)
                    .WithInverseBindTransform(Matrix4x4.Identity);
                using (var stream = File.Create(Path.Combine(root, "surface.skl"))) builder.Build().Write(stream);
                var emitter = CreateEmitter(VfxPrimitiveKind.CameraQuad) with
                {
                    IsSingleParticle = false, Rate = VfxCurveF.Const(20),
                    ParticleLifetime = VfxCurveF.Const(2),
                    EmissionSurface = new VfxEmissionSurfaceDefinition(VfxEmissionSurfaceKind.Mesh,
                        "surface.skn", "surface.skl", Array.Empty<uint>(), Array.Empty<uint>(), UseNormal: false)
                };
                var definition = new VfxSystemDefinition(1, "surface", "surface", new[] { emitter });
                var systems = new Dictionary<uint, VfxSystemDefinition> { [1] = definition };
                var resources = new Dictionary<uint, uint>();
                using var service = new VfxLoadingService();
                using var logger = new LoggerConfiguration().CreateLogger();
                var log = new LogService(logger);
                float sampledTime = -1;
                var bound = service.PreparePlaybackGraph(definition, systems, resources, root, Matrix4x4.Identity,
                    1234, log, emissionPoseSampler: (time, name, hash) =>
                    {
                        sampledTime = time;
                        return name == "Root" ? Matrix4x4.CreateTranslation(time * 10, 0, 0) : null;
                    });
                var unbound = service.PreparePlaybackGraph(definition, systems, resources, root, Matrix4x4.Identity, 1234, log);
                bound.SetStartDelay(.4f);
                unbound.SetStartDelay(.4f);
                bound.Update(.3f);
                unbound.Update(.3f);
                Assert.Equal(-1, sampledTime);
                bound.Update(.2f);
                unbound.Update(.2f);
                Assert.Equal(.5f, sampledTime, 5);
                var boundParticles = bound.Root.Emitters[0].Particles;
                var plainParticles = unbound.Root.Emitters[0].Particles;
                Assert.NotEmpty(boundParticles);
                Assert.Equal(plainParticles.Count, boundParticles.Count);
                for (int index = 0; index < boundParticles.Count; index++)
                    Assert.Equal(plainParticles[index].Pos + new Vector3(5, 0, 0), boundParticles[index].Pos);

                var child = definition with { PathHash = 2 };
                var parentEmitter = emitter with { EmissionSurface = null, IsSingleParticle = true,
                    ChildParticleSet = new VfxChildParticleSetDefinition(new[] { new VfxChildSystemReference("child", 2, 0) },
                        false, VfxCurveF.Zero, VfxCurve3.Const(Vector3.Zero), 0) };
                var parent = definition with { Emitters = new[] { parentEmitter } };
                var family = new Dictionary<uint, VfxSystemDefinition> { [1] = parent, [2] = child };
                var childTimes = new List<float>();
                var posedFamily = service.PreparePlaybackGraph(parent, family, resources, root, Matrix4x4.Identity,
                    1234, log, emissionPoseSampler: (time, name, hash) =>
                    {
                        childTimes.Add(time);
                        return name == "Root" ? Matrix4x4.CreateTranslation(time * 10, 0, 0) : null;
                    });
                var plainFamily = service.PreparePlaybackGraph(parent, family, resources, root, Matrix4x4.Identity, 1234, log);
                foreach (var graph in new[] { posedFamily, plainFamily })
                {
                    graph.SetStartDelay(.4f);
                    graph.Update(.5f);
                    graph.Update(.1f);
                }
                Assert.NotEmpty(childTimes);
                Assert.All(childTimes, time => Assert.Equal(.6f, time, 5));
                Assert.True(posedFamily.Runtimes.Count > 1);
                var posedChild = posedFamily.Runtimes[1].Emitters[0].Particles;
                var plainChild = plainFamily.Runtimes[1].Emitters[0].Particles;
                Assert.NotEmpty(posedChild);
                Assert.Equal(plainChild.Count, posedChild.Count);
                for (int index = 0; index < posedChild.Count; index++)
                    Assert.Equal(plainChild[index].Pos + new Vector3(6, 0, 0), posedChild[index].Pos);

                VfxSystemModel Model() => new() { Name = "surface", Definition = definition,
                    SystemCatalog = systems, ResourceMap = resources, SearchDirectory = root,
                    PlaybackSeed = 1234, TotalDuration = 2 };
                using var replay = new AssetsManager.Services.Viewer.Vfx.Session.VfxRenderSession();
                using var straight = new AssetsManager.Services.Viewer.Vfx.Session.VfxRenderSession();
                replay.SetSystem(Model());
                straight.SetSystem(Model());
                Func<double, string, uint, Matrix4x4?> sampler = (time, name, hash) => name == "Root"
                    ? Matrix4x4.CreateTranslation((float)time * 10, 0, 0) : null;
                straight.SetBoneTransformSampler(sampler);
                straight.Seek(.8);
                replay.Seek(.8);
                Assert.NotEqual(straight.Graphs[0].Root.Emitters[0].Particles, replay.Graphs[0].Root.Emitters[0].Particles);
                replay.SetBoneTransformSampler(sampler);
                Assert.Equal(.8, replay.CurrentTime, 6);
                Assert.Equal(straight.Graphs[0].Root.Emitters[0].Particles, replay.Graphs[0].Root.Emitters[0].Particles);
                replay.Seek(1.1);
                replay.Seek(.8);
                Assert.Equal(straight.Graphs[0].Root.Emitters[0].Particles, replay.Graphs[0].Root.Emitters[0].Particles);
                replay.SetBoneTransformSampler(null);
                straight.SetBoneTransformSampler(null);
                Assert.Equal(.8, replay.CurrentTime, 6);
                Assert.Equal(straight.Graphs[0].Root.Emitters[0].Particles, replay.Graphs[0].Root.Emitters[0].Particles);
                Func<string, uint, Matrix4x4?> bind = (name, hash) => name == "Root"
                    ? Matrix4x4.CreateTranslation(0, 6, 0) : null;
                replay.UpdateBoneTransforms(bind);
                using var bindFromStart = new AssetsManager.Services.Viewer.Vfx.Session.VfxRenderSession();
                bindFromStart.SetSystem(Model());
                bindFromStart.UpdateBoneTransforms(bind);
                bindFromStart.Seek(.8);
                Assert.Equal(bindFromStart.Graphs[0].Root.Emitters[0].Particles, replay.Graphs[0].Root.Emitters[0].Particles);
                replay.UpdateBoneTransforms(null);
                Assert.Equal(straight.Graphs[0].Root.Emitters[0].Particles, replay.Graphs[0].Root.Emitters[0].Particles);
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        private static void WriteTwoRangeSkinnedMesh(
            string path,
            ushort[] secondRangeIndices = null,
            bool normalizedIndices = false)
        {
            VertexBufferDescription description = SkinnedMeshVertex.BASIC;
            var vertexOwner = VertexBuffer.AllocateForElements(description.Elements, 6);
            Span<byte> vertices = vertexOwner.Span;
            const int stride = 52;
            for (int vertex = 0; vertex < 6; vertex++)
            {
                int at = vertex * stride;
                BitConverter.TryWriteBytes(vertices.Slice(at, 4), (float)vertex);
                BitConverter.TryWriteBytes(vertices.Slice(at + 4, 4), vertex * 2f);
                BitConverter.TryWriteBytes(vertices.Slice(at + 8, 4), vertex * 3f);
                vertices[at + 12] = 0;
                BitConverter.TryWriteBytes(vertices.Slice(at + 16, 4), 1f);
                BitConverter.TryWriteBytes(vertices.Slice(at + 36, 4), 1f);
                BitConverter.TryWriteBytes(vertices.Slice(at + 44, 4), (float)vertex);
                BitConverter.TryWriteBytes(vertices.Slice(at + 48, 4), -(float)vertex);
            }

            VertexBuffer vertexBuffer = VertexBuffer.Create(description.Usage, description.Elements, vertexOwner);
            MemoryOwner<byte> indexOwner = MemoryOwner<byte>.Allocate(6 * sizeof(ushort));
            Span<byte> indices = indexOwner.Span;
            secondRangeIndices ??= normalizedIndices
                ? new ushort[] { 0, 1, 2 }
                : new ushort[] { 3, 4, 5 };
            Assert.Equal(3, secondRangeIndices.Length);
            ushort[] stored = { 0, 1, 2, secondRangeIndices[0], secondRangeIndices[1], secondRangeIndices[2] };
            for (int index = 0; index < stored.Length; index++)
                BitConverter.TryWriteBytes(indices.Slice(index * sizeof(ushort), sizeof(ushort)), stored[index]);
            IndexBuffer indexBuffer = IndexBuffer.Create(IndexFormat.U16, indexOwner);

            using var mesh = new SkinnedMesh(
                new[]
                {
                    new SkinnedMeshRange("body", 0, 3, 0, 3),
                    new SkinnedMeshRange("cape", 3, 3, 3, 3)
                },
                vertexBuffer,
                indexBuffer);
            mesh.WriteSimpleSkin(path);

            if (normalizedIndices)
                PatchSimpleSkinFlags(path, SkinnedMeshIndexSemantics.NormalizedIndicesFlag);
        }

        private static void PatchSimpleSkinFlags(string path, uint flags)
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            stream.Position = 8;
            uint rangeCount = reader.ReadUInt32();
            stream.Position = checked(12L + (rangeCount * 80L));
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write(flags);
        }

        private static void WriteSkinBin(string path, bool previewable = true)
        {
            BinTreeProperty[] properties = Array.Empty<BinTreeProperty>();
            if (previewable)
            {
                properties = new BinTreeProperty[]
                {
                    new BinTreeStruct(
                        Fnv1a.HashLower("skinMeshProperties"),
                        Fnv1a.HashLower("SkinMeshDataProperties"),
                        new BinTreeProperty[]
                        {
                            new BinTreeString(Fnv1a.HashLower("simpleSkin"), "ASSETS/Test/Test.skn")
                        })
                };
            }

            var obj = new BinTreeObject("SkinData", "SkinCharacterDataProperties", properties);
            var tree = new BinTree(new[] { obj }, Array.Empty<string>());
            using var stream = File.Create(path);
            tree.Write(stream);
        }

        private static VfxEmitterDefinition CreateEmitter(VfxPrimitiveKind primitiveKind)
            => new(
                Name: "mesh",
                Rate: VfxCurveF.Const(1f),
                ParticleLifetime: VfxCurveF.Const(1f),
                EmitterLifetime: null,
                ParticleLinger: 0f,
                TimeBeforeFirstEmission: 0f,
                IsSingleParticle: true,
                Disabled: false,
                BlendMode: 1,
                BirthScale: VfxCurve3.Const(Vector3.One),
                ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(Vector4.One),
                ColorOverLife: null,
                BirthVelocity: null,
                Acceleration: null,
                BirthRotationalVelocity: null,
                EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                TexturePath: string.Empty,
                TexDiv: Vector2.One,
                NumFrames: 1,
                RandomStartFrame: false,
                IsMeshPrimitive: primitiveKind == VfxPrimitiveKind.Mesh,
                PrimitiveKind: primitiveKind);

        private static BinTreeObject CreateSystem(string path, string name)
            => new(
                path,
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("particleName"), name),
                    new BinTreeString(Fnv1a.HashLower("particlePath"), path)
                });

        private static BinTreeObject CreateResolver(string path, uint key, uint target)
            => new(
                path,
                "ResourceResolver",
                new BinTreeProperty[]
                {
                    new BinTreeMap(
                        Fnv1a.HashLower("resourceMap"),
                        BinPropertyType.Hash,
                        BinPropertyType.ObjectLink,
                        new[]
                        {
                            new KeyValuePair<BinTreeProperty, BinTreeProperty>(
                                new BinTreeHash(0, key),
                                new BinTreeObjectLink(0, target))
                        })
                });

        private static BinTreeObject CreateSkin(string path, uint resolverHash)
            => new(
                path,
                "SkinCharacterDataProperties",
                new BinTreeProperty[]
                {
                    new BinTreeObjectLink(Fnv1a.HashLower("mResourceResolver"), resolverHash)
                });

        private static void WriteBin(string path, BinTreeObject[] objects, string[] dependencies)
        {
            var tree = new BinTree(objects, dependencies);
            using var stream = File.Create(path);
            tree.Write(stream);
        }
    }
}
