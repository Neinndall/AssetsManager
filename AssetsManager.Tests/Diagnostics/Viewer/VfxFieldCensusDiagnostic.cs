using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AssetsManager.Tests.Support;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `vfx-field-census <viewer-source-dir> <out.csv> [--maps-only|--champions-only]`: walks every
    /// VfxSystemDefinitionData of the installed champion, map and common BINs and counts, per owning class, each
    /// field Riot authors and each class it nests. A field or class whose hash the viewer source never names
    /// (FNV literal or hex constant) is one the engine cannot read. Systems are counted once by path hash.
    /// </summary>
    internal static class VfxFieldCensusDiagnostic
    {
        private sealed class Usage
        {
            public int Structs;
            public readonly HashSet<uint> Systems = new();
            public string Example;
        }

        public static void Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: vfx-field-census <viewer-source-dir> <out.csv> [--maps-only|--champions-only]");
                return;
            }

            HashSet<uint> known = KnownHashes(args[0]);
            string hashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            Dictionary<uint, string> fieldNames = ReadNames(Path.Combine(hashDir, "hashes.binfields.txt"));
            Dictionary<uint, string> typeNames = ReadNames(Path.Combine(hashDir, "hashes.bintypes.txt"));
            string Field(uint hash) => fieldNames.TryGetValue(hash, out string name) ? name : $"0x{hash:x8}";
            string Type(uint hash) => typeNames.TryGetValue(hash, out string name) ? name : $"0x{hash:x8}";

            bool mapsOnly = args.Contains("--maps-only"), championsOnly = args.Contains("--champions-only");
            string install = InstalledSkins.FindInstall();
            string final = Path.Combine(install, @"Game\DATA\FINAL");
            var binPaths = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.game.txt")))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && line.EndsWith(".bin", StringComparison.Ordinal) &&
                    ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    binPaths[hash] = line[(space + 1)..];
            }

            uint systemClass = Fnv1a.HashLower("VfxSystemDefinitionData");
            uint particleName = Fnv1a.HashLower("particleName");
            var fields = new Dictionary<(uint Owner, uint Field), Usage>();
            var classes = new Dictionary<uint, Usage>();
            var seenSystems = new HashSet<uint>();
            int bins = 0;

            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client", SearchOption.AllDirectories)
                         .Where(path => (!mapsOnly && path.Contains(@"\Champions\")) ||
                                        (!championsOnly && (path.Contains(@"\Maps\") || Path.GetFileName(path).StartsWith("Common", StringComparison.OrdinalIgnoreCase))))
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                using var wad = new WadFile(wadPath);
                foreach (ulong chunk in wad.Chunks.Keys)
                {
                    if (!binPaths.TryGetValue(chunk, out string binPath) || binPath.Contains("/animations/")) continue;
                    BinTree tree;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                        tree = new BinTree(stream);
                    }
                    catch { continue; }
                    bins++;
                    foreach (BinTreeObject system in tree.Objects.Values)
                    {
                        if (system.ClassHash != systemClass || !seenSystems.Add(system.PathHash)) continue;
                        string label = system.Properties.TryGetValue(particleName, out var nameProperty) && nameProperty is BinTreeString text
                            ? $"{binPath} {text.Value}"
                            : $"{binPath} 0x{system.PathHash:x8}";
                        Walk(systemClass, system.Properties, system.PathHash, label, fields, classes);
                    }
                }
            }

            var rows = fields
                .Select(pair => (Owner: Type(pair.Key.Owner), Field: Field(pair.Key.Field), Known: known.Contains(pair.Key.Field), pair.Value))
                .OrderBy(row => row.Known).ThenByDescending(row => row.Value.Systems.Count)
                .ToList();
            var classRows = classes
                .Select(pair => (Class: Type(pair.Key), Known: known.Contains(pair.Key), pair.Value))
                .OrderBy(row => row.Known).ThenByDescending(row => row.Value.Systems.Count)
                .ToList();

            var csv = new List<string> { "kind,owner,name,known,systems,structs,example" };
            csv.AddRange(classRows.Select(row => $"class,,{row.Class},{row.Known},{row.Value.Systems.Count},{row.Value.Structs},\"{row.Value.Example}\""));
            csv.AddRange(rows.Select(row => $"field,{row.Owner},{row.Field},{row.Known},{row.Value.Systems.Count},{row.Value.Structs},\"{row.Value.Example}\""));
            File.WriteAllLines(args[1], csv);

            Console.WriteLine($"[FieldCensus] bins={bins} systems={seenSystems.Count} classes={classes.Count} fields={fields.Count} " +
                              $"unknownClasses={classRows.Count(row => !row.Known)} unknownFields={rows.Count(row => !row.Known)}");
            foreach (var row in classRows.Where(row => !row.Known).Take(40))
                Console.WriteLine($"[FieldCensus] class  {row.Class} systems={row.Value.Systems.Count} e.g. {row.Value.Example}");
            foreach (var row in rows.Where(row => !row.Known).Take(120))
                Console.WriteLine($"[FieldCensus] field  {row.Owner}.{row.Field} systems={row.Value.Systems.Count} structs={row.Value.Structs} e.g. {row.Value.Example}");
            Console.WriteLine($"[FieldCensus] csv={args[1]}");
        }

        private static void Walk(
            uint owner,
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint system,
            string label,
            Dictionary<(uint, uint), Usage> fields,
            Dictionary<uint, Usage> classes)
        {
            foreach ((uint name, BinTreeProperty property) in properties)
            {
                Count(fields, (owner, name), system, label);
                Visit(property, system, label, fields, classes);
            }
        }

        private static void Visit(
            BinTreeProperty property,
            uint system,
            string label,
            Dictionary<(uint, uint), Usage> fields,
            Dictionary<uint, Usage> classes)
        {
            switch (property)
            {
                case BinTreeStruct nested:
                    Count(classes, nested.ClassHash, system, label);
                    Walk(nested.ClassHash, nested.Properties, system, label, fields, classes);
                    break;
                case BinTreeContainer container:
                    foreach (BinTreeProperty element in container.Elements)
                        Visit(element, system, label, fields, classes);
                    break;
                case BinTreeMap map:
                    foreach (var pair in map)
                    {
                        Visit(pair.Key, system, label, fields, classes);
                        Visit(pair.Value, system, label, fields, classes);
                    }
                    break;
                case BinTreeOptional optional when optional.Value != null:
                    Visit(optional.Value, system, label, fields, classes);
                    break;
            }
        }

        private static void Count<TKey>(Dictionary<TKey, Usage> table, TKey key, uint system, string label)
        {
            if (!table.TryGetValue(key, out Usage usage))
                table[key] = usage = new Usage { Example = label };
            usage.Structs++;
            usage.Systems.Add(system);
        }

        /// <summary>Every FNV-1a name literal and 32-bit hex constant written in the viewer source.</summary>
        private static HashSet<uint> KnownHashes(string sourceDir)
        {
            var known = new HashSet<uint>();
            var literal = new Regex(@"(?:Fnv1a|HashLower|Hash)\(\s*""([^""]+)""", RegexOptions.Compiled);
            var hex = new Regex(@"0x([0-9a-fA-F]{8})\b", RegexOptions.Compiled);
            foreach (string file in Directory.EnumerateFiles(sourceDir, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                foreach (Match match in literal.Matches(text))
                    known.Add(Fnv1a.HashLower(match.Groups[1].Value));
                foreach (Match match in hex.Matches(text))
                    known.Add(uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            }
            return known;
        }

        private static Dictionary<uint, string> ReadNames(string path)
        {
            var names = new Dictionary<uint, string>();
            foreach (string line in File.ReadLines(path))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && uint.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                    names.TryAdd(hash, line[(space + 1)..]);
            }
            return names;
        }
    }
}
