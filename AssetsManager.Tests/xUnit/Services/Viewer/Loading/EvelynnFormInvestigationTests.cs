using System;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Hashing;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Loading;

public sealed class EvelynnFormInvestigationTests(InstalledChampionProjects projects)
    : IClassFixture<InstalledChampionProjects>
{
    [InstalledChampionFact("Evelynn")]
    public void Skin66StealthStateOffersAndShowsItsAuthoredAlternateGeometryForEveryGear()
    {
        string root = projects.GetRoot("Evelynn");
        string binPath = Path.Combine(root, "data", "characters", "evelynn", "skins", "skin66.bin");
        using var loader = new VfxLoadingService();
        var bundle = loader.Load(binPath, null);
        var owner = bundle.OwnerSceneContext;
        var trees = bundle.LoadedBins.Select(path =>
        {
            using var stream = File.OpenRead(path);
            return new BinTree(stream);
        }).ToArray();
        string meshPath = loader.ResolveAssetPath(owner.MeshPath, Path.GetDirectoryName(binPath), ".skn");
        var metadata = SknMaterialTextureResolver.ReadMetadata(trees, targetSknPath: meshPath);
        var materials = SknMaterialTextureResolver.Resolve(metadata, Array.Empty<string>());
        using var mesh = SkinnedMesh.ReadFromSimpleSkin(meshPath);
        using var model = new SceneModel();
        foreach (var range in mesh.Ranges)
        {
            string name = range.Material.TrimEnd('\0');
            model.AddPart(new ModelPart(name, new System.Windows.Media.Media3D.GeometryModel3D())
            {
                MaterialDefinition = materials.ResolveMaterialDefinition(SknMaterialTextureResolver.NormalizeMaterialKey(name))
            });
        }

        const string stealth = "spell:866b0d92";
        Assert.Contains(stealth, CharacterFormSemantics.GameStateBuffs(model.Parts, owner));
        Assert.Contains(owner.SubmeshConditions, condition =>
            condition.Condition.Buffs().Contains(stealth) && condition.Show.Contains(Fnv1a.HashLower("ShadowBody")));
        string[] expected =
        {
            "BodyMetal", "Dog1", "Dog2", "ShadowHorns", "LTailMetal", "RTailMetal", "ShadowBody",
            "ShadowHand", "ShadowStockings", "ShadowGlasses", "ShadowHair", "ShadowHead", "ShadowTail"
        };
        var forms = CharacterFormSemantics.FormOptions(bundle.CharacterForms, owner, model);
        Assert.Equal(3, forms.Count);
        foreach (var option in forms)
        {
            var form = option.Definition;
            var resting = CharacterFormSemantics.HiddenSubmeshes(owner.InitialHiddenSubmeshHashes, form,
                model.Parts, owner.SubmeshConditions);
            if (form.GearIndex == 1)
            {
                Assert.DoesNotContain(Fnv1a.HashLower("ShadowBody"), resting);
                Assert.Contains(Fnv1a.HashLower("Body"), resting);
                Assert.Contains(Fnv1a.HashLower("Stockings"), resting);
                string[] formVisible = model.Parts.Where(part => !resting.Contains(Fnv1a.HashLower(part.Name)))
                    .Select(part => part.Name).OrderBy(name => name).ToArray();
                Assert.Equal(expected.Where(name => name is not ("Dog1" or "Dog2")).OrderBy(name => name).ToArray(), formVisible);
            }
            else
            {
                Assert.Contains(Fnv1a.HashLower("ShadowBody"), resting);
                Assert.DoesNotContain(Fnv1a.HashLower("Body"), resting);
                Assert.DoesNotContain(Fnv1a.HashLower("Stockings"), resting);
            }
            var active = CharacterFormSemantics.HiddenSubmeshes(owner.InitialHiddenSubmeshHashes, form,
                model.Parts, owner.SubmeshConditions, GameMaterialState.From(form.GearIndex, new[] { stealth }, null));
            string[] visible = model.Parts.Where(part => !active.Contains(Fnv1a.HashLower(part.Name)))
                .Select(part => part.Name).OrderBy(name => name).ToArray();
            Assert.Equal(expected.OrderBy(name => name).ToArray(), visible);
            Assert.True(resting.SetEquals(CharacterFormSemantics.HiddenSubmeshes(owner.InitialHiddenSubmeshHashes,
                form, model.Parts, owner.SubmeshConditions, GameMaterialState.Resting)));
        }
        // Switching back after Form 2 must not retain its inferred visibility or enable gameplay buffs.
        foreach (int index in new[] { 2, 0, 2, 1, 0 })
        {
            var hidden = CharacterFormSemantics.HiddenSubmeshes(owner.InitialHiddenSubmeshHashes,
                forms[index].Definition, model.Parts, owner.SubmeshConditions);
            Assert.Equal(index != 2, hidden.Contains(Fnv1a.HashLower("ShadowBody")));
            Assert.Contains(Fnv1a.HashLower("Dog1"), hidden);
            Assert.Contains(Fnv1a.HashLower("Dog2"), hidden);
        }
    }
}
