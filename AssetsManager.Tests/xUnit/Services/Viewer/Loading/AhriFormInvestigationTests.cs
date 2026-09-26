using System;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Loading;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Hashing;
using Xunit;
using Xunit.Abstractions;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Loading;

public sealed class AhriFormInvestigationTests(ITestOutputHelper output)
{
    [AhriDiagnosticFact]
    public void InspectHallOfLegendsForms()
    {
        foreach (int skin in new[] { 85, 86 })
        {
            using var loader = new VfxLoadingService();
            string binPath = Path.Combine(AhriDiagnosticFactAttribute.Root,
                "data", "characters", "ahri", "skins", $"skin{skin}.bin");
            var bundle = loader.Load(binPath, null);
            string meshPath = Path.Combine(AhriDiagnosticFactAttribute.Root,
                "assets", "characters", "ahri", "skins", $"skin{skin}", $"ahri_skin{skin}.skn");
            using var mesh = SkinnedMesh.ReadFromSimpleSkin(meshPath);
            var names = mesh.Ranges.Select(range => range.Material)
                .GroupBy(name => Fnv1a.HashLower(name))
                .ToDictionary(group => group.Key, group => group.First());
            string Submesh(uint hash) => names.TryGetValue(hash, out string name) ? name : hash.ToString("x8");
            output.WriteLine($"SKIN {skin}: forms={bundle.CharacterForms.Count}; " +
                $"gearClips={bundle.Clips.Count(clip => clip.UsesEquippedGearParameter)}; " +
                $"mesh={bundle.OwnerSceneContext?.MeshPath}; skeleton={bundle.OwnerSceneContext?.SkeletonPath}");
            Assert.Equal(skin == 86 ? 3 : 0, bundle.CharacterForms.Count);
            output.WriteLine("SUBMESHES: " + string.Join(",", names.Values));
            foreach (var form in bundle.CharacterForms)
            {
                Assert.False(form.HasMaterialOverrides);
                Assert.True(string.IsNullOrEmpty(form.MeshPath));
                Assert.True(string.IsNullOrEmpty(form.SkeletonPath));
                output.WriteLine($"FORM {form.GearIndex} {form.PathHash:x8}: " +
                    $"show={string.Join(",", form.ShowSubmeshHashes.Select(Submesh))}; " +
                    $"hide={string.Join(",", form.HideSubmeshHashes.Select(Submesh))}; " +
                    $"mesh={form.MeshPath}; skeleton={form.SkeletonPath}; " +
                    $"materialOverrides={form.HasMaterialOverrides}; resources={form.ResourceMap.Count}; " +
                    $"idleOverride={form.EnableOverrideIdleEffects}");
            }
            Assert.NotEmpty(bundle.LoadedBins);
        }
    }
}

public sealed class AhriDiagnosticFactAttribute : FactAttribute
{
    public static string Root => Environment.GetEnvironmentVariable("ASSETSMANAGER_AHRI_DIAGNOSTIC_ROOT");

    public AhriDiagnosticFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Root) || !Directory.Exists(Root))
            Skip = "Set ASSETSMANAGER_AHRI_DIAGNOSTIC_ROOT to an extracted Ahri WAD folder.";
    }
}
