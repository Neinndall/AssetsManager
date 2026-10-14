using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    // Searches "{known prefix}{sep}{value}{suffix}" for every unresolved BIN hash by rewinding
    // sep+value+suffix off the target hash and looking the remaining FNV-1a state up among all
    // known name prefixes. Single hits are expected chance noise; a (context, prefix, field,
    // transform, sep, suffix) template that repeats across distinct hashes is a real pattern.
    internal static class BinTemplateProbeDiagnostic
    {
        private static readonly string[] Suffixes = { "", "/Root", "/Resources", "/Base", "_Root", "/Tier1" };

        private sealed class Cluster
        {
            public HashSet<uint> Hashes { get; } = new();
            public List<string> Samples { get; } = new();
        }

        public static void Run(string[] args)
        {
            string root = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? @"C:\Riot Games\League of Legends (PBE)";
            string outPath = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal))?[6..];
            string wadFilter = args.FirstOrDefault(a => a.StartsWith("--wad=", StringComparison.Ordinal))?[6..];
            int minHits = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--min=", StringComparison.Ordinal))?[6..], out int m) ? m : 3;

            var directories = new DirectoriesCreator();
            string[] kinds = { "binentries", "binfields", "bintypes", "binhashes" };
            var unknown = kinds.ToDictionary(k => k, k => LoadUnknown(Path.Combine(directories.HashLabPath, $"current.{k}.txt")));
            var known = kinds.ToDictionary(k => k, k => LoadKnown(Path.Combine(directories.HashesPath, $"hashes.{k}.txt")));

            // FNV state of every known prefix: path directories, '_' stems and full names.
            var prefixes = new Dictionary<uint, string>();
            foreach (string name in known["binentries"].Values.Concat(known["binhashes"].Values))
            {
                AddPrefix(name);
                for (int index = 0; index < name.Length; index++)
                    if (name[index] is '/' or '_') AddPrefix(name[..index]);
            }
            void AddPrefix(string prefix)
            {
                if (prefix.Length >= 2) prefixes.TryAdd(Fnv1aIncremental.Hash(prefix), prefix);
            }
            Console.WriteLine($"Prefix states: {prefixes.Count}");

            var templateIds = new Dictionary<string, int>();
            var templateNames = new List<string>();
            var valueIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var valueNames = new List<string>();
            // Compact records: millions of per-state sets exhaust memory on a full install.
            var records = new List<(int Template, uint State, uint Target, int Value)>();
            var probed = new HashSet<(string Context, uint Target)>();
            long queries = 0;
            string gameDir = Path.Combine(root, "Game");
            foreach (string wadPath in Directory.EnumerateFiles(Directory.Exists(gameDir) ? gameDir : root, "*.wad.client", SearchOption.AllDirectories))
            {
                if (wadFilter != null && !Path.GetFileName(wadPath).Contains(wadFilter, StringComparison.OrdinalIgnoreCase)) continue;
                using var wad = new WadFile(wadPath);
                foreach (var (_, chunk) in wad.Chunks)
                {
                    if (chunk.Compression == WadChunkCompression.Satellite) continue;
                    BinTree tree;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        if (data.Length < 4 || Encoding.ASCII.GetString(data.Span[..4]) is not ("PROP" or "PTCH")) continue;
                        ArraySegment<byte> buffer = data.DangerousGetArray();
                        using var stream = new MemoryStream(buffer.Array!, buffer.Offset, buffer.Count, false);
                        tree = new BinTree(stream);
                    }
                    catch { continue; }

                    foreach (var (entryHash, item) in tree.Objects)
                    {
                        string className = Name("bintypes", item.ClassHash);
                        if (unknown["binentries"].Contains(entryHash))
                            Probe("entry " + className, entryHash, Values(item.Properties.Values, 2));
                        foreach (var property in item.Properties.Values)
                            Visit(property, className, item.Properties.Values, null);
                    }
                }
            }

            var output = new StringBuilder();
            void Line(string text = "") { Console.WriteLine(text); output.AppendLine(text); }
            // A family resolves each target through one template; drop weaker templates that
            // only restate the same targets (e.g. id vs leaf of the same value).
            Console.WriteLine($"Records: {records.Count}; grouping...");
            var accepted = records
                .GroupBy(r => (r.Template, r.State))
                .Select(g =>
                {
                    var cluster = new Cluster();
                    foreach (var record in g)
                        if (cluster.Hashes.Add(record.Target) && cluster.Samples.Count < 3)
                            cluster.Samples.Add($"{record.Target:x8} X + {valueNames[record.Value]}");
                    return (Key: g.Key, Value: cluster);
                })
                .Where(p => p.Value.Hashes.Count >= minHits)
                .OrderByDescending(p => p.Value.Hashes.Count)
                .ToList();
            records.Clear();
            var claimed = new HashSet<uint>();
            var families = new List<(string Template, uint State, Cluster Cluster, string Prefix)>();
            foreach (var ((templateId, state), cluster) in accepted)
            {
                if (cluster.Hashes.All(claimed.Contains)) continue;
                claimed.UnionWith(cluster.Hashes);
                families.Add((templateNames[templateId], state, cluster, NamePrefix(state)));
            }

            Line($"Queries: {queries}; template states with >= {minHits} distinct targets: {accepted.Count}; families {families.Count}");
            Line($"Targets covered: {claimed.Count} (named prefix: {families.Where(f => f.Prefix != null).Sum(f => f.Cluster.Hashes.Count)}; prefix still unknown: {families.Where(f => f.Prefix == null).Sum(f => f.Cluster.Hashes.Count)})");
            foreach (var (template, state, cluster, prefix) in families.Take(250))
            {
                // A prefix glued to the value without a separator is almost always a colliding name.
                string flag = prefix is { Length: > 0 } && !prefix.EndsWith('/') && !prefix.EndsWith('_') ? " [suspect collision]" : "";
                Line($"{cluster.Hashes.Count,6}  X={(prefix == null ? $"<state {state:x8}>" : $"\"{prefix}\"")}{flag}  {template}");
                foreach (string sample in cluster.Samples) Line($"            · {sample}");
            }
            if (outPath != null) File.WriteAllText(outPath, output.ToString());

            // Only direct known prefixes, optionally followed by a separator: anything broader
            // would name the state with chance collisions such as "Khazix_Recall_ZP_2".
            string NamePrefix(uint state)
            {
                if (state == Fnv1aIncremental.Offset) return "";
                if (prefixes.TryGetValue(state, out string direct)) return direct;
                foreach (string separator in new[] { "/", "_" })
                    if (prefixes.TryGetValue(Fnv1aIncremental.Rewind(state, separator), out string prefix))
                        return prefix + separator;
                return null;
            }

            void Visit(BinTreeProperty property, string owner, IEnumerable<BinTreeProperty> siblings, string role)
            {
                string field = role ?? Name("binfields", property.NameHash);
                switch (property)
                {
                    case BinTreeHash hash when unknown["binhashes"].Contains(hash.Value):
                        Probe($"hash {owner}.{field}", hash.Value, Values(siblings, 1));
                        break;
                    case BinTreeObjectLink link when unknown["binentries"].Contains(link.Value):
                        Probe($"link {owner}.{field}", link.Value, Values(siblings, 1));
                        break;
                    case BinTreeStruct structure:
                        string structName = Name("bintypes", structure.ClassHash);
                        foreach (var child in structure.Properties.Values) Visit(child, structName, structure.Properties.Values, null);
                        break;
                    case BinTreeContainer container:
                        foreach (var child in container.Elements) Visit(child, owner, siblings, $"{field}[]");
                        break;
                    case BinTreeOptional option when option.Value != null:
                        Visit(option.Value, owner, siblings, $"{field}?");
                        break;
                    case BinTreeMap map:
                        foreach (var pair in map)
                        {
                            IEnumerable<BinTreeProperty> valueSiblings = pair.Value is BinTreeStruct s ? s.Properties.Values : new[] { pair.Value };
                            Visit(pair.Key, owner, valueSiblings, $"{field}{{key}}");
                            Visit(pair.Value, owner, siblings, $"{field}{{value}}");
                        }
                        break;
                }
            }

            void Probe(string context, uint target, List<(string Field, string Transform, string Value)> values)
            {
                if (!probed.Add((context, target))) return;
                foreach (var (field, transform, value) in values)
                    foreach (string suffix in Suffixes)
                    {
                        // The prefix is left as an FNV state: a state shared by many targets of the same
                        // template means "fixed unknown prefix + value", named or not.
                        queries++;
                        uint state = Fnv1aIncremental.Rewind(target, value + suffix);
                        string template = $"{context} :: X + {transform}({field}) + \"{suffix}\"";
                        if (!templateIds.TryGetValue(template, out int templateId))
                        {
                            templateId = templateNames.Count;
                            templateIds[template] = templateId;
                            templateNames.Add(template);
                        }
                        string shown = value + suffix;
                        if (!valueIds.TryGetValue(shown, out int valueId))
                        {
                            valueId = valueNames.Count;
                            valueIds[shown] = valueId;
                            valueNames.Add(shown);
                        }
                        records.Add((templateId, state, target, valueId));
                    }
            }

            List<(string, string, string)> Values(IEnumerable<BinTreeProperty> properties, int depth)
            {
                var values = new List<(string, string, string)>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Collect(properties, depth, "");
                return values;

                void Collect(IEnumerable<BinTreeProperty> props, int remaining, string path)
                {
                    foreach (var property in props.Take(32))
                    {
                        string field = path + Name("binfields", property.NameHash);
                        switch (property)
                        {
                            case BinTreeString text when !string.IsNullOrWhiteSpace(text.Value) && text.Value.Length <= 200:
                                AddString(field, text.Value);
                                break;
                            case BinTreeOptional { Value: BinTreeString optionalText } when !string.IsNullOrWhiteSpace(optionalText.Value):
                                AddString(field, optionalText.Value);
                                break;
                            case BinTreeU32 u32: Add(field, "dec", u32.Value.ToString(CultureInfo.InvariantCulture)); break;
                            case BinTreeI32 i32: Add(field, "dec", i32.Value.ToString(CultureInfo.InvariantCulture)); break;
                            case BinTreeU16 u16: Add(field, "dec", u16.Value.ToString(CultureInfo.InvariantCulture)); break;
                            case BinTreeU64 u64: Add(field, "dec", u64.Value.ToString(CultureInfo.InvariantCulture)); break;
                            case BinTreeHash hash when known["binhashes"].TryGetValue(hash.Value, out string hashName): AddString(field + "#", hashName); break;
                            case BinTreeObjectLink link when known["binentries"].TryGetValue(link.Value, out string linkName): AddString(field + "@", linkName); break;
                            case BinTreeStruct structure when remaining > 1:
                                Collect(structure.Properties.Values, remaining - 1, field + ".");
                                break;
                        }
                    }
                }

                void AddString(string field, string value)
                {
                    Add(field, "id", value);
                    int slash = value.LastIndexOf('/');
                    string leaf = slash >= 0 ? value[(slash + 1)..] : value;
                    if (slash >= 0) Add(field, "leaf", leaf);
                    int dot = leaf.LastIndexOf('.');
                    if (dot > 0) Add(field, "leafstem", leaf[..dot]);
                }

                void Add(string field, string transform, string value)
                {
                    if (value.Length >= 1 && seen.Add(value)) values.Add((field, transform, value));
                }
            }

            string Name(string kind, uint hash) =>
                hash == 0 ? "-" : known[kind].TryGetValue(hash, out string name) ? name : $"{{{hash:x8}}}";
        }

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
