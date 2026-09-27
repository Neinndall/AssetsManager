using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Services.Viewer.Resolvers;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    internal static class CharacterFormAuditDiagnostic
    {
        internal static void Run(string[] args)
        {
            var names = new Dictionary<uint, string>();
            string hashes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetsManager", "hashes");
            foreach (string file in new[] { "hashes.binfields.txt", "hashes.bintypes.txt", "hashes.binentries.txt" })
            {
                string path = Path.Combine(hashes, file);
                if (!File.Exists(path)) continue;
                foreach (string line in File.ReadLines(path))
                {
                    int split = line.IndexOf(' ');
                    if (split > 0 && uint.TryParse(line.AsSpan(0, split), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out uint hash))
                        names.TryAdd(hash, line[(split + 1)..].Trim());
                }
            }
            string Name(uint hash) => names.GetValueOrDefault(hash) ?? $"0x{hash:x8}";
            bool inspectMesh = args.Contains("--mesh-properties");
            foreach (string path in args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)))
            {
                using var stream = File.OpenRead(path);
                var tree = new BinTree(stream);
                var data = VfxCharacterFormParser.ParseDocument(tree);
                Console.WriteLine($"[Forms] {path} objects={tree.Objects.Count} primaryGear={data.PrimaryGearUpgradePathHashes.Count} gearDefinitions={data.GearForms.Count}");
                foreach (string dependency in tree.Dependencies) Console.WriteLine($"dependency={dependency}");
                uint Hash(string field) => LeagueToolkit.Hashing.Fnv1a.HashLower(field);
                var skin = tree.Objects.Values.FirstOrDefault(obj => obj.ClassHash == Hash("SkinCharacterDataProperties"));
                var mesh = skin?.Properties.GetValueOrDefault(Hash("skinMeshProperties")) as BinTreeEmbedded;
                string baseMesh = (mesh?.Properties.GetValueOrDefault(Hash("simpleSkin")) as BinTreeString)?.Value;
                string baseSkeleton = (mesh?.Properties.GetValueOrDefault(Hash("skeleton")) as BinTreeString)?.Value;
                Console.WriteLine($"baseMesh={baseMesh} baseSkeleton={baseSkeleton}");
                if (inspectMesh) PrintMesh("base", mesh, Name);
                var owner = VfxAnimationParser.ExtractOwnerSceneContext(tree);
                foreach (var form in VfxCharacterFormParser.Resolve(new[] { data }, Name, owner))
                {
                    bool sameMesh = string.IsNullOrWhiteSpace(form.MeshPath) || string.Equals(form.MeshPath, baseMesh, StringComparison.OrdinalIgnoreCase);
                    bool sameSkeleton = string.IsNullOrWhiteSpace(form.SkeletonPath) || string.Equals(form.SkeletonPath, baseSkeleton, StringComparison.OrdinalIgnoreCase);
                    Console.WriteLine($"gear={form.GearIndex} name={form.Name} sameMesh={sameMesh} sameSkeleton={sameSkeleton} materialOverrides={form.HasMaterialOverrides}");
                    Console.WriteLine($"  mesh={form.MeshPath} skeleton={form.SkeletonPath} resources={form.ResourceMap?.Count} idle={form.OverrideIdleEffects?.Count} overrideIdle={form.EnableOverrideIdleEffects}");
                    if (!inspectMesh) continue;
                    var gear = tree.Objects.GetValueOrDefault(form.PathHash);
                    var gearData = gear?.Properties.GetValueOrDefault(0x639b0013u) as BinTreeStruct;
                    var gearMesh = gearData?.Properties.GetValueOrDefault(Hash("skinMeshProperties")) as BinTreeStruct;
                    PrintMesh($"gear {form.GearIndex}", gearMesh, Name);
                    var metadata = SknMaterialTextureResolver.ReadMetadata(
                        new[] { tree }, new[] { tree }, binEntryResolver: Name,
                        targetSknPath: form.MeshPath ?? baseMesh, gearUpgradePathHash: form.PathHash);
                    Console.WriteLine($"  resolved scale={metadata.SkinScale.ToString(CultureInfo.InvariantCulture)} hidden=[{string.Join(",", metadata.InitialHiddenSubmeshes)}] defaultTexture={metadata.DefaultTexturePath}");
                    foreach (var entry in metadata.DirectOverrideTexturePaths.OrderBy(entry => entry.Key))
                        Console.WriteLine($"  resolved texture {entry.Key}={entry.Value}");
                    foreach (var entry in metadata.OverrideMaterials.OrderBy(entry => entry.Key))
                        Console.WriteLine($"  resolved material {entry.Key}={string.Join(",", entry.Value.Samplers.Select(sampler => sampler.TexturePath))}");
                }
            }
        }
        private static void PrintMesh(string label, BinTreeStruct mesh, Func<uint, string> name)
        {
            Console.WriteLine($"[{label}] fields={mesh?.Properties.Count ?? 0}");
            if (mesh == null) return;
            foreach (var field in mesh.Properties.Values.OrderBy(field => name(field.NameHash)))
            {
                Console.WriteLine($"  {name(field.NameHash)}={Describe(field, name)}");
                if (field is not BinTreeContainer container) continue;
                foreach (var entry in container.Elements.OfType<BinTreeStruct>())
                    Console.WriteLine("    " + string.Join(" ", entry.Properties.Values.Select(
                        property => $"{name(property.NameHash)}={Describe(property, name)}")));
            }
        }

        private static string Describe(BinTreeProperty property, Func<uint, string> name) => property switch
        {
            BinTreeString text => text.Value,
            BinTreeF32 value => value.Value.ToString(CultureInfo.InvariantCulture),
            BinTreeObjectLink link => name(link.Value),
            BinTreeWadChunkLink link => $"wad:0x{link.Value:x16}",
            BinTreeContainer container => $"entries={container.Elements.Count}",
            _ => property.GetType().Name
        };
    }
}
