using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Animation.Builders;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer;

public sealed class GraphPoseTests
{
    [Fact]
    public void AdditiveZeroDeltasKeepBoneSeparationThroughRetimedPlaylistAndSeeking()
    {
        var builder = new RigResourceBuilder();
        var root = builder.CreateJoint("Root").WithLocalTransform(Matrix4x4.CreateTranslation(0, 80, 0))
            .WithInverseBindTransform(Matrix4x4.CreateTranslation(0, -80, 0));
        root.CreateJoint("Spine").WithLocalTransform(Matrix4x4.CreateTranslation(-30, 0, 0))
            .WithInverseBindTransform(Matrix4x4.CreateTranslation(30, -80, 0));
        var rig = builder.Build();
        var delta = new PoseAsset(("Root", Quaternion.Identity, Vector3.Zero, Vector3.One),
            ("Spine", Quaternion.Identity, Vector3.Zero, Vector3.One));
        using var animation = AnimationGraphPlayback.CreatePlaylist(new[] {
            AnimationGraphPlayback.RetimeForGraph(new GraphPoseAnimationAsset(delta, null, 1f, null), 1f / 30f) });
        using var evaluator = new AnimationService();
        foreach (float time in new[] { 0.7f, 0.1f, 0.7f })
        {
            var matrices = evaluator.EvaluateSkinningTransforms(time, animation, rig);
            foreach (var matrix in matrices) Assert.Equal(Matrix4x4.Identity, matrix);
            Assert.Equal(new Vector3(-30, 80, 0), evaluator.WorldBoneTransforms[1].Translation);
        }
    }

    [Fact]
    public void AuthoredBasisAndWeightedDeltasComposeTranslationRotationAndScale()
    {
        Quaternion baseRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.8f);
        Quaternion deltaRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.6f);
        var basis = new PoseAsset(("Root", baseRotation, new(10, 20, 30), new(2)));
        var delta = new PoseAsset(("Root", deltaRotation, new(4, 6, 8), new(3)));
        var animation = new GraphPoseAnimationAsset(delta, basis, 0.5f, null);
        var pose = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
        animation.Evaluate(0f, pose);
        var result = pose[Elf.HashLower("Root")];
        Assert.Equal(new Vector3(12, 23, 34), result.Translation);
        Assert.Equal(new Vector3(4), result.Scale);
        Matrix4x4 expected = Matrix4x4.CreateFromQuaternion(Quaternion.Slerp(Quaternion.Identity, deltaRotation, 0.5f)) *
            Matrix4x4.CreateFromQuaternion(baseRotation);
        Vector3 actual = Vector3.Transform(Vector3.UnitZ, result.Rotation);
        Assert.InRange(Vector3.Distance(Vector3.TransformNormal(Vector3.UnitZ, expected), actual), 0f, 1e-5f);
    }

    [Fact]
    public void MasksApplyBySkeletonSlotAndInvalidOrMissingWeightsPreserveBase()
    {
        var builder = new RigResourceBuilder();
        builder.CreateJoint("Root").WithLocalTransform(Matrix4x4.CreateTranslation(10, 0, 0));
        builder.CreateJoint("Hand").WithLocalTransform(Matrix4x4.CreateTranslation(20, 0, 0));
        var delta = new PoseAsset(("Root", Quaternion.Identity, Vector3.UnitX * 8, Vector3.One),
            ("Hand", Quaternion.Identity, Vector3.UnitX * 8, Vector3.One));
        var pose = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
        var animation = new GraphPoseAnimationAsset(delta, null, 0.5f, new[] { 0.5f, float.NaN });
        animation.Evaluate(0f, pose, builder.Build());
        Assert.Equal(new Vector3(12, 0, 0), pose[Elf.HashLower("Root")].Translation);
        Assert.Equal(new Vector3(20, 0, 0), pose[Elf.HashLower("Hand")].Translation);
    }

    [Fact]
    public void OnlyDeclaredAdditiveTracksAreWrappedAndBasisIsGraphLocal()
    {
        var source = new PoseAsset();
        var atomic = new AnimationClipDefinition(2, 0, 1f/30f, 0, -1, Array.Empty<AnimationClipEventDefinition>(),
            AnimationFilePath: "attack.anm", Track: new(20, "Action", true));
        var basis = atomic with { OwnerPathHash = Fnv1a.HashLower("Idle1_Base"), Track = new(10, "Base", true), AnimationFilePath = "idle.anm" };
        var graph = new AnimationGraphDefinition(1, new[] { atomic, basis }, new[] {
            new AnimationTrackDefinition(10, "Base", 0, 0, 1), new AnimationTrackDefinition(20, "Action", 3, 1, 1) },
            Array.Empty<AnimationMaskDefinition>(), Array.Empty<AnimationSyncGroupDefinition>());
        Assert.Same(basis, GraphPoseAnimationAsset.BasisClip(graph));
        Assert.IsType<GraphPoseAnimationAsset>(GraphPoseAnimationAsset.Wrap(source, atomic, graph, null));
        Assert.Same(source, GraphPoseAnimationAsset.Wrap(source, basis, graph, null));
        Assert.Null(GraphPoseAnimationAsset.BasisClip(graph with { Clips = new[] { atomic } }));
    }

    [Fact]
    public async Task VfxCatalogPreparesTheSelectedAdditiveClipAgainstItsOwnGraphBase()
    {
        string folder = Path.Combine(Path.GetTempPath(), "AssetsManager-GraphPose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            void Write(string name, Vector3 translation)
            {
                using var file = File.Create(Path.Combine(folder, name));
                using var writer = new BinaryWriter(file);
                writer.Write(Encoding.ASCII.GetBytes("r3d2anmd"));
                writer.Write(3u); writer.Write(0u); writer.Write(1); writer.Write(1); writer.Write(30);
                byte[] jointName = new byte[32]; Encoding.ASCII.GetBytes("Root").CopyTo(jointName, 0); writer.Write(jointName);
                writer.Write(0u);
                writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
                writer.Write(translation.X); writer.Write(translation.Y); writer.Write(translation.Z);
            }
            Write("basis.anm", new(10, 20, 30));
            Write("delta.anm", new(4, 6, 8));
            var atomic = new AnimationClipDefinition(2, Fnv1a.HashLower("AtomicClipData"), 1f/30, 0, -1,
                Array.Empty<AnimationClipEventDefinition>(), "Attack1", "delta.anm", 1, Track: new(20, "Action", true));
            var basis = atomic with { OwnerPathHash = Fnv1a.HashLower("Idle1_Base"), ClipName = "Idle1_Base", Track = new(10, "Base", true), AnimationFilePath = "basis.anm" };
            var graph = new AnimationGraphDefinition(1, new[] { atomic, basis }, new[] {
                new AnimationTrackDefinition(10, "Base", 0, 0, 1), new AnimationTrackDefinition(20, "Action", 3, 1, 0.5f) },
                Array.Empty<AnimationMaskDefinition>(), Array.Empty<AnimationSyncGroupDefinition>());
            var bundle = new VfxLoadingService.Bundle();
            bundle.Clips.AddRange(graph.Clips); bundle.AnimationGraphs.Add(graph);
            using var catalog = new VfxClipCatalog();
            string Resolve(string name) => Path.Combine(folder, name);
            var item = catalog.BuildMetadata(bundle, Resolve).Single(item => item.Clip.OwnerPathHash == 2);
            var prepared = await catalog.PrepareAsync(item, bundle, Resolve, null);
            Assert.NotNull(prepared);
            var builder = new RigResourceBuilder(); builder.CreateJoint("Root").WithLocalTransform(Matrix4x4.Identity);
            using var evaluator = new AnimationService();
            evaluator.EvaluateSkinningTransforms(0, prepared.AnimationAsset, builder.Build());
            Assert.Equal(new Vector3(12, 23, 34), evaluator.WorldBoneTransforms[0].Translation);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }

    [Fact]
    public async Task MapAmbientPlaybackReceivesPoseCuesAndAttachmentSamplingPreservesVisiblePose()
    {
        string folder = Path.Combine(Path.GetTempPath(), "AssetsManager-MapPose-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using (var writer = new BinaryWriter(File.Create(Path.Combine(folder, "animation.anm"))))
            {
                writer.Write(Encoding.ASCII.GetBytes("r3d2anmd"));
                writer.Write(3u); writer.Write(0u); writer.Write(1); writer.Write(2); writer.Write(30);
                byte[] jointName = new byte[32]; Encoding.ASCII.GetBytes("Root").CopyTo(jointName, 0); writer.Write(jointName);
                writer.Write(0u);
                foreach (float x in new[] { 0f, 10f })
                {
                    writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
                    writer.Write(x); writer.Write(0f); writer.Write(0f);
                }
            }
            var idle = new AnimationClipDefinition(1, Fnv1a.HashLower("AtomicClipData"), 1f/30, 0, -1,
                new AnimationClipEventDefinition[] { new AnimationJointSnapEventDefinition(1, 0, -1,
                    Fnv1a.HashLower("Root"), Fnv1a.HashLower("Hand"), Vector3.UnitX) }, "Idle1", "animation.anm", 1);
            var attack = idle with { OwnerPathHash = 2, ClipName = "Attack1", Events = Array.Empty<AnimationClipEventDefinition>() };
            var graph = new AnimationGraphDefinition(1, new[] { idle, attack }, Array.Empty<AnimationTrackDefinition>(),
                Array.Empty<AnimationMaskDefinition>(), Array.Empty<AnimationSyncGroupDefinition>());
            var builder = new RigResourceBuilder();
            builder.CreateJoint("Root").WithLocalTransform(Matrix4x4.Identity).WithInverseBindTransform(Matrix4x4.Identity)
                .CreateJoint("Hand").WithLocalTransform(Matrix4x4.CreateTranslation(2, 0, 0))
                .WithInverseBindTransform(Matrix4x4.CreateTranslation(-2, 0, 0));
            var asset = new MapCharacterAssetData(null, null, builder.Build(), null, null, graph);
            using var runtime = new MapCharacterAnimationRuntime(new MapAssetResolver(null, null), null);
            await runtime.PrepareAsync(asset, new[] { "Idle1" }, folder);
            Assert.Equal(new Vector3(3, 0, 0), runtime.Evaluate(asset, "Idle1", 0f)[0].Translation);
            Assert.True(await runtime.PrepareClipAsync(asset, attack));
            Matrix4x4[] visible = runtime.EvaluateClip(asset, attack, 0.01f).ToArray();
            Assert.True(runtime.TrySamplePreparedClipBoneTransform(asset, attack, 0.02, null, Fnv1a.HashLower("Root"), out var sampled));
            Assert.InRange(sampled.Translation.X, 5.99f, 6.01f);
            Assert.Equal(visible, runtime.PreparedClipSkinningMatrices(attack));
            Assert.True(runtime.TryGetPreparedClipBoneTransform(attack, "Root", 0, out var restored));
            Assert.Equal(visible[0].Translation, restored.Translation);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }

    [Fact]
    public void BackgroundBasisPreparationAndPlaybackSerializeTheSharedDecoder()
    {
        var source = new CursorAsset();
        Parallel.For(0, 32, index =>
        {
            var pose = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
            if (index % 2 == 0) _ = new GraphPoseAnimationAsset(new PoseAsset(), source, 1, null);
            else AnimationGraphPlayback.Evaluate(source, index, pose, null);
        });
        Assert.False(source.Overlapped);
    }

    private sealed class CursorAsset : IAnimationAsset
    {
        private int _readers;
        internal bool Overlapped;
        public float Duration => 1;
        public float Fps => 30;
        public bool IsDisposed => false;
        public void Dispose() { }
        public void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
        {
            if (Interlocked.Increment(ref _readers) != 1) Overlapped = true;
            Thread.SpinWait(10000);
            Interlocked.Decrement(ref _readers);
        }
    }

    private sealed class PoseAsset(params (string Name, Quaternion Rotation, Vector3 Translation, Vector3 Scale)[] joints) : IAnimationAsset
    {
        public float Duration => 1;
        public float Fps => 60;
        public bool IsDisposed => false;
        public void Dispose() { }
        public void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
        {
            foreach (var joint in joints) pose[Elf.HashLower(joint.Name)] = (joint.Rotation, joint.Translation, joint.Scale);
        }
    }
}
