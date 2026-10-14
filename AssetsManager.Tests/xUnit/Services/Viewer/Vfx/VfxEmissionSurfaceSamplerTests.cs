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
            var sampler = new VfxMeshEmissionSurfaceSampler(mesh, null, 4);
            var expectedRng = new VfxLtkRandom(0x12345678u);
            _ = expectedRng.NextUnitFloat(); // triangle selection consumes one draw even for one triangle
            float root = MathF.Sqrt(expectedRng.NextUnitFloat());
            float along = expectedRng.NextUnitFloat();
            Vector3 expected = (
                new Vector3(2f, 0f, 0f) * (root * (1f - along)) +
                new Vector3(0f, 2f, 0f) * (root * along));

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
            var staticSampler = new VfxMeshEmissionSurfaceSampler(mesh, null, 4);
            var skinnedSampler = new VfxMeshEmissionSurfaceSampler(mesh, pose, 4);

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
            var sampler = new VfxSkeletonEmissionSurfaceSampler(pose, new[] { longHash });
            var expectedRng = new VfxLtkRandom(19u);
            _ = expectedRng.NextUnitFloat(); // length-weighted segment choice
            float along = expectedRng.NextUnitFloat();

            Assert.True(sampler.TrySample(1.25f, new VfxLtkRandom(19u), out VfxSurfaceBirth birth));

            AssertVector(new Vector3(0f, 6f * along, 0f), birth.Position);
            float angle = expectedRng.NextUnitFloat() * MathF.Tau;
            AssertVector(new Vector3(MathF.Sin(angle), 0f, MathF.Cos(angle)), birth.Normal);
            Assert.Equal(1.25f, pose.LastEvaluatedTime, precision: 5);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MeshNormalsBlendBeforeNormalizingAndSkinWithThePose(bool skinned)
        {
            var mesh = TriangleMesh() with { Normals = new float[] { 1,0,0, 0,1,0, 0,0,1 } };
            Matrix4x4 transform = Matrix4x4.CreateScale(2f, 3f, 4f) * Matrix4x4.CreateRotationZ(.7f);
            var pose = new FakePose(new float[12], new float[] { 1,0,0,0, 1,0,0,0, 1,0,0,0 },
                new[] { transform }, Array.Empty<uint>(), Array.Empty<int>(), Array.Empty<Vector3>());
            var expectedRng = new VfxLtkRandom(77);
            _ = expectedRng.NextUnitFloat();
            float root = MathF.Sqrt(expectedRng.NextUnitFloat());
            float along = expectedRng.NextUnitFloat();
            Vector3 Direction(Vector3 value) => skinned
                ? Vector3.Normalize(Vector3.TransformNormal(value, transform)) : value;
            Vector3 expected = Vector3.Normalize(Direction(Vector3.UnitX) * (1f - root) +
                Direction(Vector3.UnitY) * root * (1f - along) + Direction(Vector3.UnitZ) * root * along);
            var sampler = new VfxMeshEmissionSurfaceSampler(mesh, skinned ? pose : null, 0);
            var random = new VfxLtkRandom(77);
            Assert.True(sampler.TrySample(.25f, random, out var birth));
            AssertVector(expected, birth.Normal);
            Assert.Equal(expectedRng.State, random.State);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MeshUsesTheFaceNormalWhenVertexNormalsAreAbsentOrZero(bool absent)
        {
            var mesh = TriangleMesh() with { Normals = absent ? Array.Empty<float>() : new float[9] };
            Assert.True(new VfxMeshEmissionSurfaceSampler(mesh, null, 4)
                .TrySample(0f, new VfxLtkRandom(77), out var birth));
            AssertVector(Vector3.UnitZ, birth.Normal);
        }

        [Fact]
        public void SkeletonSelectsByRestLengthsWhilePlacingAlongPosedBones()
        {
            var pose = new FakePose(Array.Empty<float>(), Array.Empty<float>(), Array.Empty<Matrix4x4>(),
                new uint[] { 1,2,3 }, new[] { -1,0,0 },
                new[] { Vector3.Zero, new Vector3(100,0,0), new Vector3(0,4,0) },
                new[] { Vector3.Zero, new Vector3(1,0,0), new Vector3(0,4,0) });
            var sampler = new VfxSkeletonEmissionSurfaceSampler(pose, Array.Empty<uint>());
            uint seed = 1;
            while (new VfxLtkRandom(seed).NextUnitFloat() is var pick && (pick <= .2f || pick >= .9f)) seed++;
            var expected = new VfxLtkRandom(seed);
            _ = expected.NextUnitFloat();
            float along = expected.NextUnitFloat();
            _ = expected.NextUnitFloat();
            var random = new VfxLtkRandom(seed);
            Assert.True(sampler.TrySample(.5f, random, out var birth));
            AssertVector(new Vector3(0, 4 * along, 0), birth.Position);
            Assert.Equal(0f, Vector3.Dot(Vector3.UnitY, birth.Normal), 5);
            Assert.Equal(1f, birth.Normal.Length(), 5);
            Assert.Equal(expected.State, random.State);
        }

        [Fact]
        public void SkeletonWithNoRestLengthDoesNotDrawEvenIfItsPoseHasLength()
        {
            var pose = new FakePose(Array.Empty<float>(), Array.Empty<float>(), Array.Empty<Matrix4x4>(),
                new uint[] { 1,2 }, new[] { -1,0 }, new[] { Vector3.Zero, Vector3.UnitX },
                new[] { Vector3.Zero, Vector3.Zero });
            var random = new VfxLtkRandom(99);
            uint state = random.State;
            Assert.False(new VfxSkeletonEmissionSurfaceSampler(pose, Array.Empty<uint>())
                .TrySample(1f, random, out _));
            Assert.Equal(state, random.State);
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
            private readonly Vector3[] _restPositions;

            public FakePose(
                float[] boneIndices,
                float[] boneWeights,
                Matrix4x4[] palette,
                uint[] jointHashes,
                int[] parents,
                Vector3[] jointPositions, Vector3[] restPositions = null)
            {
                BoneIndices = boneIndices;
                BoneWeights = boneWeights;
                _palette = palette;
                _jointHashes = jointHashes;
                _parents = parents;
                _jointPositions = jointPositions;
                _restPositions = restPositions ?? jointPositions;
            }

            public int JointCount => _jointHashes.Length;
            public float[] BoneIndices { get; }
            public float[] BoneWeights { get; }
            public float LastEvaluatedTime { get; private set; }

            public uint JointHashAt(int index) => _jointHashes[index];
            public int ParentIndexAt(int index) => _parents[index];
            public ReadOnlySpan<Matrix4x4> EvaluatePalette(float seconds) => _palette;

            public void EvaluateRestJointPositions(Span<Vector3> positions) => _restPositions.CopyTo(positions);

            public void EvaluateJointPositions(float seconds, Span<Vector3> positions)
            {
                LastEvaluatedTime = seconds;
                _jointPositions.CopyTo(positions);
            }
        }
    }
}
