using System;
using System.IO;
using System.Numerics;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Vfx;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Serilog;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer.Vfx
{
    public sealed class VfxLoadingServiceTests
    {
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

        private static BinTreeObject CreateSystem(string path, string name)
            => new(
                path,
                "VfxSystemDefinitionData",
                new BinTreeProperty[]
                {
                    new BinTreeString(Fnv1a.HashLower("particleName"), name),
                    new BinTreeString(Fnv1a.HashLower("particlePath"), path)
                });

        private static void WriteBin(string path, BinTreeObject[] objects, string[] dependencies)
        {
            var tree = new BinTree(objects, dependencies);
            using var stream = File.Create(path);
            tree.Write(stream);
        }
    }
}
