using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CommunityToolkit.HighPerformance.Buffers;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
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
                var index = VfxResourceIndex.Build(root);
                Assert.Equal(dataPath, Assert.Single(index.ResolveLinkedAll("DATA/Shared/Effect.bin", new[] { ".bin" })));
                Assert.Equal(assetPath, Assert.Single(index.ResolveLinkedAll("ASSETS/Shared/Effect.bin", new[] { ".bin" })));
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
                var index = VfxResourceIndex.Build(root);

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
        public void LinkedBinTraversalStopsAfterTheFirstThirtyTwoFilesLikeLtk()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxLinkedCap", Guid.NewGuid().ToString("N"));
            string championDirectory = Path.Combine(root, "data", "characters", "hero");
            string skinsDirectory = Path.Combine(championDirectory, "skins");
            Directory.CreateDirectory(skinsDirectory);
            try
            {
                string skin = Path.Combine(skinsDirectory, "skin0.bin");
                var dependencies = new List<string>();
                for (int index = 0; index < 33; index++)
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

                Assert.Equal(33, bundle.LoadedBins.Count); // primary + 32 linked documents
                Assert.Equal(32, bundle.Systems.Count);
                Assert.True(bundle.Systems.ContainsKey(Fnv1a.HashLower("Effects/Linked31")));
                Assert.False(bundle.Systems.ContainsKey(Fnv1a.HashLower("Effects/Linked32")));
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
                Assert.Equal(
                    Path.GetFullPath(sharedBin),
                    bundle.SystemSources[system.PathHash],
                    ignoreCase: true);
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

        [Fact]
        public void UnsupportedSimpleMeshFailsLocallyLikeLtk()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxUnsupportedMesh", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string meshPath = Path.Combine(root, "unsupported.gmesh");
            File.WriteAllBytes(meshPath, new byte[] { (byte)'G', (byte)'M', (byte)'S', (byte)'H', 1, 0, 0, 0 });

            try
            {
                using var resolver = new VfxResourceResolver();

                Assert.Null(resolver.ResolveMesh("unsupported.gmesh", root));
                // The failed decode is cached just like an unresolved asset, so repeated draws
                // cannot repeatedly throw or reparse the same unsupported resource.
                Assert.Null(resolver.ResolveMesh("unsupported.gmesh", root));
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
                Assert.Equal(authoredTransform * Matrix4x4.CreateTranslation(3f, 0f, 0f), runtime.WorldTransform);
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

            Assert.Equal(authoredTransform * placement, graph.Root.WorldTransform);
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
        public void SkinnedMeshDecodeRejectsAnInvalidRangeEvenWhenTheDrawListHidesIt()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxBadSknRange", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string meshPath = Path.Combine(root, "bad_range.skn");
            try
            {
                WriteTwoRangeSkinnedMesh(meshPath, new ushort[] { 0, 1, 9 });
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

                var index = VfxResourceIndex.Build(root);

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

                var skins = VfxFolderCatalog.Scan(root, System.Threading.CancellationToken.None);
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

                VfxFolderCatalog.BrowserCatalog catalog = VfxFolderCatalog.ScanBrowser(
                    root,
                    System.Threading.CancellationToken.None,
                    hash => hash == spellHash ? spellPath : null);

                // Theme BINs stay indexed as support/resource data, but they are not duplicate
                // preview choices beside the authored Skin entries.
                Assert.Contains(catalog.Entries, entry => entry.BinPath == Path.GetFullPath(themeTier));

                VfxBrowserFolder characters = Assert.Single(catalog.Roots);
                Assert.Equal("Characters", characters.Title);
                Assert.True(characters.IsExpanded);
                Assert.Equal(2, characters.Children.Count);

                VfxBrowserFolder yunaraNode = Assert.IsType<VfxBrowserFolder>(characters.Children[0]);
                Assert.Equal("PetChibiYunara", yunaraNode.Title);
                Assert.Equal("Companion", yunaraNode.Subtitle);
                Assert.Equal(new[] { "Skins" },
                    yunaraNode.Children.Cast<VfxBrowserFolder>().Select(folder => folder.Title));

                VfxBrowserFolder skins = Assert.IsType<VfxBrowserFolder>(yunaraNode.Children[0]);
                VfxSkinItem skin = Assert.IsType<VfxSkinItem>(Assert.Single(skins.Children));
                Assert.Equal("Skin 1", skin.Title);
                Assert.Equal(new[] { "Systems", "Clips", "Spells" }, skin.Sections.Select(section => section.Title));

                VfxBrowserFolder spellGroup = Assert.IsType<VfxBrowserFolder>(Assert.Single(skin.SpellItems));
                Assert.Equal("Q", spellGroup.Title);
                VfxSpellBrowserItem spell = Assert.IsType<VfxSpellBrowserItem>(Assert.Single(spellGroup.Children));
                Assert.Equal("Missile", spell.Name);
                Assert.Equal(spellPath, spell.ObjectPath);

                VfxBrowserFolder zoeNode = Assert.IsType<VfxBrowserFolder>(characters.Children[1]);
                Assert.Equal("PetChibiZoe", zoeNode.Title);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        private static void WriteTwoRangeSkinnedMesh(string path, ushort[] secondRangeIndices = null)
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
            secondRangeIndices ??= new ushort[] { 0, 1, 2 };
            Assert.Equal(3, secondRangeIndices.Length);
            ushort[] local = { 0, 1, 2, secondRangeIndices[0], secondRangeIndices[1], secondRangeIndices[2] };
            for (int index = 0; index < local.Length; index++)
                BitConverter.TryWriteBytes(indices.Slice(index * sizeof(ushort), sizeof(ushort)), local[index]);
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
