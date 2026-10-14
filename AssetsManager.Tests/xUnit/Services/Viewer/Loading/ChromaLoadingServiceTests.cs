using System;
using System.IO;
using AssetsManager.Utils;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Vfx.Loading;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Renderer;
using CommunityToolkit.HighPerformance.Buffers;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Loading
{
    public sealed class ChromaLoadingServiceTests
    {
        private static readonly Lazy<Dispatcher> UiDispatcher = new(() =>
        {
            var ready = new TaskCompletionSource<Dispatcher>();
            var thread = new Thread(() =>
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                ready.SetResult(application.Dispatcher);
                Dispatcher.Run();
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return ready.Task.GetAwaiter().GetResult();
        });

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public Task StudioChromaLoadsReplacementTexturesWithoutBin(bool reference) =>
            UiDispatcher.Value.InvokeAsync(async () =>
        {
            string root = Path.Combine(Path.GetTempPath(), $"am-studio-chroma-{Guid.NewGuid():N}");
            try
            {
                CreateSkin(root, "skin02", true);
                CreateSkin(root, "skin04", false);
                string modelPath = Path.Combine(root, "skin02", "skin02.skn");
                var description = SkinnedMeshVertex.COLOR;
                var vertices = VertexBuffer.Create(description.Usage, description.Elements,
                    VertexBuffer.AllocateForElements(description.Elements, 3));
                var indices = MemoryOwner<byte>.Allocate(6);
                for (ushort i = 0; i < 3; i++)
                    BitConverter.TryWriteBytes(indices.Span.Slice(i * 2, 2), i);
                using (var mesh = new SkinnedMesh(new[] { new SkinnedMeshRange("body", 0, 3, 0, 3) },
                    vertices, IndexBuffer.Create(IndexFormat.U16, indices)))
                    mesh.WriteSimpleSkin(modelPath);
                string textures = Path.Combine(root, "skin04");
                WriteSolidDds(Path.Combine(textures, "chroma_tx_cm.dds"));
                var chroma = new ChromaSkinModel
                {
                    Name = "SKIN04", ModelPath = modelPath, TexturePath = textures, SourceRoot = root,
                    SourceKind = reference ? ChromaSourceKind.Reference : ChromaSourceKind.Current
                };
                StudioSkinItem skin = ChromaLoadingService.CreateStudioSkin(chroma);
                Assert.NotNull(skin);
                Assert.Null(skin.BinPath);
                Assert.Equal(textures, skin.IdentityPath);
                Assert.Equal(root, skin.ResourceRoot);
                if (reference) Assert.Contains("Reference", skin.DisplayName);
                using var logger = new LoggerConfiguration().CreateLogger();
                var log = new LogService(logger);
                using var loading = new VfxLoadingService();
                var sknLoading = new SknLoadingService(log);
                var (focused, resolved) = await sknLoading.LoadStudioModelAsync(
                    skin, new VfxLoadingService.Bundle(), null, loading, root, CancellationToken.None);
                Assert.Equal(modelPath, resolved);
                Assert.NotNull(focused);
                using (focused)
                    Assert.Contains("chroma_tx_cm", Assert.Single(focused.Parts).AllTextures.Keys);

                var actor = new StudioSceneActor(skin);
                StudioSceneActorRuntime runtime = await StudioSceneActorRuntime.LoadAsync(
                    actor, loading, sknLoading, log, "unused-project-root", CancellationToken.None);
                Assert.NotNull(runtime);
                try
                {
                    Assert.Equal(root, runtime.SearchDirectory);
                    Assert.Equal(textures, runtime.SourceIdentity);
                    Assert.Contains("chroma_tx_cm", Assert.Single(runtime.Model.Parts).AllTextures.Keys);
                    await runtime.PlayRememberedClipAsync(actor, log, CancellationToken.None);
                }
                finally { runtime.ReleaseCpuState(); }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }).Task.Unwrap();

        private static void WriteSolidDds(string path)
        {
            uint[] header = { 0x20534444, 124, 0x100F, 1, 1, 4, 0, 0,
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 32, 0x41, 0, 32,
                0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000, 0x1000, 0, 0, 0, 0 };
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream);
            foreach (uint value in header) writer.Write(value);
            writer.Write(0xFF00FF00u);
        }

        [Fact]
        public async Task LoadFamiliesAsync_GroupsChromasUnderNearestPrimarySkin()
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                $"assetsmanager-chroma-groups-{Guid.NewGuid():N}");

            try
            {
                CreateSkin(root, "base", hasModel: true);
                CreateSkin(root, "skin01", hasModel: false);
                CreateSkin(root, "skin02", hasModel: true);
                CreateSkin(root, "skin03", hasModel: false);
                CreateSkin(root, "skin04", hasModel: false);

                using var logger = new LoggerConfiguration().CreateLogger();
                var loader = new ChromaLoadingService(new LogService(logger));

                var families = await loader.LoadFamiliesAsync(root);

                Assert.Collection(
                    families,
                    family => AssertFamily(family, "BASE", "skin01"),
                    family => AssertFamily(family, "SKIN02", "skin03", "skin04"));
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }

        [Theory]
        [InlineData(false, "skin02")]
        [InlineData(true, "skin02")]
        [InlineData(false, "skin05")]
        [InlineData(true, "skin05")]
        public async Task LoadFamiliesAsync_PreservesFolderFamilyWhileUsingDeclaredMesh(bool hashedMesh, string parent)
        {
            string project = Path.Combine(Path.GetTempPath(), $"assetsmanager-chroma-binding-{Guid.NewGuid():N}");
            string root = Path.Combine(project, "assets", "characters", "test", "skins");
            try
            {
                CreateSkin(root, "skin02", hasModel: true);
                CreateSkin(root, "skin03", hasModel: true);
                CreateSkin(root, "skin04", hasModel: false);
                CreateSkin(root, "skin05", hasModel: true);
                WriteChromaBin(project, parent, hashedMesh);
                using var logger = new LoggerConfiguration().CreateLogger();
                var loader = new ChromaLoadingService(new LogService(logger));

                var families = await loader.LoadFamiliesAsync(root);

                ChromaFamilyModel family = Assert.Single(families);
                Assert.Equal("SKIN03", family.Name);
                Assert.Equal(Path.Combine(root, "skin03", "skin03.skn"), family.ModelPath);
                ChromaSkinModel chroma = Assert.Single(family.Chromas);
                Assert.Equal("SKIN04", chroma.Name);
                Assert.Equal(Path.Combine(root, parent, $"{parent}.skn"), chroma.ModelPath);
                Assert.Equal(Path.Combine(root, "skin04"), chroma.TexturePath);
            }
            finally
            {
                if (Directory.Exists(project))
                    Directory.Delete(project, true);
            }
        }

        [Fact]
        public async Task LoadFamiliesAsync_DoesNotSubstituteAnotherMeshWhenDeclaredMeshIsMissing()
        {
            string project = Path.Combine(Path.GetTempPath(), $"assetsmanager-chroma-missing-{Guid.NewGuid():N}");
            string root = Path.Combine(project, "assets", "characters", "test", "skins");
            try
            {
                CreateSkin(root, "skin03", hasModel: true);
                CreateSkin(root, "skin04", hasModel: false);
                WriteChromaBin(project, "skin02", false);
                using var logger = new LoggerConfiguration().CreateLogger();
                var loader = new ChromaLoadingService(new LogService(logger));

                Assert.Empty(await loader.LoadFamiliesAsync(root));
            }
            finally
            {
                if (Directory.Exists(project))
                    Directory.Delete(project, true);
            }
        }

        private static void WriteChromaBin(string project, string parent, bool hashedMesh)
        {
            string virtualPath = $"assets/characters/test/skins/{parent}/{parent}.skn";
            uint field = Fnv1a.HashLower("simpleSkin");
            BinTreeProperty model = hashedMesh
                ? new BinTreeWadChunkLink(field, XxHash64Ext.Hash(virtualPath))
                : new BinTreeString(field, virtualPath.ToUpperInvariant());
            var mesh = new BinTreeStruct(
                Fnv1a.HashLower("skinMeshProperties"),
                Fnv1a.HashLower("SkinMeshDataProperties"),
                new[] { model });
            var skin = new BinTreeObject("Characters/Test/Skins/Skin4", "SkinCharacterDataProperties",
                new BinTreeProperty[] { mesh });
            var tree = new BinTree(new[] { skin }, Array.Empty<string>());
            string path = Path.Combine(project, "data", "characters", "test", "skins", "skin4.bin");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = File.Create(path);
            tree.Write(stream);
        }

        private static void AssertFamily(
            ChromaFamilyModel family,
            string expectedName,
            params string[] expectedChromas)
        {
            Assert.Equal(expectedName, family.Name);
            Assert.Equal(expectedChromas.Length, family.ChromaCount);
            Assert.Equal(
                expectedChromas,
                System.Linq.Enumerable.Select(family.Chromas, chroma => chroma.Name.ToLowerInvariant()));
            Assert.All(family.Chromas, chroma => Assert.Equal(family.ModelPath, chroma.ModelPath));
        }

        private static void CreateSkin(string root, string name, bool hasModel)
        {
            string directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, $"{name}_tx_cm.tex"), Array.Empty<byte>());
            if (hasModel)
                File.WriteAllBytes(Path.Combine(directory, $"{name}.skn"), Array.Empty<byte>());
        }
    }
}
