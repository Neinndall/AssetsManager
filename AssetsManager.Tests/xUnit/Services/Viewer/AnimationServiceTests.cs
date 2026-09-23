using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Animation;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Animation.Builders;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer
{
    public sealed class AnimationServiceTests
    {
        [Fact]
        public void CreateBindBoneTransformProvider_ComposesHierarchyAndResolvesBothGameHashes()
        {
            Matrix4x4 rootLocal = Matrix4x4.CreateTranslation(1f, 2f, 3f);
            Matrix4x4 handLocal = Matrix4x4.CreateTranslation(4f, 5f, 6f);
            Matrix4x4 handWorld = handLocal * rootLocal;

            var builder = new RigResourceBuilder();
            JointBuilder root = builder.CreateJoint("Root")
                .WithLocalTransform(rootLocal)
                .WithInverseBindTransform(Inverse(rootLocal));
            root.CreateJoint("Hand")
                .WithLocalTransform(handLocal)
                .WithInverseBindTransform(Inverse(handWorld));
            RigResource skeleton = builder.Build();

            Func<string, uint, Matrix4x4?> provider = AnimationService.CreateBindBoneTransformProvider(skeleton);

            Assert.NotNull(provider);
            AssertMatrixNear(handWorld, provider("hand", 0).Value);
            AssertMatrixNear(handWorld, provider(null, Fnv1a.HashLower("Hand")).Value);
            AssertMatrixNear(handWorld, provider(null, Elf.HashLower("Hand")).Value);
            Assert.Null(provider("missing", 0));
        }

        [Fact]
        public void CreateBindSkinningMatrices_UsesJointSlotsAndProducesBindPosePalette()
        {
            Matrix4x4 rootLocal = Matrix4x4.CreateTranslation(3f, -2f, 1f);
            Matrix4x4 childLocal = Matrix4x4.CreateRotationZ(0.35f) * Matrix4x4.CreateTranslation(2f, 1f, 0f);
            Matrix4x4 rootWorld = rootLocal;
            Matrix4x4 childWorld = childLocal * rootWorld;

            var builder = new RigResourceBuilder();
            JointBuilder root = builder.CreateJoint("Root")
                .WithLocalTransform(rootLocal)
                .WithInverseBindTransform(Inverse(rootWorld));
            root.CreateJoint("Child")
                .WithLocalTransform(childLocal)
                .WithInverseBindTransform(Inverse(childWorld));
            RigResource skeleton = builder.Build();

            Matrix4x4[] palette = AnimationService.CreateBindSkinningMatrices(skeleton);

            Assert.Equal(skeleton.Joints.Count, palette.Length);
            AssertMatrixNear(Matrix4x4.Identity, palette[0]);
            AssertMatrixNear(Matrix4x4.Identity, palette[1]);
        }

        private static Matrix4x4 Inverse(Matrix4x4 matrix)
        {
            Assert.True(Matrix4x4.Invert(matrix, out Matrix4x4 inverse));
            return inverse;
        }

        private static void AssertMatrixNear(Matrix4x4 expected, Matrix4x4 actual, float epsilon = 0.0001f)
        {
            Assert.InRange(MathF.Abs(expected.M11 - actual.M11), 0f, epsilon);
            Assert.InRange(MathF.Abs(expected.M12 - actual.M12), 0f, epsilon);
            Assert.InRange(MathF.Abs(expected.M13 - actual.M13), 0f, epsilon);
            Assert.InRange(MathF.Abs(expected.M14 - actual.M14), 0f, epsilon);
            Assert.InRange(MathF.Abs(expected.M21 - actual.M21), 0f, epsilon);
            Assert.InRange(MathF.Abs(expected.M22 - actual.M22), 0f, epsilon);
            Assert.InRange(MathF.Abs(expected.M23 - actual.M23), 0f, epsilon);
            Assert.InRange(MathF.Abs(expected.M24 - actual.M24), 0f, epsilon);
            Assert.InRange(MathF.Abs(expected.M31 - actual.M31), 0f, epsilon);
            Assert.InRange(MathF.Abs(expected.M32 - actual.M32), 0f, epsilon);
            Assert.InRange(MathF.Abs(expected.M33 - actual.M33), 0f, epsilon);
            Assert.InRange(MathF.Abs(expected.M34 - actual.M34), 0f, epsilon);
            Assert.InRange(MathF.Abs(expected.M41 - actual.M41), 0f, epsilon);
            Assert.InRange(MathF.Abs(expected.M42 - actual.M42), 0f, epsilon);
            Assert.InRange(MathF.Abs(expected.M43 - actual.M43), 0f, epsilon);
            Assert.InRange(MathF.Abs(expected.M44 - actual.M44), 0f, epsilon);
        }
    }
}
