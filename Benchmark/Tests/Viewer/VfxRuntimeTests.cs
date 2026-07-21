using System;
using System.IO;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer
{
    public sealed class VfxRuntimeTests
    {
        [Fact]
        public void MeshInstancesPreserveAuthoredNonUniformScale()
        {
            var emitter = CreateEmitter(new Vector3(2f, 3f, 4f), VfxEmitterRenderState.Default);
            var system = new VfxSystemDefinition(1, "test", "test", new[] { emitter });
            var simulator = new VfxPlaybackRuntime(7);

            simulator.SetSystem(system, Vector3.Zero);
            simulator.Update(0.02f);

            var state = Assert.Single(simulator.Emitters);
            Assert.Equal(1, state.InstanceCount);
            Assert.Equal(2f, state.Instances[3]);
            Assert.Equal(3f, state.Instances[4]);
            Assert.Equal(4f, state.Instances[18]);
        }

        [Fact]
        public void RenderStateNormalizesAlphaReference()
        {
            var state = new VfxEmitterRenderState(3, 128, 1, true, true, false, true);

            Assert.Equal(3, state.RenderPass);
            Assert.InRange(state.AlphaCutoff, 0.5019f, 0.5020f);
            Assert.True(state.ClampUvScroll);
            Assert.True(state.FlipU);
            Assert.True(state.DisableBackfaceCull);
        }

        [Fact]
        public void AssetIndexPrefersAuthoredPathOverSameNamedFallback()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxIndex", Guid.NewGuid().ToString("N"));
            string skin0 = Path.Combine(root, "assets", "characters", "hero", "skins", "skin0", "particles");
            string skin1 = Path.Combine(root, "assets", "characters", "hero", "skins", "skin1", "particles");
            Directory.CreateDirectory(skin0);
            Directory.CreateDirectory(skin1);
            string expected = Path.Combine(skin1, "shared.tex");
            File.WriteAllBytes(Path.Combine(skin0, "shared.tex"), new byte[] { 0 });
            File.WriteAllBytes(expected, new byte[] { 1 });

            try
            {
                var index = VfxResourceIndex.Build(root);
                string resolved = index.Resolve(
                    "assets/characters/hero/skins/skin1/particles/shared.tex",
                    new[] { ".tex" });

                Assert.Equal(Path.GetFullPath(expected), resolved);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void AssetIndexResolvesExtractorTruncatedBinNameInTheAuthoredDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssetsManagerVfxTruncatedBin", Guid.NewGuid().ToString("N"));
            string directory = Path.Combine(root, "data", "characters", "hero");
            Directory.CreateDirectory(directory);
            string extractedStem = new string('a', 236);
            string extracted = Path.Combine(directory, extractedStem + ".bin");
            File.WriteAllBytes(extracted, new byte[] { 1 });

            try
            {
                var index = VfxResourceIndex.Build(root);
                string resolved = index.Resolve(
                    $"DATA/Characters/Hero/{extractedStem}_skins_skin28.bin",
                    new[] { ".bin" });

                Assert.Equal(Path.GetFullPath(extracted), resolved);
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static VfxEmitterDefinition CreateEmitter(Vector3 birthScale, VfxEmitterRenderState renderState)
            => new(
                Name: "mesh",
                Rate: VfxCurveF.Const(1f),
                ParticleLifetime: VfxCurveF.Const(1f),
                EmitterLifetime: null,
                ParticleLinger: 0f,
                TimeBeforeFirstEmission: 0f,
                IsSingleParticle: true,
                Disabled: false,
                BlendMode: 2,
                BirthScale: VfxCurve3.Const(birthScale),
                ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(Vector4.One),
                ColorOverLife: null,
                BirthVelocity: null,
                Acceleration: null,
                BirthRotationalVelocity: null,
                EmitterPosition: Vector3.Zero,
                TexturePath: "mesh.tex",
                TexDiv: Vector2.One,
                NumFrames: 1,
                RandomStartFrame: false,
                IsMeshPrimitive: true,
                RenderState: renderState);
    }
}
