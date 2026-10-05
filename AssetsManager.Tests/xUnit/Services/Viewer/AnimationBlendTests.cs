using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Animation.Builders;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer;

public sealed class AnimationBlendTests
{
    private const uint GraphHash = 10;
    private static readonly uint RootHash = Elf.HashLower("Root");

    [Theory]
    [InlineData(null, 0.2f)]
    [InlineData(0f, 0f)]
    [InlineData(0.1f, 0.1f)]
    public void ParserReadsDirectedTimeBlendsWithNativeDefaultAndExplicitZero(float? time, float expected)
    {
        var blend = new BinTreeStruct(0, Fnv1a.HashLower("TimeBlendData"), time.HasValue
            ? new BinTreeProperty[] { new BinTreeF32(Fnv1a.HashLower("mTime"), time.Value) }
            : Array.Empty<BinTreeProperty>());
        var graph = ReadGraph(3165926883608822659ul, blend);
        var parsed = Assert.IsType<AnimationTimeBlendDefinition>(Assert.Single(graph.Blends));
        Assert.Equal(Fnv1a.HashLower("Ult_Windup"), parsed.FromClipHash);
        Assert.Equal(Fnv1a.HashLower("Ult_Loop"), parsed.ToClipHash);
        Assert.Equal(expected, parsed.Duration);
    }

    [Theory]
    [InlineData(-0.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidTimeBlendsAreIgnored(float duration)
    {
        var graph = ReadGraph(1, new BinTreeStruct(0, Fnv1a.HashLower("TimeBlendData"),
            new BinTreeProperty[] { new BinTreeF32(Fnv1a.HashLower("mTime"), duration) }));
        Assert.Empty(graph.Blends);
    }

    [Fact]
    public void TransitionClipsRemainDistinctFromTimeBlendsAndUnknownTypesAreIgnored()
    {
        var blend = new BinTreeStruct(0, Fnv1a.HashLower("TransitionClipBlendData"),
            new BinTreeProperty[] { new BinTreeHash(Fnv1a.HashLower("mClipName"), 3) });
        var parsed = Assert.IsType<AnimationTransitionClipBlendDefinition>(Assert.Single(ReadGraph((1ul << 32) | 2, blend).Blends));
        Assert.Equal(1u, parsed.FromClipHash);
        Assert.Equal(2u, parsed.ToClipHash);
        Assert.Equal(3u, parsed.ClipHash);
        Assert.Empty(ReadGraph(1, new BinTreeStruct(0, 999, Array.Empty<BinTreeProperty>())).Blends);
    }

    [Fact]
    public void BlendInterpolatesMovingIncomingPoseAndKeepsSequenceAndCueClocks()
    {
        var clips = new[] { Clip(1), Clip(2) with { Events = new AnimationClipEventDefinition[] {
            new AnimationSubmeshVisibilityEventDefinition(7, 0, -1, new[] { 11u }, Array.Empty<uint>()) } } };
        var steps = new IAnimationAsset[] { new PoseAsset(1, time => time * 10), new PoseAsset(1, time => 30 + time * 10, MathF.PI / 2, 3) };
        using var animation = AnimationGraphPlayback.CreatePlaylist(steps, clips, Graph(clips, new AnimationTimeBlendDefinition(1, 2, 0.2f)));
        Assert.Equal(2f, animation.Duration);
        Assert.Equal(1d, Assert.Single(MapCharacterAnimationRuntime.BuildTimedCues(clips, steps)).AtSeconds);
        Assert.Equal(2.5f, PoseAt(animation, 0.25f)[RootHash].Translation.X);
        Assert.Equal(10f, PoseAt(animation, 1f)[RootHash].Translation.X);
        var half = PoseAt(animation, 1.1f)[RootHash];
        Assert.InRange(MathF.Abs(half.Translation.X - 20.5f), 0, 0.00001f);
        Assert.InRange(MathF.Abs(half.Scale.X - 2), 0, 0.00001f);
        Assert.InRange(Vector3.Distance(Vector3.Transform(Vector3.UnitX, half.Rotation),
            new Vector3(MathF.Sqrt(0.5f), MathF.Sqrt(0.5f), 0)), 0, 0.00001f);
        Assert.InRange(MathF.Abs(PoseAt(animation, 1.2f)[RootHash].Translation.X - 32), 0, 0.00001f);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("zero")]
    [InlineData("reverse")]
    [InlineData("other_graph")]
    [InlineData("transition_clip")]
    public void OnlyPositiveTimeRulesForTheExactGraphAndDirectionBlend(string kind)
    {
        var clips = new[] { Clip(1), Clip(2) };
        AnimationBlendDefinition[] rules = kind switch
        {
            "missing" => Array.Empty<AnimationBlendDefinition>(),
            "zero" => new AnimationBlendDefinition[] { new AnimationTimeBlendDefinition(1, 2, 0) },
            "reverse" => new AnimationBlendDefinition[] { new AnimationTimeBlendDefinition(2, 1, 0.2f) },
            "transition_clip" => new AnimationBlendDefinition[] { new AnimationTransitionClipBlendDefinition(1, 2, 3) },
            _ => new AnimationBlendDefinition[] { new AnimationTimeBlendDefinition(1, 2, 0.2f) }
        };
        var graph = Graph(clips, rules);
        if (kind == "other_graph") graph = graph with { PathHash = 99 };
        using var animation = AnimationGraphPlayback.CreatePlaylist(
            new IAnimationAsset[] { new PoseAsset(1, _ => 10), new PoseAsset(1, _ => 30) }, clips, graph);
        Assert.Equal(30, PoseAt(animation, 1)[RootHash].Translation.X);
    }

    [Fact]
    public void InterruptedBlendsAndBackwardSeeksReconstructTheSameBoundaryPose()
    {
        var clips = new[] { Clip(1), Clip(2), Clip(3) };
        using var animation = AnimationGraphPlayback.CreatePlaylist(new IAnimationAsset[] {
            new PoseAsset(1, _ => 0), new PoseAsset(0.05f, _ => 100), new PoseAsset(1, _ => 200) }, clips,
            Graph(clips, new AnimationTimeBlendDefinition(1, 2, 0.2f), new AnimationTimeBlendDefinition(2, 3, 0.1f)));
        foreach (float time in new[] { 1.075f, 1.05f, 0f, 1.075f, 1.15f, 1.05f })
        {
            float expected = time switch { 1.075f => 68.75f, 1.05f => 25f, 0f => 0f, _ => 200f };
            Assert.InRange(MathF.Abs(PoseAt(animation, time)[RootHash].Translation.X - expected), 0, 0.0003f);
        }
    }

    [Theory]
    [InlineData(true, 10f)]
    [InlineData(false, 30f)]
    public void PartialTracksBlendAgainstSkeletonBindPose(bool missingIncoming, float expected)
    {
        var builder = new RigResourceBuilder();
        builder.CreateJoint("Root").WithLocalTransform(Matrix4x4.CreateTranslation(20, 0, 0));
        var rig = builder.Build();
        var clips = new[] { Clip(1), Clip(2) };
        var tracked = new PoseAsset(1, _ => missingIncoming ? 0 : 40);
        var empty = new PoseAsset(1, null);
        using var animation = AnimationGraphPlayback.CreatePlaylist(missingIncoming
            ? new IAnimationAsset[] { tracked, empty } : new IAnimationAsset[] { empty, tracked }, clips,
            Graph(clips, new AnimationTimeBlendDefinition(1, 2, 0.2f)));
        foreach (float time in new[] { 1.1f, 0f, 1.1f })
        {
            var pose = PoseAt(animation, time, rig);
            if (time > 1) Assert.InRange(MathF.Abs(pose[RootHash].Translation.X - expected), 0, 0.00001f);
        }
    }

    [Fact]
    public async Task CatalogUsesGraphBlendsWhileKeepingVisibilityOnTheIncomingClipClock()
    {
        string folder = Path.Combine(Path.GetTempPath(), "AssetsManager-AnimationBlend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            void WriteAnm(string name, float position)
            {
                using var writer = new BinaryWriter(File.Create(Path.Combine(folder, name)));
                writer.Write(Encoding.ASCII.GetBytes("r3d2anmd"));
                writer.Write(3u); writer.Write(0u); writer.Write(1); writer.Write(30); writer.Write(30);
                byte[] jointName = new byte[32];
                Encoding.ASCII.GetBytes("Root").CopyTo(jointName, 0);
                writer.Write(jointName); writer.Write(0u);
                for (int frame = 0; frame < 30; frame++)
                {
                    writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
                    writer.Write(position); writer.Write(0f); writer.Write(0f);
                }
            }
            WriteAnm("first.anm", 10);
            WriteAnm("second.anm", 30);
            var first = Clip(1) with { AnimationFilePath = "first.anm" };
            var second = Clip(2) with { AnimationFilePath = "second.anm", Events = new AnimationClipEventDefinition[] {
                new AnimationSubmeshVisibilityEventDefinition(7, 0, -1, new[] { 11u }, Array.Empty<uint>()) } };
            var sequence = Clip(3) with { OwnerClassHash = Fnv1a.HashLower("SequencerClipData"), ChildClipHashes = new[] { 1u, 2u } };
            var clips = new[] { sequence, first, second };
            var bundle = new VfxLoadingService.Bundle();
            bundle.Clips.AddRange(clips);
            bundle.AnimationGraphs.Add(Graph(clips, new AnimationTimeBlendDefinition(1, 2, 0.2f)));
            using var catalog = new VfxClipCatalog();
            string Resolve(string name) => Path.Combine(folder, name);
            var metadata = catalog.BuildMetadata(bundle, Resolve).Single(item => item.Clip.OwnerPathHash == 3);
            var prepared = await catalog.PrepareAsync(metadata, bundle, Resolve, null);
            Assert.Equal(2f, prepared.Duration);
            Assert.Equal(1d, Assert.Single(prepared.TimedCues).AtSeconds);
            foreach (float time in new[] { 1.1f, 0f, 1.1f })
                Assert.InRange(MathF.Abs(PoseAt(prepared.AnimationAsset, time)[RootHash].Translation.X - (time > 1 ? 20 : 10)), 0, 0.00001f);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static AnimationGraphDefinition ReadGraph(ulong key, BinTreeStruct blend)
    {
        var owner = new BinTreeObject("Animations/Blends", "AnimationGraphData", new BinTreeProperty[] {
            new BinTreeMap(Fnv1a.HashLower("mClipDataMap"), BinPropertyType.Hash, BinPropertyType.Struct,
                Array.Empty<KeyValuePair<BinTreeProperty, BinTreeProperty>>()),
            new BinTreeMap(Fnv1a.HashLower("mBlendDataTable"), BinPropertyType.U64, BinPropertyType.Struct,
                new[] { new KeyValuePair<BinTreeProperty, BinTreeProperty>(new BinTreeU64(0, key), blend) }) });
        var tree = new BinTree(new[] { owner }, Array.Empty<string>());
        return Assert.Single(VfxAnimationParser.ExtractAnimationGraphs(tree, null, null));
    }

    private static AnimationClipDefinition Clip(uint hash) => new(hash, Fnv1a.HashLower("AtomicClipData"),
        1f / 30f, 0, -1, Array.Empty<AnimationClipEventDefinition>(), GraphPathHash: GraphHash);

    private static AnimationGraphDefinition Graph(AnimationClipDefinition[] clips, params AnimationBlendDefinition[] rules) =>
        new(GraphHash, clips, Array.Empty<AnimationTrackDefinition>(), Array.Empty<AnimationMaskDefinition>(),
            Array.Empty<AnimationSyncGroupDefinition>(), rules);

    private static Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> PoseAt(
        IAnimationAsset animation, float time, RigResource skeleton = null)
    {
        var pose = new Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)>();
        AnimationGraphPlayback.Evaluate(animation, time, pose, skeleton);
        return pose;
    }

    private sealed class PoseAsset(float duration, Func<float, float> position, float angle = 0, float scale = 1) : IAnimationAsset
    {
        public float Duration => duration;
        public float Fps => 30;
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
        public void Evaluate(float time, IDictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> pose)
        {
            if (position != null) pose[RootHash] = (Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle),
                new Vector3(position(time), 0, 0), new Vector3(scale));
        }
    }
}
