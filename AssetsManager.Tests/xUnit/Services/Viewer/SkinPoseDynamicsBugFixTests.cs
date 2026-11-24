using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Animation.Builders;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer
{
    public sealed class SkinPoseDynamicsBugFixTests
    {
        [Fact]
        public void MirroredCharacterPoseRootTrailsWorldXMovement()
        {
            using var model = new SceneModel { PositionX = 0 };
            var spring = new SkinSpringRuntime(new SkinSpringDefinition(1, 2, Stiffness: 0f, Damping: 0f, DoTranslation: true));

            Matrix4x4 startRoot = AnimationService.CreatePoseRootTransform(model, mirrorCharacterX: true);
            spring.Step(startRoot, 1f / 60f);
            spring.Step(startRoot, 1f / 60f);

            model.PositionX = 10;
            Matrix4x4 movedRoot = AnimationService.CreatePoseRootTransform(model, mirrorCharacterX: true);
            spring.Step(movedRoot, 1f / 60f);

            // Bone-local offset +10 becomes world -10 after mirrorCharacterX (-scaleX), trailing behind +10 movement.
            Matrix4x4 local = spring.Apply(Matrix4x4.Identity, Matrix4x4.Identity, 1f);
            Matrix4x4 renderedWorld = GlMeshRenderer.CreateWorldMatrix(model, mirrorCharacterX: true);
            Vector3 worldTip = Vector3.Transform(local.Translation, renderedWorld);
            Assert.Equal(0f, worldTip.X, 3);
        }

        [Fact]
        public void LoopBoundaryWrapPreservesSpringMomentumWhileBackwardsSeekResets()
        {
            var definition = new SkinPoseDefinition
            {
                Springs = new[] { new SkinSpringDefinition(1, 2, Stiffness: 0f, Damping: 0f, DoTranslation: true) }
            };
            var runtime = new SkinPoseRuntime(definition, new[] { -1, 0 }, new[] { 0, 1 }, hash => hash == 2 ? 1 : 0);

            runtime.Advance(0.92f, Matrix4x4.Identity, 1f / 60f, duration: 1.0f);
            runtime.Advance(0.95f, Matrix4x4.Identity, 1f / 60f, duration: 1.0f);
            runtime.Advance(0.98f, Matrix4x4.CreateTranslation(5f, 0f, 0f), 1f / 60f, duration: 1.0f);

            // Natural forward loop wrap from 0.98s -> 0.02s on a 1.0s clip must keep spring offset intact.
            runtime.Advance(0.02f, Matrix4x4.CreateTranslation(5f, 0f, 0f), 1f / 60f, duration: 1.0f);
            var locals = new[] { Matrix4x4.Identity, Matrix4x4.Identity };
            runtime.ApplyPost(0.02f, locals, new Matrix4x4[2]);
            Assert.True(MathF.Abs(locals[1].Translation.X) > 4f, $"Expected spring momentum preserved across loop boundary, got X={locals[1].Translation.X}.");

            // Explicit backwards seek (0.60s -> 0.20s) must reset live spring state.
            runtime.Advance(0.20f, Matrix4x4.CreateTranslation(5f, 0f, 0f), 1f / 60f, duration: 1.0f);
            runtime.Advance(0.60f, Matrix4x4.CreateTranslation(10f, 0f, 0f), 1f / 60f, duration: 1.0f);
            runtime.Advance(0.20f, Matrix4x4.CreateTranslation(10f, 0f, 0f), 1f / 60f, duration: 1.0f);
            locals = new[] { Matrix4x4.Identity, Matrix4x4.Identity };
            runtime.ApplyPost(0.20f, locals, new Matrix4x4[2]);
            Assert.Equal(0f, locals[1].Translation.X, 3);
        }

        [Fact]
        public void PausedUpdateFreezesLiveSpringDynamics()
        {
            uint hairHash = Fnv1a.HashLower("Hair");
            var definition = new SkinPoseDefinition
            {
                Springs = new[] { new SkinSpringDefinition(1, hairHash, Stiffness: 0f, Damping: 0f, DoTranslation: true) }
            };
            using var service = new AnimationService();
            using var anim = new StaticTestAnimation(1.0f);
            using var model = new SceneModel { PoseDefinition = definition };
            var rig = CreateTwoBoneRig();
            var skin = (SkinnedMesh)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SkinnedMesh));

            service.Update(0.1f, anim, rig, skin, Array.Empty<ModelPart>(), "test", model, deltaSeconds: 1f / 60f, mirrorCharacterX: true, advanceDynamics: true);
            service.Update(0.12f, anim, rig, skin, Array.Empty<ModelPart>(), "test", model, deltaSeconds: 1f / 60f, mirrorCharacterX: true, advanceDynamics: true);
            Vector3 beforePause = service.WorldBoneTransforms[1].Translation;

            // While paused, moving the model must not step live spring dynamics.
            model.PositionX = 25;
            service.Update(0.12f, anim, rig, skin, Array.Empty<ModelPart>(), "test", model, deltaSeconds: 1f / 60f, mirrorCharacterX: true, advanceDynamics: false);
            Vector3 afterPausedMove = service.WorldBoneTransforms[1].Translation;

            Assert.Equal(beforePause.X, afterPausedMove.X, 3);
        }

        private static RigResource CreateTwoBoneRig()
        {
            var builder = new RigResourceBuilder();
            JointBuilder root = builder.CreateJoint("Root")
                .WithLocalTransform(Matrix4x4.Identity)
                .WithInverseBindTransform(Matrix4x4.Identity);
            root.CreateJoint("Hair")
                .WithLocalTransform(Matrix4x4.Identity)
                .WithInverseBindTransform(Matrix4x4.Identity);
            return builder.Build();
        }

        private sealed class StaticTestAnimation : IAnimationAsset
        {
            public StaticTestAnimation(float duration) => Duration = duration;
            public float Duration { get; }
            public float Fps => 30f;
            public bool IsDisposed => false;

            public void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
            {
                pose[Elf.HashLower("Root")] = (Quaternion.Identity, Vector3.Zero, Vector3.One);
                pose[Elf.HashLower("Hair")] = (Quaternion.Identity, Vector3.Zero, Vector3.One);
            }

            public void Dispose() { }
        }
    }
}
