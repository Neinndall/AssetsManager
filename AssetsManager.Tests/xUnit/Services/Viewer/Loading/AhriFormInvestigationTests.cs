using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services.Viewer.Vfx.Loading;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using Xunit;
using Xunit.Abstractions;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Loading;

public sealed class AhriFormInvestigationTests(ITestOutputHelper output)
{
    [AhriDiagnosticFact]
    public void InspectFormMaterialBindings()
    {
        string binPath = Path.Combine(AhriDiagnosticFactAttribute.Root,
            "data", "characters", "ahri", "skins", "skin86.bin");
        using var loader = new VfxLoadingService();
        var bundle = loader.Load(binPath, null);
        var trees = bundle.LoadedBins.Select(path =>
        {
            using var stream = File.OpenRead(path);
            return new BinTree(stream);
        }).ToArray();
        var texturePaths = Directory.EnumerateFiles(AhriDiagnosticFactAttribute.Root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".tex", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(AhriDiagnosticFactAttribute.Root, path).Replace('\\', '/'))
            .ToDictionary(path => XxHash64Ext.Hash(path.ToLowerInvariant()), path => path);
        string ResolvePath(ulong hash) => texturePaths.GetValueOrDefault(hash) ?? $"{hash:x16}";
        var metadata = SknMaterialTextureResolver.ReadMetadata(trees, ResolvePath);
        foreach (var texture in metadata.OverrideMaterials.Values.SelectMany(material => material.TextureSwaps)
            .SelectMany(swap => swap.Options).Select(option => option.TexturePath).Distinct())
        {
            output.WriteLine("SWAP " + texture);
            Assert.Contains(texture, metadata.ReferencedTexturePaths);
            Assert.True(File.Exists(Path.Combine(AhriDiagnosticFactAttribute.Root, texture)), texture);
        }
        foreach (var sample in new[]
        {
            (Submesh: "Tails", Form2: "ahri_skin_86_hol_tails_form_2_tx_cm", Form3: "ahri_skin_86_hol_tails_tx_cm"),
            (Submesh: "Form_1", Form2: "ahri_skin_86_hol_body_form_2_tx_cm", Form3: "ahri_skin_86_hol_body_form_3_tx_cm")
        })
        {
            var keys = metadata.ReferencedTexturePaths.Select(path => Path.GetFileNameWithoutExtension(path)).ToArray();
            var material = SknMaterialTextureResolver.Resolve(metadata, keys).ResolveMaterialDefinition(sample.Submesh);
            using var part = new ModelPart
            {
                MaterialDefinition = material,
                AllTextures = keys.Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(key => key,
                    _ => (System.Windows.Media.Imaging.BitmapSource)null)
            };
            foreach (int gear in new[] { 0, 1, 2, 0 })
            {
                part.EquippedGearIndex = gear;
                VfxCharacterFormSemantics.RestoreAuthoredTextures(new[] { part });
                string expected = gear == 0 ? material.BaseTextureName : gear == 1 ? sample.Form2 : sample.Form3;
                Assert.Equal(expected, part.SelectedTextureName);
                Assert.Same(material, part.MaterialDefinition);
                output.WriteLine($"BINDING {sample.Submesh} gear={gear}: {part.SelectedTextureName}");
            }
        }
        foreach (var pair in metadata.OverrideMaterials.Where(pair =>
            pair.Key.Contains("form", StringComparison.OrdinalIgnoreCase) ||
            pair.Key.Contains("tail", StringComparison.OrdinalIgnoreCase) ||
            pair.Key.Contains("fox", StringComparison.OrdinalIgnoreCase)))
        {
            var material = pair.Value;
            var preview = SknStaticMaterialResolver.ResolveAuthoredPreview(material, null);
            output.WriteLine($"MATERIAL {pair.Key}: texture={preview.BaseTextureName}; " +
                $"alphaCutoff={preview.AlphaCutoff}; uv={preview.UvRepeat}; " +
                $"blend={preview.RenderState.Blending}; animated={material.IsAnimated}; " +
                $"shader={material.ShaderHash:x8}");
            output.WriteLine("SAMPLERS: " + string.Join(",", material.Samplers.Select(
                sampler => $"{sampler.TextureName}={sampler.TexturePath}")));
            output.WriteLine("PARAMETERS: " + string.Join(",", material.Parameters.Select(
                parameter => $"{parameter.Key}={parameter.Value}")));
        }
        foreach (var material in trees.SelectMany(tree => tree.Objects.Values)
            .Where(obj => obj.ClassHash == Fnv1a.HashLower("StaticMaterialDef") &&
                obj.Properties.ContainsKey(Fnv1a.HashLower("dynamicMaterial"))))
        {
            output.WriteLine($"DYNAMIC MATERIAL {material.PathHash:x8}");
            Dump(material.Properties[Fnv1a.HashLower("dynamicMaterial")], 0);
        }
    }

    private void Dump(BinTreeProperty property, int depth)
    {
        string prefix = new string(' ', depth * 2) + $"{property.NameHash:x8} {property.GetType().Name}" +
            (property is BinTreeStruct typed ? $" class={typed.ClassHash:x8}" : string.Empty) + ": ";
        output.WriteLine(prefix + (property switch
        {
            BinTreeString value => value.Value,
            BinTreeHash value => value.Value.ToString("x8"),
            BinTreeObjectLink value => value.Value.ToString("x8"),
            BinTreeWadChunkLink value => value.Value.ToString("x16"),
            BinTreeBool value => value.Value.ToString(),
            BinTreeU8 value => value.Value.ToString(),
            BinTreeF32 value => value.Value.ToString(),
            BinTreeVector4 value => value.Value.ToString(),
            _ => string.Empty
        }));
        if (depth >= 8) return;
        if (property is BinTreeStruct structure)
            foreach (var child in structure.Properties.Values) Dump(child, depth + 1);
        if (property is BinTreeContainer container)
            foreach (var child in container.Elements) Dump(child, depth + 1);
    }

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
