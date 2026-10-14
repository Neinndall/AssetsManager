using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AssetsManager.Utils;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    // Read-only census of unresolved BIN hashes grouped by the structure that owns them,
    // used to decide which families have enough context to be guessed deterministically.
    internal static class BinUnknownsAuditDiagnostic
    {
        private sealed class Group
        {
            public HashSet<uint> Unique { get; } = new();
            public long Occurrences { get; set; }
            public List<string> Samples { get; } = new();
        }

        private static readonly string[] Kinds = { "binentries", "binfields", "bintypes", "binhashes" };

        public static void Run(string[] args)
        {
            string root = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))
                ?? @"C:\Riot Games\League of Legends (PBE)";
            string outPath = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal))?[6..];
            int top = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--top=", StringComparison.Ordinal))?[6..], out int t) ? t : 40;

            var directories = new DirectoriesCreator();
            var unknown = Kinds.ToDictionary(k => k, k => LoadUnknown(Path.Combine(directories.HashLabPath, $"current.{k}.txt")));
            var known = Kinds.ToDictionary(k => k, k => LoadKnown(Path.Combine(directories.HashesPath, $"hashes.{k}.txt")));
            var output = new StringBuilder();
            void Line(string text = "") { Console.WriteLine(text); output.AppendLine(text); }

            Line($"Root: {root}");
            foreach (string kind in Kinds) Line($"{kind}: {unknown[kind].Count} unknown / {known[kind].Count} known");

            // Cross-catalog: a hash unknown in one BIN domain may already be named in another.
            Line();
            Line("== Cross-catalog names (unknown here, known in another BIN catalog) ==");
            foreach (string kind in Kinds)
                foreach (string other in Kinds.Where(o => o != kind))
                {
                    int count = unknown[kind].Count(h => known[other].ContainsKey(h));
                    if (count > 0) Line($"{kind} <- {other}: {count}");
                }

            var entries = new Dictionary<string, Group>();
            var links = new Dictionary<string, Group>();
            var hashes = new Dictionary<string, Group>();
            var fields = new Dictionary<string, Group>();
            var types = new Dictionary<string, Group>();
            int bins = 0, failures = 0;

            string gameDir = Path.Combine(root, "Game");
            foreach (string wadPath in Directory.EnumerateFiles(Directory.Exists(gameDir) ? gameDir : root, "*.wad.client", SearchOption.AllDirectories))
            {
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var (_, chunk) in wad.Chunks)
                    {
                        if (chunk.Compression == WadChunkCompression.Satellite) continue;
                        try
                        {
                            using var data = wad.LoadChunkDecompressed(chunk);
                            var span = data.Span;
                            if (span.Length < 4) continue;
                            string magic = Encoding.ASCII.GetString(span[..4]);
                            if (magic != "PROP" && magic != "PTCH") continue;
                            ArraySegment<byte> buffer = data.DangerousGetArray();
                            using var stream = new MemoryStream(buffer.Array!, buffer.Offset, buffer.Count, false);
                            Audit(new BinTree(stream), Path.GetFileName(wadPath));
                            bins++;
                        }
                        catch
                        {
                            failures++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Line($"Skipped {wadPath}: {ex.Message}");
                }
            }

            Line();
            Line($"Parsed BINs: {bins} (failures {failures})");
            Report("Unknown entry keys by class", entries);
            Report("Unknown entry links by owner field", links);
            Report("Unknown hash values by owner field", hashes);
            Report("Unknown field names by owner class", fields);
            Report("Unknown type names by usage", types);

            if (!string.IsNullOrEmpty(outPath)) File.WriteAllText(outPath, output.ToString());

            void Report(string title, Dictionary<string, Group> groups)
            {
                Line();
                Line($"== {title}: {groups.Values.SelectMany(g => g.Unique).Distinct().Count()} unique ==");
                foreach (var (context, group) in groups.OrderByDescending(p => p.Value.Unique.Count).Take(top))
                {
                    Line($"{group.Unique.Count,7} uniq {group.Occurrences,8} occ  {context}");
                    foreach (string sample in group.Samples) Line($"                        · {sample}");
                }
            }

            void Audit(BinTree tree, string wadName)
            {
                foreach (var (entryHash, item) in tree.Objects)
                {
                    string className = Name("bintypes", item.ClassHash);
                    if (unknown["binentries"].Contains(entryHash))
                        Add(entries, className, entryHash, () => $"{entryHash:x8} {wadName} {DescribeStrings(item.Properties.Values)}");
                    foreach (var property in item.Properties.Values)
                        Visit(property, className, item.ClassHash, item.Properties.Values, wadName);
                }
            }

            void Visit(BinTreeProperty property, string owner, uint ownerClass, IEnumerable<BinTreeProperty> siblings, string wadName, string role = null)
            {
                string field = role ?? Name("binfields", property.NameHash);
                string context = $"{owner}.{field}";
                if (role == null && unknown["binfields"].Contains(property.NameHash))
                    Add(fields, owner, property.NameHash, () => $"{property.NameHash:x8} {property.Type} {DescribeStrings(siblings)}");

                switch (property)
                {
                    case BinTreeHash hash when unknown["binhashes"].Contains(hash.Value):
                        Add(hashes, context, hash.Value, () => $"{hash.Value:x8} {wadName} {DescribeStrings(siblings)}");
                        break;
                    case BinTreeObjectLink link when unknown["binentries"].Contains(link.Value):
                        Add(links, context, link.Value, () => $"{link.Value:x8} {wadName} {DescribeStrings(siblings)}");
                        break;
                    case BinTreeStruct structure:
                        string structName = Name("bintypes", structure.ClassHash);
                        if (unknown["bintypes"].Contains(structure.ClassHash))
                            Add(types, context, structure.ClassHash, () => $"{structure.ClassHash:x8} fields: {string.Join(", ", structure.Properties.Keys.Take(6).Select(k => Name("binfields", k)))}");
                        foreach (var child in structure.Properties.Values)
                            Visit(child, structName, structure.ClassHash, structure.Properties.Values, wadName);
                        break;
                    case BinTreeContainer container:
                        foreach (var child in container.Elements)
                            Visit(child, owner, ownerClass, siblings, wadName, $"{field}[]");
                        break;
                    case BinTreeOptional option when option.Value != null:
                        Visit(option.Value, owner, ownerClass, siblings, wadName, $"{field}?");
                        break;
                    case BinTreeMap map:
                        foreach (var pair in map)
                        {
                            Visit(pair.Key, owner, ownerClass, siblings, wadName, $"{field}{{key}}");
                            IEnumerable<BinTreeProperty> valueSiblings = pair.Value is BinTreeStruct s ? s.Properties.Values : siblings;
                            Visit(pair.Value, owner, ownerClass, valueSiblings, wadName, $"{field}{{value}}");
                        }
                        break;
                }
            }

            void Add(Dictionary<string, Group> groups, string context, uint hash, Func<string> sample)
            {
                if (!groups.TryGetValue(context, out Group group)) groups[context] = group = new Group();
                group.Occurrences++;
                if (group.Unique.Add(hash) && group.Samples.Count < 3) group.Samples.Add(sample());
            }

            string Name(string kind, uint hash) =>
                hash == 0 ? "-" : known[kind].TryGetValue(hash, out string name) ? name : $"{{{hash:x8}}}";

            string DescribeStrings(IEnumerable<BinTreeProperty> properties)
            {
                var parts = properties.OfType<BinTreeString>()
                    .Where(s => !string.IsNullOrWhiteSpace(s.Value))
                    .Take(3)
                    .Select(s => $"{Name("binfields", s.NameHash)}=\"{Truncate(s.Value)}\"");
                return string.Join(" ", parts);
            }
        }

        private static string Truncate(string value) => value.Length <= 80 ? value : value[..77] + "...";

        private static HashSet<uint> LoadUnknown(string path)
        {
            var result = new HashSet<uint>();
            if (!File.Exists(path)) return result;
            foreach (string line in File.ReadLines(path))
                if (uint.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash)) result.Add(hash);
            return result;
        }

        private static Dictionary<uint, string> LoadKnown(string path)
        {
            var result = new Dictionary<uint, string>();
            if (!File.Exists(path)) return result;
            foreach (string line in File.ReadLines(path))
                if (line.Length > 9 && line[8] == ' ' && uint.TryParse(line.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                    result.TryAdd(hash, line[9..]);
            return result;
        }
    }
}
