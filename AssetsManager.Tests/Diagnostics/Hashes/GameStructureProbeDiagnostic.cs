using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.Json;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Hashes;

// Exports bounded structural evidence from an isolated state; never persists guessed paths.
internal static class GameStructureProbeDiagnostic
{
    private sealed record Frame(string Owner, string Position, Dictionary<string, string> Values);
    private sealed record Reference(string Hash, string Bin, string Wad, string Entry, bool Override, Frame[] Context);

    internal static void Run(string[] args)
    {
        string Option(string key) => args.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
        string state = Option("--state") ?? throw new ArgumentException("Provide --state=<isolated state>.");
        string output = Option("--out") ?? throw new ArgumentException("Provide --out=<JSON file>.");
        string root = Option("--root") ?? @"C:\Riot Games\League of Legends (PBE)";
        string filter = Option("--wad");
        var selectedClasses = (Option("--classes") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var known = new HashFile(HashGuessDomain.Game, Path.Combine(state, "hashes", "hashes.game.txt")).Load();
        var unknown = File.ReadLines(Path.Combine(state, "hash_lab", "unknowns.game.txt"))
            .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => ulong.Parse(s.Trim(), NumberStyles.HexNumber)).ToHashSet();
        var names = new Dictionary<uint, string>();
        foreach (string kind in new[] { "binentries", "binhashes", "binfields", "bintypes" })
        foreach (string line in File.ReadLines(Path.Combine(state, "hashes", $"hashes.{kind}.txt")))
        {
            int separator = line.IndexOf(' ');
            if (separator > 0 && uint.TryParse(line.AsSpan(0, separator), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                names.TryAdd(hash, line[(separator + 1)..]);
        }
        string Name(uint hash) => names.GetValueOrDefault(hash, $"{{{hash:x8}}}");
        var visited = new HashSet<(ulong, ulong)>();
        var references = new List<Reference>();
        var counts = new Dictionary<ulong, int>();
        var wideNames = new List<object>();
        var evidence = new List<object>();
        int bins = 0, overrides = 0, errors = 0;
        var guesser = new GameHashGuesser();
        foreach (string file in guesser.FindWads(root))
        {
            if (filter != null && !file.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            using var wad = new WadFile(file);
            if (!wad.Chunks.Keys.Any(unknown.Contains)) continue;
            foreach (var (hash, chunk) in wad.Chunks)
            {
                if (chunk.Compression == WadChunkCompression.Satellite || !visited.Add((hash, chunk.Checksum))) continue;
                if (known.TryGetValue(hash, out string path) && Path.HasExtension(path) &&
                    !path.EndsWith(".bin", StringComparison.Ordinal)) continue;
                try
                {
                    using var owner = wad.LoadChunkDecompressed(chunk);
                    if (owner.Length < 4 || !owner.Span[..4].SequenceEqual("PROP"u8) && !owner.Span[..4].SequenceEqual("PTCH"u8)) continue;
                    var data = owner.DangerousGetArray();
                    using var stream = new MemoryStream(data.Array, data.Offset, data.Count, false);
                    var tree = new BinTree(stream);
                    bins++;
                    foreach (var (entry, obj) in tree.Objects)
                    {
                        foreach (var wide in obj.Properties.Values.OfType<BinTreeHash64>())
                        {
                            if (wideNames.Count >= 100) break;
                            string value = Name(entry);
                            byte[] lower = Encoding.UTF8.GetBytes(value.ToLowerInvariant());
                            wideNames.Add(new { Bin = path, Entry = value, Field = Name(wide.NameHash), Hash = wide.Value.ToString("x16"),
                                Xxh3 = XxHash3.HashToUInt64(lower).ToString("x16"), Xxh64 = XxHash64.HashToUInt64(lower).ToString("x16") });
                        }
                        VisitScope(obj.ClassHash, obj.Properties.Values, new List<Frame>(), "root", Name(entry), false);
                    }
                    foreach (var patch in tree.DataOverrides)
                    {
                        overrides++;
                        Visit(patch.Property, new List<Frame>(), patch.PropertyPath, Name(patch.ObjectPathHash), true);
                    }

                    void VisitScope(uint type, IEnumerable<BinTreeProperty> properties, List<Frame> ancestors, string position, string entry, bool patch)
                    {
                        var props = properties.ToArray();
                        var values = new Dictionary<string, string>();
                        foreach (var property in props)
                        {
                            string value = property switch
                            {
                                BinTreeString text => text.Value.Length <= 256 ? text.Value : text.Value[..256],
                                BinTreeHash name => Name(name.Value),
                                BinTreeHash64 name => "#" + name.Value.ToString("x16"),
                                BinTreeWadChunkLink link => known.GetValueOrDefault(link.Value, "#" + link.Value.ToString("x16")),
                                _ => null
                            };
                            if (value != null) values[Name(property.NameHash)] = value;
                        }
                        var context = new List<Frame>(ancestors) { new(Name(type), position, values) };
                        if (selectedClasses.Contains(Name(type))) evidence.Add(new { Bin = path, Wad = Path.GetFileName(file), Entry = entry, Context = context.ToArray() });
                        foreach (var property in props) Visit(property, context, Name(property.NameHash), entry, patch);
                    }
                    void Visit(BinTreeProperty property, List<Frame> context, string position, string entry, bool patch)
                    {
                        switch (property)
                        {
                            case BinTreeWadChunkLink link when unknown.Contains(link.Value):
                                int count = counts.GetValueOrDefault(link.Value);
                                counts[link.Value] = count + 1;
                                if (count < 12) references.Add(new(link.Value.ToString("x16"), path ?? hash.ToString("x16"),
                                    Path.GetFileName(file), entry, patch, context.Concat(new[] { new Frame("link", position, new()) }).ToArray()));
                                break;
                            case BinTreeStruct structure: VisitScope(structure.ClassHash, structure.Properties.Values, context, position, entry, patch); break;
                            case BinTreeOptional { Value: not null } optional: Visit(optional.Value, context, position, entry, patch); break;
                            case BinTreeContainer container:
                                for (int i = 0; i < container.Elements.Count; i++) Visit(container.Elements[i], context, position + "[" + i + "]", entry, patch);
                                break;
                            case BinTreeMap map:
                                foreach (var pair in map)
                                {
                                    string key = pair.Key switch { BinTreeHash h => Name(h.Value), BinTreeHash64 h => "#" + h.Value.ToString("x16"), BinTreeString s => s.Value, _ => pair.Key.ToString() };
                                    Visit(pair.Key, context, position + "/key", entry, patch);
                                    Visit(pair.Value, context, position + "[" + key + "]", entry, patch);
                                }
                                break;
                        }
                    }
                }
                catch (Exception exception) when (exception is IOException or ArgumentException or InvalidOperationException)
                {
                    errors++;
                    if (errors <= 5) Console.WriteLine($"SKIP {path}: {exception.Message}");
                }
            }
            Console.WriteLine($"WAD {Path.GetFileName(file)}: {bins} BINs; {references.Count} references");
        }
        File.WriteAllText(output, JsonSerializer.Serialize(new { Bins = bins, Overrides = overrides, Errors = errors, References = references, WideNames = wideNames, Evidence = evidence }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"RESULT {unknown.Count} targets; {references.Select(r => r.Hash).Distinct().Count()} referenced; {references.Count(r => r.Override)} override references; no persistence");
    }
}
