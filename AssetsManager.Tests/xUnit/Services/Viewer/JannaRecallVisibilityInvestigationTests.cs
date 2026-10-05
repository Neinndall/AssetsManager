using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Tests.Support;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer;

public class JannaRecallVisibilityInvestigationTests
{
    [Fact]
    public async Task AuditInstalledRecall()
    {
        string install = InstalledSkins.FindInstall();
        Assert.NotNull(install);
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var log = new LogService(logger);
        var provider = new WadContentProvider(log, new WadNodeLoaderService(null, log), new DirectoriesCreator(), new SvgParser());
        var resolver = new MapAssetResolver(provider, InstalledSkins.Settings(install));
        var loader = new MapCharacterLoadingService(resolver, new MapCharacterSkinParser(), new MapCharacterMeshDecoder(), null, log);
        var asset = await loader.LoadAsync("Characters/Janna/Skins/Skin67", null);
        Assert.NotNull(asset);
        var output = new StringBuilder();
        var names = asset.Mesh.Ranges.Select(range => range.Name).ToDictionary(Fnv1a.HashLower, name => name);
        string Name(uint hash) => names.GetValueOrDefault(hash) ?? $"0x{hash:x8}";
        output.AppendLine("SUBMESHES " + string.Join(", ", names.Select(pair => $"{pair.Key:x8}={pair.Value}")));
        var baseHidden = asset.Materials.InitialHiddenSubmeshes.Select(Fnv1a.HashLower).ToArray();
        output.AppendLine("BASE HIDDEN " + string.Join(", ", baseHidden.Select(Name)));
        var clips = asset.AnimationGraph.Clips.Where(clip => clip.OwnerPathHash == 0x5a81bdb0 || clip.ClipName?.Contains("recall", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        Assert.NotEmpty(clips);
        using var runtime = new MapCharacterAnimationRuntime(resolver, log);
        foreach (var clip in clips)
        {
            output.AppendLine($"CLIP {clip.ClipName} {clip.OwnerPathHash:x8} file={clip.AnimationFilePath} tick={clip.TickDuration:R}");
            foreach (var authored in clip.Events.OfType<AnimationSubmeshVisibilityEventDefinition>())
                output.AppendLine($"EVENT {authored.EventHash:x8} frames={authored.StartFrame:R}..{authored.EndFrame:R} show={string.Join(',', authored.ShowSubmeshHashes.Select(Name))} hide={string.Join(',', authored.HideSubmeshHashes.Select(Name))}");
            Assert.True(await runtime.PrepareClipAsync(asset, clip));
            output.AppendLine($"DURATION {runtime.PreparedClipDuration(clip):R}");
            var cues = runtime.PreparedClipCues(clip);
            foreach (var cue in cues.OfType<AnimationSubmeshVisibilityCue>())
                output.AppendLine($"CUE {cue.AtSeconds:R}..{cue.UntilSeconds:R} show={string.Join(',', cue.ShowSubmeshHashes.Select(Name))} hide={string.Join(',', cue.HideSubmeshHashes.Select(Name))}");
            var timeline = VfxClipCueEvaluator.BuildVisibilityTimeline(cues, baseHidden);
            foreach (var entry in timeline)
                output.AppendLine($"TIMELINE {entry.AtSeconds:R} visible={string.Join(',', names.Where(pair => !entry.Hidden.Contains(pair.Key)).Select(pair => pair.Value))}");
            foreach (double time in new[] { 6.08, 6.13, 6.13334, 6.15, 6.16, 6.16667, 6.17 })
            {
                var hidden = VfxClipCueEvaluator.HiddenSubmeshesAt(timeline, time);
                output.AppendLine($"SAMPLE {time:R} visible={string.Join(',', names.Where(pair => !hidden.Contains(pair.Key)).Select(pair => pair.Value))}");
            }
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "am-janna67-recall-audit.txt"), output.ToString());
    }
}
