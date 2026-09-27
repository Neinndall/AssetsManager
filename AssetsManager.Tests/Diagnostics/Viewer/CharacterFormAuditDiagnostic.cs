using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Parsing;
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
            foreach (string path in args)
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
                foreach (var form in VfxCharacterFormParser.Resolve(new[] { data }, Name))
                {
                    bool sameMesh = string.IsNullOrWhiteSpace(form.MeshPath) || string.Equals(form.MeshPath, baseMesh, StringComparison.OrdinalIgnoreCase);
                    bool sameSkeleton = string.IsNullOrWhiteSpace(form.SkeletonPath) || string.Equals(form.SkeletonPath, baseSkeleton, StringComparison.OrdinalIgnoreCase);
                    Console.WriteLine($"gear={form.GearIndex} name={form.Name} sameMesh={sameMesh} sameSkeleton={sameSkeleton} materialOverrides={form.HasMaterialOverrides}");
                    Console.WriteLine($"  mesh={form.MeshPath} skeleton={form.SkeletonPath} resources={form.ResourceMap?.Count} idle={form.OverrideIdleEffects?.Count} overrideIdle={form.EnableOverrideIdleEffects}");
                }
            }
        }
    }
}
