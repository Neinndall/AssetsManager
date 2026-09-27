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
    // Dumps a few unresolved entries of the requested classes with every scalar value, and the
    // unresolved keys of MapPlaceableContainer.items with their value struct, to form naming hypotheses.
    internal static class BinClassSampleDiagnostic
    {
        public static void Run(string[] args)
        {
            string wadFilter = args.FirstOrDefault(a => a.StartsWith("--wad=", StringComparison.Ordinal))?[6..] ?? "Global";
            int samples = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--samples=", StringComparison.Ordinal))?[10..], out int s) ? s : 2;
            bool includeKnown = args.Contains("--all");
            var classes = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var directories = new DirectoriesCreator();
            var types = LoadKnown(Path.Combine(directories.HashesPath, "hashes.bintypes.txt"));
            var fields = LoadKnown(Path.Combine(directories.HashesPath, "hashes.binfields.txt"));
            var entries = LoadKnown(Path.Combine(directories.HashesPath, "hashes.binentries.txt"));
            var hashes = LoadKnown(Path.Combine(directories.HashesPath, "hashes.binhashes.txt"));
            var printed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string wadPath in Directory.EnumerateFiles(@"C:\Riot Games\League of Legends (PBE)\Game", "*.wad.client", SearchOption.AllDirectories)
                .Where(p => Path.GetFileName(p).Contains(wadFilter, StringComparison.OrdinalIgnoreCase)))
            {
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
                        string className = Name(types, item.ClassHash);
                        if (!classes.Contains(className) || (!includeKnown && entries.ContainsKey(entryHash))) continue;
                        if (printed.GetValueOrDefault(className) >= samples) continue;
                        printed[className] = printed.GetValueOrDefault(className) + 1;
                        Console.WriteLine($"=== {className} entry {entryHash:x8} ({Path.GetFileName(wadPath)}) deps: {string.Join(", ", tree.Dependencies.Take(3))}");
                        foreach (var property in item.Properties.Values) Dump(property, "  ", 0);
                    }
                }
            }

            void Dump(BinTreeProperty property, string indent, int depth)
            {
                if (depth > 3) return;
                string field = Name(fields, property.NameHash);
                switch (property)
                {
                    case BinTreeString text: Console.WriteLine($"{indent}{field}: \"{text.Value}\""); break;
                    case BinTreeHash hash: Console.WriteLine($"{indent}{field}: hash {hash.Value:x8} {Resolve(hash.Value)}"); break;
                    case BinTreeObjectLink link: Console.WriteLine($"{indent}{field}: link {link.Value:x8} {entries.GetValueOrDefault(link.Value)}"); break;
                    case BinTreeU32 u32: Console.WriteLine($"{indent}{field}: u32 {u32.Value}"); break;
                    case BinTreeI32 i32: Console.WriteLine($"{indent}{field}: i32 {i32.Value}"); break;
                    case BinTreeU64 u64: Console.WriteLine($"{indent}{field}: u64 {u64.Value:x16}"); break;
                    case BinTreeWadChunkLink chunk: Console.WriteLine($"{indent}{field}: file {chunk.Value:x16}"); break;
                    case BinTreeStruct structure:
                        Console.WriteLine($"{indent}{field}: {Name(types, structure.ClassHash)} {{");
                        foreach (var child in structure.Properties.Values) Dump(child, indent + "  ", depth + 1);
                        Console.WriteLine($"{indent}}}");
                        break;
                    case BinTreeContainer container:
                        Console.WriteLine($"{indent}{field}: [{container.Elements.Count}]");
                        foreach (var child in container.Elements.Take(3)) Dump(child, indent + "  ", depth + 1);
                        break;
                    case BinTreeOptional option when option.Value != null: Dump(option.Value, indent, depth); break;
                    case BinTreeMap map:
                        Console.WriteLine($"{indent}{field}: map[{map.Count}]");
                        foreach (var pair in map.Take(3))
                        {
                            Dump(pair.Key, indent + "  key ", depth + 1);
                            Dump(pair.Value, indent + "  val ", depth + 1);
                        }
                        break;
                    default: Console.WriteLine($"{indent}{field}: {property.Type}"); break;
                }
            }

            string Resolve(uint hash) =>
                hashes.TryGetValue(hash, out string h) ? h : entries.TryGetValue(hash, out string e) ? "(entry) " + e : "?";
        }

        private static string Name(Dictionary<uint, string> catalog, uint hash) =>
            catalog.TryGetValue(hash, out string name) ? name : $"{{{hash:x8}}}";

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
