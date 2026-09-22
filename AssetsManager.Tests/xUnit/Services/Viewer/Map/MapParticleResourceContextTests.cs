using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class MapParticleResourceContextTests
    {
        [Fact]
        public void ReferenceForSeparatesWadHashesFromVirtualPaths()
        {
            MapAssetReference hashed = MapParticleResourceContext.ReferenceFor("0123456789abcdef.dds");
            MapAssetReference path = MapParticleResourceContext.ReferenceFor("assets/maps/test/fire.dds");
            MapAssetReference truncated = MapParticleResourceContext.ReferenceFor("89abcdef.dds");

            Assert.Null(hashed.VirtualPath);
            Assert.Equal(0x0123456789abcdefUL, hashed.PathHash);
            Assert.Equal("assets/maps/test/fire.dds", path.VirtualPath);
            Assert.Equal(0UL, path.PathHash);
            Assert.Equal("89abcdef.dds", truncated.VirtualPath);
            Assert.Equal(0UL, truncated.PathHash);
        }

        [Fact]
        public void CollectRequestsMergesResourceFamiliesForTheSameAuthoredAsset()
        {
            const string asset = "0123456789abcdef";
            VfxEmitterDefinition emitter = Emitter(asset, asset);
            MapParticleSystemCatalog catalog = Catalog(emitter);

            MapParticleResourceContext.ResourceRequest request =
                Assert.Single(MapParticleResourceContext.CollectRequests(catalog));

            Assert.Equal(asset, request.AuthoredPath);
            Assert.Contains(".tex", request.Extensions, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(".dds", request.Extensions, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(".scb", request.Extensions, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(".skn", request.Extensions, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(".tmesh", request.Extensions, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(".gmesh", request.Extensions, StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task MaterializationUsesExactExplicitExtensionAndPreservesVirtualPath()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerTests", Guid.NewGuid().ToString("N"));
            string effects = Path.Combine(root, "assets", "maps", "test");
            Directory.CreateDirectory(effects);
            string exact = Path.Combine(effects, "fire.tex");
            string wrongFallback = Path.Combine(effects, "smoke.dds");
            await File.WriteAllBytesAsync(exact, new byte[] { 1, 2, 3, 4 });
            await File.WriteAllBytesAsync(wrongFallback, new byte[] { 5, 6, 7, 8 });

            try
            {
                VfxEmitterDefinition first = Emitter("assets/maps/test/fire.tex", null);
                VfxEmitterDefinition second = Emitter("assets/maps/test/smoke.tex", null);
                MapParticleSystemCatalog catalog = Catalog(first, second);
                var resolver = new MapAssetResolver(null, null);

                using MapParticleResourceContext context = await MapParticleResourceContext.CreateAsync(
                    catalog,
                    root,
                    resolver,
                    hashResolver: null,
                    logService: null);

                Assert.True(File.Exists(Path.Combine(
                    context.SearchDirectory,
                    "assets",
                    "maps",
                    "test",
                    "fire.tex")));
                Assert.False(File.Exists(Path.Combine(
                    context.SearchDirectory,
                    "assets",
                    "maps",
                    "test",
                    "smoke.dds")));
                Assert.False(File.Exists(Path.Combine(
                    context.SearchDirectory,
                    "assets",
                    "maps",
                    "test",
                    "smoke.tex")));
            }
            finally
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }

        private static MapParticleSystemCatalog Catalog(params VfxEmitterDefinition[] emitters)
        {
            const uint hash = 0x30000001;
            var system = new VfxSystemDefinition(
                hash,
                "MapVfx",
                "Maps/Test/MapVfx",
                emitters ?? Array.Empty<VfxEmitterDefinition>());
            return new MapParticleSystemCatalog(
                new Dictionary<uint, VfxSystemDefinition> { [hash] = system },
                new Dictionary<uint, uint>(),
                Array.Empty<MapParticleSystemGroupData>());
        }

        private static VfxEmitterDefinition Emitter(string texturePath, string meshPath) =>
            new(
                Name: "Emitter",
                Rate: VfxCurveF.Const(1f),
                ParticleLifetime: VfxCurveF.Const(1f),
                EmitterLifetime: null,
                ParticleLinger: 0f,
                TimeBeforeFirstEmission: 0f,
                IsSingleParticle: false,
                Disabled: false,
                BlendMode: 4,
                BirthScale: VfxCurve3.Const(Vector3.One),
                ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(Vector4.One),
                ColorOverLife: null,
                BirthVelocity: null,
                Acceleration: null,
                BirthRotationalVelocity: null,
                EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                TexturePath: texturePath ?? string.Empty,
                TexDiv: Vector2.One,
                NumFrames: 1,
                RandomStartFrame: false,
                IsMeshPrimitive: !string.IsNullOrWhiteSpace(meshPath),
                MeshPath: meshPath);
    }
}
