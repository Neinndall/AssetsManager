using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Vfx;

public class ParentClipEventsTests
{
    [Theory]
    [InlineData("SequencerClipData", false, true)]
    [InlineData("ParametricClipData", false, false)]
    [InlineData("ParametricClipData", true, true)]
    [InlineData("SelectorClipData", true, false)]
    public void ParserPreservesTheAuthoredParentEventPolicy(string kind, bool flag, bool expected)
    {
        var owner = new BinTreeObject("Animations/Parent", kind, new BinTreeProperty[]
        {
            new BinTreeBool(0x69de8fca, flag),
            new BinTreeMap(Fnv1a.HashLower("mEventDataMap"), BinPropertyType.Hash, BinPropertyType.Struct,
                new[] { new System.Collections.Generic.KeyValuePair<BinTreeProperty, BinTreeProperty>(
                    new BinTreeHash(0, 1), new BinTreeStruct(0, Fnv1a.HashLower("FaceTargetEventData"), Array.Empty<BinTreeProperty>())) })
        });
        using var stream = new MemoryStream();
        new BinTree(new[] { owner }, Array.Empty<string>()).Write(stream);
        var document = VfxGraphParser.ParseDocument(stream.ToArray());
        Assert.Equal(expected, Assert.Single(document.EventSequences).PlaysOwnEvents);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CatalogAndMapCompositionKeepParentEventsOnceOnTheWholePassClock(bool ownEvents)
    {
        string folder = Path.Combine(Path.GetTempPath(), "AssetsManager-ParentEvents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            WriteAnimation(Path.Combine(folder, "first.anm"), 30, 30);
            WriteAnimation(Path.Combine(folder, "second.anm"), 120, 60);
            var first = Clip(1, "first.anm", Particle(1, 15, -1));
            var second = Clip(2, "second.anm", Particle(2, 30, -1));
            var parent = Clip(3, null, Particle(3, 50, 100)) with
            {
                OwnerClassHash = Fnv1a.HashLower("SequencerClipData"), PlaysOwnEvents = ownEvents,
                ChildClipHashes = new uint[] { 1, 2 },
                Events = new AnimationClipEventDefinition[] { Particle(3, 50, 100),
                    new AnimationSubmeshVisibilityEventDefinition(4, 25, 75, new uint[] { 7 }, Array.Empty<uint>()) }
            };
            var bundle = new VfxLoadingService.Bundle();
            bundle.Clips.AddRange(new[] { parent, first, second });
            bundle.Systems[100] = new VfxSystemDefinition(100, "Effect", "effect", Array.Empty<VfxEmitterDefinition>());
            bundle.ResourceMap[7] = 100;
            using var catalog = new VfxClipCatalog();
            string Resolve(string path) => Path.Combine(folder, path);
            var metadata = catalog.BuildMetadata(bundle, Resolve).Single(item => item.Clip == parent);
            Assert.Equal(ownEvents ? 3 : 2, metadata.Composition.ResolvedCount);
            var prepared = await catalog.PrepareAsync(metadata, bundle, Resolve, null);
            Assert.Equal(3f, prepared.Duration, 5);
            Assert.Equal(ownEvents ? 4 : 2, prepared.EventCount);
            Assert.Equal(ownEvents ? 3 : 2, prepared.Composition.ResolvedCount);
            Assert.Equal(0.5f, prepared.Composition.Events.Single(cue => cue.Event.EventHash == 1).Event.StartFrame, 5);
            Assert.Equal(1.5f, prepared.Composition.Events.Single(cue => cue.Event.EventHash == 2).Event.StartFrame, 5);
            if (ownEvents)
            {
                var cue = Assert.Single(prepared.Composition.Events, cue => cue.Event.EventHash == 3);
                Assert.Equal(1f, cue.Event.StartFrame, 5);
                Assert.Equal(2f, cue.Event.EndFrame, 5);
                var visibility = Assert.Single(prepared.TimedCues);
                Assert.Equal(0.5, visibility.AtSeconds, 5);
                Assert.Equal(1.5, visibility.UntilSeconds!.Value, 5);
            }
            else Assert.Empty(prepared.TimedCues);

            var mapped = VfxAbilityCompositionBuilder.BuildTimedPlaylist(parent, new[] { first, second },
                new[] { 1f, 2f }, new[] { 1f / 30, 1f / 60 }, bundle.Systems, bundle.ResourceMap);
            Assert.Equal(prepared.Composition.Events.Select(cue => cue.Event), mapped.Events.Select(cue => cue.Event));

            var direct = first with { PlaysOwnEvents = true };
            var directMetadata = catalog.BuildMetadata(BundleWith(direct), Resolve).Single();
            var directPrepared = await catalog.PrepareAsync(directMetadata, BundleWith(direct), Resolve, null);
            Assert.Single(directPrepared.Composition.Events);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static VfxLoadingService.Bundle BundleWith(AnimationClipDefinition clip)
    {
        var bundle = new VfxLoadingService.Bundle();
        bundle.Clips.Add(clip);
        return bundle;
    }

    private static AnimationClipDefinition Clip(uint hash, string file, VfxParticleEventDefinition particle)
        => new(hash, Fnv1a.HashLower("AtomicClipData"), 0, 0, -1, new[] { particle },
            ClipName: $"Clip{hash}", AnimationFilePath: file);

    private static VfxParticleEventDefinition Particle(uint hash, float start, float end)
        => new(hash, 0, start, end, 7, 0, "", false, false, false, false, false, false, false, 1,
            Array.Empty<VfxParticleEventAttachment>());

    private static void WriteAnimation(string path, int frames, int fps)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(Encoding.ASCII.GetBytes("r3d2anmd"));
        writer.Write(3u); writer.Write(0u); writer.Write(1); writer.Write(frames); writer.Write(fps);
        byte[] joint = new byte[32];
        Encoding.ASCII.GetBytes("Root").CopyTo(joint, 0);
        writer.Write(joint); writer.Write(0u);
        for (int frame = 0; frame < frames; frame++)
        {
            writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
            writer.Write(0f); writer.Write(0f); writer.Write(0f);
        }
    }
}
