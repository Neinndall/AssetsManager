using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Viewer;

internal static class CharacterFormCensusDiagnostic
{
    internal static void Run(string[] args)
    {
        string output = args.First(arg => arg.StartsWith("--out=", StringComparison.Ordinal))[6..];
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var log = new LogService(logger);
        using var hashes = new HashResolverService(new DirectoriesCreator(), log);
        hashes.LoadAllHashesAsync().GetAwaiter().GetResult();
        var projects = new List<object>();
        foreach (string root in args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)))
        {
            var trees = new Dictionary<string, BinTree>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            foreach (string path in Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).OrderBy(path => path))
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    trees.Add(path, new BinTree(stream));
                }
                catch (Exception ex) { errors.Add($"{path}: {ex.Message}"); }
            }
            var skins = new List<object>();
            int gearCount = 0, reloads = 0, repairs = 0, conditions = 0, templates = 0;
            using var loading = new VfxLoadingService(hashes);
            foreach (var entry in trees.Where(pair => pair.Value.Objects.Values.Any(obj =>
                obj.ClassHash == Fnv1a.HashLower("SkinCharacterDataProperties"))).ToArray())
            {
                try
                {
                    var bundle = loading.Load(entry.Key, log);
                    var owner = bundle?.OwnerSceneContext;
                    if (string.IsNullOrWhiteSpace(owner?.MeshPath))
                    {
                        templates++;
                        continue;
                    }
                    string folder = Path.GetDirectoryName(entry.Key);
                    BinTree[] documents = bundle.LoadedBins.Select(path =>
                    {
                        if (trees.TryGetValue(path, out var tree)) return tree;
                        using var stream = File.OpenRead(path);
                        return new BinTree(stream);
                    }).ToArray();
                    var forms = new List<object>();
                    var choices = new[] { CharacterFormDefinition.CreateBase() }.Concat(bundle.CharacterForms);
                    foreach (var form in choices)
                    {
                        var playbackOwner = bundle.CreateCharacterPlaybackView(form).OwnerSceneContext;
                        string meshPath = loading.ResolveAssetPath(playbackOwner.MeshPath, folder, ".skn");
                        string skeletonPath = loading.ResolveAssetPath(playbackOwner.SkeletonPath, folder, ".skl");
                        if (!File.Exists(meshPath) || !File.Exists(skeletonPath))
                            throw new FileNotFoundException($"Missing form mesh/skeleton: {playbackOwner.MeshPath}, {playbackOwner.SkeletonPath}");
                        using var mesh = SkinnedMesh.ReadFromSimpleSkin(meshPath);
                        var metadata = SknMaterialTextureResolver.ReadMetadata(documents, documents, hashes.ResolveHash,
                            hashes.ResolveBinEntry, meshPath, form.ReloadsModel ? form.PathHash : 0);
                        var materials = SknMaterialTextureResolver.Resolve(metadata,
                            metadata.ReferencedTexturePaths.Select(Path.GetFileNameWithoutExtension).ToArray());
                        using var model = new SceneModel();
                        foreach (var range in mesh.Ranges)
                        {
                            string name = range.Material.TrimEnd('\0');
                            model.AddPart(new ModelPart(name, new System.Windows.Media.Media3D.GeometryModel3D())
                            {
                                MaterialDefinition = materials.ResolveMaterialDefinition(SknMaterialTextureResolver.NormalizeMaterialKey(name))
                            });
                        }
                        var names = model.Parts.Select(part => part.Name).Distinct().ToArray();
                        var meshHashes = names.Select(Fnv1a.HashLower).ToHashSet();
                        var baseline = owner.InitialHiddenSubmeshHashes;
                        var hidden = CharacterFormSemantics.HiddenSubmeshes(baseline, form, model.Parts, owner.SubmeshConditions);
                        var authored = CharacterFormSemantics.HiddenSubmeshes(baseline, form, null, owner.SubmeshConditions);
                        bool repaired = form.ShowSubmeshHashes.Count > 0 && meshHashes.Any(hash => hidden.Contains(hash) != authored.Contains(hash));
                        string[] Visible(IReadOnlySet<uint> value) => names.Where(name => !value.Contains(Fnv1a.HashLower(name))).ToArray();
                        var buffs = CharacterFormSemantics.GameStateBuffs(model.Parts, owner);
                        forms.Add(new
                        {
                            form.GearIndex, form.Name, Label = CharacterFormSemantics.FormLabel(form, model),
                            PathHash = $"{form.PathHash:x8}", form.ReloadsModel,
                            Mesh = Path.GetRelativePath(root, meshPath), Repaired = repaired,
                            Visible = Visible(hidden),
                            MissingShow = form.ShowSubmeshHashes.Where(hash => !meshHashes.Contains(hash)).Select(hash => $"{hash:x8}").ToArray(),
                            MissingHide = form.HideSubmeshHashes.Where(hash => !meshHashes.Contains(hash)).Select(hash => $"{hash:x8}").ToArray(),
                            Buffs = buffs.Select(buff => new
                            {
                                Key = buff,
                                Name = GameMaterialState.TrySpellBuffHash(buff, out uint hash) ? hashes.ResolveBinEntry(hash) : buff,
                                Visible = Visible(CharacterFormSemantics.HiddenSubmeshes(baseline, form, model.Parts,
                                    owner.SubmeshConditions, GameMaterialState.From(form.GearIndex, new[] { buff }, null)))
                            }).ToArray()
                        });
                        if (!form.IsBase) gearCount++;
                        if (form.ReloadsModel) reloads++;
                        if (repaired) repairs++;
                    }
                    conditions += owner.SubmeshConditions.Count;
                    skins.Add(new
                    {
                        Bin = Path.GetRelativePath(root, entry.Key),
                        DeclaredGearLinks = CharacterFormParser.ParseDocument(entry.Value).PrimaryGearUpgradePathHashes.Count,
                        MissingDependencies = bundle.MissingDependencies.ToArray(),
                        Conditions = owner.SubmeshConditions.Select(condition => new
                        {
                            Kind = condition.Condition.Kind.ToString(), Buffs = condition.Condition.Buffs().ToArray(),
                            Show = condition.Show.Select(hash => $"{hash:x8}").ToArray(),
                            Hide = condition.Hide.Select(hash => $"{hash:x8}").ToArray()
                        }).ToArray(),
                        Forms = forms
                    });
                }
                catch (Exception ex) { errors.Add($"{entry.Key}: {ex.Message}"); }
            }
            var summary = new
            {
                Root = root, ParsedBins = trees.Count, Skins = skins.Count, Templates = templates, GearForms = gearCount,
                ReloadingForms = reloads, VisibilityConditions = conditions, RepairedForms = repairs, Errors = errors.Count
            };
            Console.WriteLine(JsonSerializer.Serialize(summary));
            projects.Add(new { Summary = summary, Skins = skins, Errors = errors });
        }
        File.WriteAllText(output, JsonSerializer.Serialize(projects, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Report: {output}");
    }
}
