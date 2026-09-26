using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Vfx.Loading;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;
using Xunit.Abstractions;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Loading;

public sealed class KaynFormInvestigationTests
{
    private readonly ITestOutputHelper _output;

    public KaynFormInvestigationTests(ITestOutputHelper output) => _output = output;

    private static string SkinBin => Path.Combine(KaynDiagnosticFactAttribute.Root,
        "data", "characters", "kayn", "skins", "skin32.bin");

    [KaynDiagnosticFact]
    public void InspectParsedForms()
    {
        using var loader = new VfxLoadingService();
        var bundle = loader.Load(SkinBin, null);
        using var skn = SkinnedMesh.ReadFromSimpleSkin(Path.Combine(KaynDiagnosticFactAttribute.Root,
            "assets", "characters", "kayn", "skins", "skin32", "kayn_skin32.skn"));
        var names = skn.Ranges.Select(range => range.Material)
            .ToDictionary(name => Fnv1a.HashLower(name), name => name);
        string Submesh(uint hash) => names.GetValueOrDefault(hash, hash.ToString("x8"));

        Assert.Equal(3, bundle.CharacterForms.Count);
        string[] expectedBodies = { "Body_Base", "Body_Assassin", "Body_Slayer" };
        foreach (var form in bundle.CharacterForms)
        {
            Assert.False(form.HasMaterialOverrides);
            Assert.Contains(Fnv1a.HashLower(expectedBodies[form.GearIndex]), form.ShowSubmeshHashes);
            _output.WriteLine($"FORM {form.GearIndex} {form.PathHash:x8} {form.Name}: " +
                $"show={string.Join(",", form.ShowSubmeshHashes.Select(Submesh))}; " +
                $"hide={string.Join(",", form.HideSubmeshHashes.Select(Submesh))}; " +
                $"resources={form.ResourceMap.Count}; idleOverride={form.EnableOverrideIdleEffects}");
        }
        foreach (var clip in bundle.Clips.Where(clip => clip.UsesEquippedGearParameter).Take(4))
        {
            foreach (var form in bundle.CharacterForms)
            {
                var playlist = AnimationGraphPlayback.ResolvePlaylist(clip, bundle.Clips,
                    gearIndex: form.GearIndex);
                _output.WriteLine($"GEAR CLIP {clip.OwnerPathHash:x8} Form {form.GearIndex}: " +
                    string.Join(",", playlist.Select(atomic => atomic.AnimationFilePath)));
            }
        }
        _output.WriteLine($"Gear-driven clips: {bundle.Clips.Count(clip => clip.UsesEquippedGearParameter)}; " +
            $"BINs: {bundle.LoadedBins.Count}");
    }

    [KaynDiagnosticFact]
    public void InspectAnimationGearUpdaters()
    {
        using var stream = File.OpenRead(Path.Combine(KaynDiagnosticFactAttribute.Root,
            "data", "characters", "kayn", "animations", "skin32.bin"));
        var tree = new BinTree(stream);
        foreach (var graph in tree.Objects.Values)
        {
            _output.WriteLine($"ANIMATION OBJECT {graph.PathHash:x8} class={graph.ClassHash:x8}: " +
                string.Join(",", graph.Properties.Values.Select(property =>
                    $"{property.NameHash:x8}:{property.GetType().Name}")));
            if (!graph.Properties.TryGetValue(Fnv1a.HashLower("mClipDataMap"), out var mapProperty) ||
                mapProperty is not BinTreeMap map) continue;
            _output.WriteLine("CLIP CLASSES: " + string.Join(",", map.Select(entry => entry.Value)
                .OfType<BinTreeStruct>().GroupBy(clip => clip.ClassHash)
                .Select(group => $"{group.Key:x8}={group.Count()}")));
            foreach (var entry in map)
            {
                if (entry.Value is not BinTreeStruct clip ||
                    clip.ClassHash == Fnv1a.HashLower("AtomicClipData")) continue;
                _output.WriteLine($"COMPOSITE {((BinTreeHash)entry.Key).Value:x8} class={clip.ClassHash:x8}");
                foreach (var property in clip.Properties.Values) Dump(property, 0);
            }
        }
    }

    [KaynDiagnosticFact]
    public void InspectSkin()
    {
        using var stream = File.OpenRead(SkinBin);
        var tree = new BinTree(stream);
        _output.WriteLine("Dependencies: " + string.Join("\n", tree.Dependencies));
        foreach (var upgrade in tree.Objects.Values.Where(
            obj => obj.ClassHash == Fnv1a.HashLower("GearSkinUpgrade")))
        {
            _output.WriteLine($"GEAR {upgrade.PathHash:x8}");
            foreach (var property in upgrade.Properties.Values)
                Dump(property, 0);
        }
    }

    private void Dump(BinTreeProperty property, int depth)
    {
        string prefix = new string(' ', depth * 2) + $"{property.NameHash:x8} {property.GetType().Name}" +
            (property is BinTreeStruct typed ? $" class={typed.ClassHash:x8}" : string.Empty) + ": ";
        _output.WriteLine(prefix + (property switch
        {
            BinTreeString value => value.Value,
            BinTreeHash value => value.Value.ToString("x8"),
            BinTreeObjectLink value => value.Value.ToString("x8"),
            BinTreeWadChunkLink value => value.Value.ToString("x16"),
            BinTreeBool value => value.Value.ToString(),
            _ => string.Empty
        }));
        if (depth >= 3) return;
        if (property is BinTreeStruct structure)
            foreach (var child in structure.Properties.Values) Dump(child, depth + 1);
        if (property is BinTreeContainer container)
            foreach (var child in container.Elements) Dump(child, depth + 1);
    }
}

// Opt in with an extracted WAD folder; ordinary test runs need no local game assets.
public sealed class KaynDiagnosticFactAttribute : FactAttribute
{
    public static string Root => Environment.GetEnvironmentVariable("ASSETSMANAGER_KAYN_DIAGNOSTIC_ROOT");

    public KaynDiagnosticFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Root) || !Directory.Exists(Root))
            Skip = "Set ASSETSMANAGER_KAYN_DIAGNOSTIC_ROOT to an extracted Kayn WAD folder.";
    }
}
