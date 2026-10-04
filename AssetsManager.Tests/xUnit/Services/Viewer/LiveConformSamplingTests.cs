using System;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Views.Models.Viewer;
using CommunityToolkit.HighPerformance.Buffers;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Animation.Builders;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer;

public sealed class LiveConformSamplingTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void AttachmentSamplingPreservesTheVisiblePoseAndNextFrame(int sampleKind, bool hasSpring)
    {
        uint Hash(string name) => Fnv1a.HashLower(name);
        var builder = new RigResourceBuilder();
        builder.CreateJoint("Root").WithLocalTransform(Matrix4x4.Identity).WithInverseBindTransform(Matrix4x4.Identity)
            .CreateJoint("Hand").WithLocalTransform(Matrix4x4.CreateTranslation(0, 0, 10))
            .WithInverseBindTransform(Matrix4x4.CreateTranslation(0, 0, -10))
            .CreateJoint("Tip").WithLocalTransform(Matrix4x4.CreateTranslation(0, 0, 10))
            .WithInverseBindTransform(Matrix4x4.CreateTranslation(0, 0, -20));
        RigResource rig = builder.Build();
        using var skin = CreateSkin();
        using var animation = new MovingAnimation();
        using var sampled = new AnimationService();
        using var control = new AnimationService();
        var model = new SceneModel
        {
            PoseDefinition = new SkinPoseDefinition
            {
                Conforms = new[] { new SkinConformDefinition(Hash("Hand"), Hash("Tip"), 0, MaxAngle: 180) },
                Springs = hasSpring ? new[] { new SkinSpringDefinition(Hash("spring"), Hash("Hand")) }
                    : Array.Empty<SkinSpringDefinition>()
            }
        };
        void Update(AnimationService evaluator, int frame)
        {
            model.PositionX = frame * 0.02f;
            evaluator.Update(frame / 60f, animation, rig, skin, Array.Empty<ModelPart>(), "test", model, 1f / 60f);
        }
        for (int frame = 0; frame < 8; frame++)
        {
            Update(sampled, frame);
            Update(control, frame);
        }
        Matrix4x4[] visible = sampled.WorldBoneTransforms.ToArray();
        Matrix4x4[] palette = sampled.FinalBoneTransforms.ToArray();
        Assert.True(sampleKind switch
        {
            0 => sampled.TrySampleBoneTransform(0.2f, "Tip", Hash("Tip"), out _),
            1 => sampled.TrySampleRootTransform(0.2f, out _),
            _ => sampled.TrySampleBoneTransformFnv(0.2f, null, Hash("Tip"), out _)
        });
        Assert.Equal(visible, sampled.WorldBoneTransforms.ToArray());
        Assert.Equal(palette, sampled.FinalBoneTransforms);
        Update(sampled, 8);
        Update(control, 8);
        Assert.Equal(control.WorldBoneTransforms.ToArray(), sampled.WorldBoneTransforms.ToArray());
        Assert.Equal(control.FinalBoneTransforms, sampled.FinalBoneTransforms);
    }

    private static SkinnedMesh CreateSkin()
    {
        var elements = new[] { VertexElement.POSITION };
        VertexBuffer vertices = VertexBuffer.Create(VertexBufferUsage.Static, elements,
            VertexBuffer.AllocateForElements(elements, 3));
        MemoryOwner<byte> indices = MemoryOwner<byte>.Allocate(3 * sizeof(ushort));
        for (ushort index = 0; index < 3; index++)
            BitConverter.TryWriteBytes(indices.Span.Slice(index * sizeof(ushort), sizeof(ushort)), index);
        return new SkinnedMesh(new[] { new SkinnedMeshRange("body", 0, 3, 0, 3) }, vertices,
            IndexBuffer.Create(IndexFormat.U16, indices));
    }

    private sealed class MovingAnimation : IAnimationAsset
    {
        public float Duration => 1;
        public float Fps => 60;
        public bool IsDisposed => false;
        public void Dispose() { }
        public void Evaluate(float time, System.Collections.Generic.IDictionary<uint,
            (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
        {
            pose.Clear();
            pose[Elf.HashLower("Root")] = (Quaternion.Identity, new Vector3(time * 20, 0, 0), Vector3.One);
        }
    }
}
