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

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    // Prints every place a 32-bit value occurs in the BINs of matching WADs (entry key, field name,
    // hash value, link, struct class or map key) with its owning path, to audit a proposed name.
    internal static class BinFindHashDiagnostic
    {
        public static void Run(string[] args)
        {
            string wadFilter = args.FirstOrDefault(a => a.StartsWith("--wad=", StringComparison.Ordinal))?[6..] ?? "Aatrox";
            var wanted = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal))
                .Select(a => uint.Parse(a, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToHashSet();
            var directories = new DirectoriesCreator();
            var fields = LoadKnown(Path.Combine(directories.HashesPath, "hashes.binfields.txt"));
            var types = LoadKnown(Path.Combine(directories.HashesPath, "hashes.bintypes.txt"));
            var hashes = LoadKnown(Path.Combine(directories.HashesPath, "hashes.binhashes.txt"));
            int printed = 0;

            foreach (string wadPath in Directory.EnumerateFiles(@"C:\Riot Games\League of Legends (PBE)\Game", "*.wad.client", SearchOption.AllDirectories)
                .Where(p => Path.GetFileName(p).Contains(wadFilter, StringComparison.OrdinalIgnoreCase)))
            {
                using var wad = new WadFile(wadPath);
                foreach (var (chunkHash, chunk) in wad.Chunks)
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
                        if (wanted.Contains(entryHash)) Report($"ENTRY KEY of {Name(types, item.ClassHash)}", $"{chunkHash:x16}", null);
                        foreach (var property in item.Properties.Values)
                            Visit(property, $"{chunkHash:x16}:{Name(types, item.ClassHash)}", null);
                    }
                }
            }
            Console.WriteLine(printed == 0 ? "Not found." : $"{printed} occurrences.");

            void Visit(BinTreeProperty property, string path, IEnumerable<BinTreeProperty> siblings)
            {
                string here = $"{path}.{Name(fields, property.NameHash)}";
                if (wanted.Contains(property.NameHash)) Report("FIELD NAME", here, siblings);
                switch (property)
                {
                    case BinTreeHash hash when wanted.Contains(hash.Value): Report("HASH VALUE", here, siblings); break;
                    case BinTreeObjectLink link when wanted.Contains(link.Value): Report("LINK", here, siblings); break;
                    case BinTreeStruct structure:
                        if (wanted.Contains(structure.ClassHash)) Report("STRUCT CLASS", here, null);
                        foreach (var child in structure.Properties.Values) Visit(child, $"{here}<{Name(types, structure.ClassHash)}>", structure.Properties.Values);
                        break;
                    case BinTreeContainer container:
                        foreach (var child in container.Elements) Visit(child, here + "[]", siblings);
                        break;
                    case BinTreeOptional option when option.Value != null: Visit(option.Value, here + "?", siblings); break;
                    case BinTreeMap map:
                        foreach (var pair in map)
                        {
                            var valueFields = pair.Value is BinTreeStruct s ? s.Properties.Values : null;
                            if (pair.Key is BinTreeHash key && wanted.Contains(key.Value)) Report("MAP KEY", here + "{key}", valueFields);
                            Visit(pair.Value, here + "{value}", siblings);
                        }
                        break;
                }
            }

            void Report(string what, string where, IEnumerable<BinTreeProperty> siblings)
            {
                if (++printed > 40) return;
                Console.WriteLine($"{what} at {where}");
                if (siblings == null) return;
                foreach (var sibling in siblings)
                {
                    string value = sibling switch
                    {
                        BinTreeString text => $"\"{text.Value}\"",
                        BinTreeHash hash => $"hash {hash.Value:x8} {(hashes.TryGetValue(hash.Value, out string n) ? n : "?")}",
                        _ => sibling.Type.ToString()
                    };
                    Console.WriteLine($"      {Name(fields, sibling.NameHash)} = {value}");
                }
            }
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
