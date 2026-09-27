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
    // Audits how animation event map keys relate to their event payload, per event class:
    // how many keys equal the hash of a payload string and what CommunityDragon names known keys.
    internal static class BinEventKeyAuditDiagnostic
    {
        public static void Run(string[] args)
        {
            string wadFilter = args.FirstOrDefault(a => a.StartsWith("--wad=", StringComparison.Ordinal))?[6..] ?? "";
            var directories = new DirectoriesCreator();
            var fields = LoadKnown(Path.Combine(directories.HashesPath, "hashes.binfields.txt"));
            var types = LoadKnown(Path.Combine(directories.HashesPath, "hashes.bintypes.txt"));
            var hashes = LoadKnown(Path.Combine(directories.HashesPath, "hashes.binhashes.txt"));
            var stats = new Dictionary<string, (int Total, int Known, int KeyIsPayloadString, int KnownAndPayload)>();
            var samples = new Dictionary<string, List<string>>();

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
                    foreach (var item in tree.Objects.Values)
                        foreach (var property in item.Properties.Values) Visit(property);
                }
            }

            foreach (var (eventClass, s) in stats.OrderByDescending(p => p.Value.Total))
            {
                Console.WriteLine($"{eventClass}: {s.Total} keys, {s.Known} known in CDTB, {s.KeyIsPayloadString} equal FNV(payload string), {s.KnownAndPayload} both");
                foreach (string sample in samples[eventClass]) Console.WriteLine($"      {sample}");
            }

            void Visit(BinTreeProperty property)
            {
                switch (property)
                {
                    case BinTreeMap map when Name(fields, map.NameHash) == "mEventDataMap":
                        foreach (var pair in map)
                        {
                            if (pair.Key is not BinTreeHash key || pair.Value is not BinTreeStruct value) continue;
                            string eventClass = Name(types, value.ClassHash);
                            var strings = value.Properties.Values.OfType<BinTreeString>().Select(t => (Field: Name(fields, t.NameHash), t.Value)).ToList();
                            var payload = strings.FirstOrDefault(t => Fnv1a.HashLower(t.Value) == key.Value);
                            bool known = hashes.TryGetValue(key.Value, out string knownName);
                            var s = stats.GetValueOrDefault(eventClass);
                            s.Total++;
                            if (known) s.Known++;
                            if (payload.Value != null) s.KeyIsPayloadString++;
                            if (known && payload.Value != null) s.KnownAndPayload++;
                            stats[eventClass] = s;
                            if (!samples.TryGetValue(eventClass, out var list)) samples[eventClass] = list = new List<string>();
                            if (known && list.Count < 4)
                                list.Add($"known {key.Value:x8} = \"{knownName}\"  payload: {string.Join(", ", strings.Select(t => $"{t.Field}=\"{t.Value}\""))}");
                        }
                        break;
                    case BinTreeStruct structure:
                        foreach (var child in structure.Properties.Values) Visit(child);
                        break;
                    case BinTreeContainer container:
                        foreach (var child in container.Elements) Visit(child);
                        break;
                    case BinTreeOptional option when option.Value != null: Visit(option.Value); break;
                    case BinTreeMap other:
                        foreach (var pair in other) { Visit(pair.Key); Visit(pair.Value); }
                        break;
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
