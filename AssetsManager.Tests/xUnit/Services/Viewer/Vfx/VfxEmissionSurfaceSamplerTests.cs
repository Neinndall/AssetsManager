using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx
{
    public sealed class VfxEmissionSurfaceSamplerTests
    {
        [Fact]
        public void MeshSurfaceUsesLtkTriangleBarycentricDrawOrder()
        {
            var mesh = TriangleMesh();
            var sampler = new VfxMeshEmissionSurfaceSampler(mesh, null, 1.5f, 4);
            var expectedRng = new VfxLtkRandom(0x12345678u);
            _ = expectedRng.NextUnitFloat(); // triangle selection consumes one draw even for one triangle
            float root = MathF.Sqrt(expectedRng.NextUnitFloat());
            float along = expectedRng.NextUnitFloat();
            Vector3 expected = (
                new Vector3(2f, 0f, 0f) * (root * (1f - along)) +
                new Vector3(0f, 2f, 0f) * (root * along)) * 1.5f;

            Assert.True(sampler.TrySample(0f, new VfxLtkRandom(0x12345678u), out VfxSurfaceBirth birth));

            AssertVector(expected, birth.Position);
            AssertVector(Vector3.UnitZ, birth.Normal);
        }

        [Fact]
        public void MeshSurfaceSkinsVerticesBeforeSampling()
        {
            var mesh = TriangleMesh();
            var pose = new FakePose(
                boneIndices: new float[]
                {
                    0, 0, 0, 0,
                    0, 0, 0, 0,
                    0, 0, 0, 0
                },
                boneWeights: new float[]
                {
                    1, 0, 0, 0,
                    1, 0, 0, 0,
                    1, 0, 0, 0
                },
                palette: new[] { Matrix4x4.CreateTranslation(5f, -2f, 3f) },
                jointHashes: Array.Empty<uint>(),
                parents: Array.Empty<int>(),
                jointPositions: Array.Empty<Vector3>());
            var staticSampler = new VfxMeshEmissionSurfaceSampler(mesh, null, 1f, 4);
            var skinnedSampler = new VfxMeshEmissionSurfaceSampler(mesh, pose, 1f, 4);

            Assert.True(staticSampler.TrySample(0.3f, new VfxLtkRandom(77u), out VfxSurfaceBirth plain));
            Assert.True(skinnedSampler.TrySample(0.3f, new VfxLtkRandom(77u), out VfxSurfaceBirth skinned));

            AssertVector(plain.Position + new Vector3(5f, -2f, 3f), skinned.Position);
            AssertVector(plain.Normal, skinned.Normal);
        }

        [Fact]
        public void SkeletonSurfaceAppliesJointMaskAndSamplesAlongSelectedBone()
        {
            const uint rootHash = 0x11111111u;
            const uint shortHash = 0x22222222u;
            const uint longHash = 0x33333333u;
            var pose = new FakePose(
                boneIndices: Array.Empty<float>(),
                boneWeights: Array.Empty<float>(),
                palette: Array.Empty<Matrix4x4>(),
                jointHashes: new[] { rootHash, shortHash, longHash },
                parents: new[] { -1, 0, 0 },
                jointPositions: new[]
                {
                    Vector3.Zero,
                    new Vector3(2f, 0f, 0f),
                    new Vector3(0f, 6f, 0f)
                });
            var sampler = new VfxSkeletonEmissionSurfaceSampler(pose, new[] { longHash }, 2f);
            var expectedRng = new VfxLtkRandom(19u);
            _ = expectedRng.NextUnitFloat(); // length-weighted segment choice
            float along = expectedRng.NextUnitFloat();

            Assert.True(sampler.TrySample(1.25f, new VfxLtkRandom(19u), out VfxSurfaceBirth birth));

            AssertVector(new Vector3(0f, 12f * along, 0f), birth.Position);
            AssertVector(Vector3.UnitY, birth.Normal);
            Assert.Equal(1.25f, pose.LastEvaluatedTime, precision: 5);
        }

        private static VfxMeshData TriangleMesh()
            => new(
                new float[]
                {
                    0f, 0f, 0f,
                    2f, 0f, 0f,
                    0f, 2f, 0f
                },
                new float[]
                {
                    0f, 0f, 1f,
                    0f, 0f, 1f,
                    0f, 0f, 1f
                },
                new float[] { 0f, 0f, 1f, 0f, 0f, 1f },
                new float[]
                {
                    1f, 1f, 1f, 1f,
                    1f, 1f, 1f, 1f,
                    1f, 1f, 1f, 1f
                },
                new uint[] { 0, 1, 2 });

        private static void AssertVector(Vector3 expected, Vector3 actual)
        {
            Assert.Equal(expected.X, actual.X, precision: 5);
            Assert.Equal(expected.Y, actual.Y, precision: 5);
            Assert.Equal(expected.Z, actual.Z, precision: 5);
        }

        private sealed class FakePose : IVfxEmissionSurfacePose
        {
            private readonly Matrix4x4[] _palette;
            private readonly uint[] _jointHashes;
            private readonly int[] _parents;
            private readonly Vector3[] _jointPositions;

            public FakePose(
                float[] boneIndices,
                float[] boneWeights,
                Matrix4x4[] palette,
                uint[] jointHashes,
                int[] parents,
                Vector3[] jointPositions)
            {
                BoneIndices = boneIndices;
                BoneWeights = boneWeights;
                _palette = palette;
                _jointHashes = jointHashes;
                _parents = parents;
                _jointPositions = jointPositions;
            }

            public int JointCount => _jointHashes.Length;
            public float[] BoneIndices { get; }
            public float[] BoneWeights { get; }
            public float LastEvaluatedTime { get; private set; }

            public uint JointHashAt(int index) => _jointHashes[index];
            public int ParentIndexAt(int index) => _parents[index];
            public ReadOnlySpan<Matrix4x4> EvaluatePalette(float seconds) => _palette;

            public void EvaluateJointPositions(float seconds, Span<Vector3> positions)
            {
                LastEvaluatedTime = seconds;
                _jointPositions.CopyTo(positions);
            }
        }
    }
}
