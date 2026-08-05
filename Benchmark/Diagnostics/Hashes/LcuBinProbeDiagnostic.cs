using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace BenchmarkApp.Diagnostics.Hashes
{
    internal static class LcuBinProbeDiagnostic
    {
        private static readonly HashSet<ulong> EventBusProbe = new()
        {
            0x684b0875, // AudioManagerWwise
            0xe3c2c81a, // BundleReward
            0x02ec49d8, // EventBusApBaseObject
            0x256ae2c6, // EventBusApClickElement
            0x56e39b84, // EventBusApClickNavigation
            0x9e73e21c, // EventBusApClientGameEnd
            0x8c7dfa53, // EventBusApEndOfGameObject
            0xf05bb9a0, // EventBusApGameClientPlatformInfo
            0x5e7413cb, // EventBusApMetadata
            0xd0f7cc87, // EventBusApModalViewEnd
            0xc725aa42, // EventBusApModalViewStart
            0x0c8093bd, // EventBusApObject
            0x558ed61b, // EventBusApRfc0190Scope
            0xdc20573a, // EventBusApScope
            0xf408251d, // EventBusApScreenDisplayEnd
            0xcbc0ee84, // EventBusApScreenDisplayStart
            0x253e4e01, // EventBusApScrollElement
            0x9391d52a, // EventBusApSessionStart
            0x88caeed6, // EventBusApTftClientGameEnd
            0x4a3629a2, // EventBusApTftGameClientPlatformInfo
            0xd027765c  // EventBusObject
        };

        private static readonly Dictionary<ulong, string> EventBusNames = new()
        {
            [0x684b0875] = "AudioManagerWwise",
            [0xe3c2c81a] = "BundleReward",
            [0x02ec49d8] = "EventBusApBaseObject",
            [0x256ae2c6] = "EventBusApClickElement",
            [0x56e39b84] = "EventBusApClickNavigation",
            [0x9e73e21c] = "EventBusApClientGameEnd",
            [0x8c7dfa53] = "EventBusApEndOfGameObject",
            [0xf05bb9a0] = "EventBusApGameClientPlatformInfo",
            [0x5e7413cb] = "EventBusApMetadata",
            [0xd0f7cc87] = "EventBusApModalViewEnd",
            [0xc725aa42] = "EventBusApModalViewStart",
            [0x0c8093bd] = "EventBusApObject",
            [0x558ed61b] = "EventBusApRfc0190Scope",
            [0xdc20573a] = "EventBusApScope",
            [0xf408251d] = "EventBusApScreenDisplayEnd",
            [0xcbc0ee84] = "EventBusApScreenDisplayStart",
            [0x253e4e01] = "EventBusApScrollElement",
            [0x9391d52a] = "EventBusApSessionStart",
            [0x88caeed6] = "EventBusApTftClientGameEnd",
            [0x4a3629a2] = "EventBusApTftGameClientPlatformInfo",
            [0xd027765c] = "EventBusObject"
        };

        public static void Run(string pbeRoot)
        {
            if (string.IsNullOrWhiteSpace(pbeRoot))
            {
                Console.WriteLine(
                    "Usage: dotnet run --project Benchmark/BenchmarkApp.csproj -- " +
                    "lcu-bin-probe <pbe-root>");
                return;
            }

            string hashesPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetsManager", "hashes");
            Console.WriteLine($"Hashes path: {hashesPath}");

            var knownEntries = LoadKnown(Path.Combine(hashesPath, "hashes.binentries.txt"));
            var knownFields = LoadKnown(Path.Combine(hashesPath, "hashes.binfields.txt"));
            var knownTypes = LoadKnown(Path.Combine(hashesPath, "hashes.bintypes.txt"));
            var knownHashes = LoadKnown(Path.Combine(hashesPath, "hashes.binhashes.txt"));
            var metaTypes = LoadKnown(Path.Combine(hashesPath, "hashes.metaclasses.txt"));
            var metaFields = LoadKnown(Path.Combine(hashesPath, "hashes.metafields.txt"));
            Console.WriteLine(
                $"Known catalogs: entries={knownEntries.Count} fields={knownFields.Count} " +
                $"types={knownTypes.Count} hashes={knownHashes.Count} " +
                $"meta-types={metaTypes.Count} meta-fields={metaFields.Count}");

            var observedEntries = new HashSet<ulong>();
            var observedFields = new HashSet<ulong>();
            var observedTypes = new HashSet<ulong>();
            var observedHashes = new HashSet<ulong>();

            Console.WriteLine("\nScanning LCU .wad containers (Plugins)...");
            string pluginsDir = Path.Combine(pbeRoot, "Plugins");
            string[] lcuWads = Directory.Exists(pluginsDir)
                ? Directory.EnumerateFiles(pluginsDir, "*.wad", SearchOption.AllDirectories)
                    .Where(path => path.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray()
                : Array.Empty<string>();
            Console.WriteLine($"LCU WADs found: {lcuWads.Length}");

            int scannedBins = 0;
            var binsPerWad = new List<(string Wad, int Bins)>();
            foreach (string wadPath in lcuWads)
            {
                int binsInWad = 0;
                try
                {
                    using var wad = new LeagueToolkit.Core.Wad.WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        string sig = GetChunkSignature(wad, pair.Value);
                        if (sig is not ("PROP" or "PTCH")) continue;
                        try
                        {
                            using var data = wad.LoadChunkDecompressed(pair.Value);
                            using var stream = new MemoryStream(data.Memory.ToArray(), false);
                            var tree = new BinTree(stream);
                            ReadBinInventory(tree, observedEntries, observedFields, observedTypes, observedHashes);
                            scannedBins++;
                            binsInWad++;
                        }
                        catch
                        {
                            // skip unreadable chunk
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ! {Path.GetFileName(wadPath)}: {ex.Message}");
                }
                if (binsInWad > 0) binsPerWad.Add((Path.GetFileName(wadPath), binsInWad));
            }

            Console.WriteLine($"\nBINs parsed from LCU WADs: {scannedBins}");

            var unknownsEntries = observedEntries.Where(h => !knownEntries.ContainsKey(h)).ToHashSet();
            var unknownsFields = observedFields.Where(h => !knownFields.ContainsKey(h)).ToHashSet();
            var unknownsTypes = observedTypes.Where(h => !knownTypes.ContainsKey(h)).ToHashSet();
            var unknownsHashes = observedHashes.Where(h => !knownHashes.ContainsKey(h)).ToHashSet();
            var unknownsFieldsNoMeta = unknownsFields.Where(h => !metaFields.ContainsKey(h)).ToHashSet();
            var unknownsTypesNoMeta = unknownsTypes.Where(h => !metaTypes.ContainsKey(h)).ToHashSet();

            Console.WriteLine("\n================ LCU BIN PROBE RESULTS ================");
            Console.WriteLine($"Observed   : entries={observedEntries.Count} fields={observedFields.Count} types={observedTypes.Count} hashes={observedHashes.Count}");
            Console.WriteLine($"Unknowns   : entries={unknownsEntries.Count} fields={unknownsFields.Count} types={unknownsTypes.Count} hashes={unknownsHashes.Count}");
            Console.WriteLine($"Unknowns   (meta-known subtracted): fields={unknownsFieldsNoMeta.Count} types={unknownsTypesNoMeta.Count}");

            Console.WriteLine("\n--- WADs with BIN content ---");
            foreach (var (wad, bins) in binsPerWad.OrderByDescending(item => item.Bins))
                Console.WriteLine($"  {wad}: {bins} BINs");

            Console.WriteLine("\n--- EventBus probe (21 target class hashes) ---");
            int observedCount = 0, knownCount = 0, unknownCount = 0;
            foreach (ulong hash in EventBusProbe)
            {
                bool observed = observedTypes.Contains(hash);
                bool known = knownTypes.ContainsKey(hash) || metaTypes.ContainsKey(hash);
                string status = observed ? "OBSERVED" : "not-observed";
                if (observed) observedCount++;
                if (known) knownCount++; else unknownCount++;
                Console.WriteLine(
                    $"  {hash:x8} {EventBusNames[hash],-34} {status} | known={known} " +
                    $"| unknown={observed && !known}");
            }
            Console.WriteLine(
                $"  -> observed={observedCount}/21 known(meta/catalog)={knownCount} unknown={unknownCount}");

            Console.WriteLine("\n--- Unknown BinTypes not in meta (sample up to 40) ---");
            foreach (ulong hash in unknownsTypesNoMeta.OrderBy(h => h).Take(40))
                Console.WriteLine($"  0x{hash:x8}");
            Console.WriteLine($"  ... total {unknownsTypesNoMeta.Count}");

            Console.WriteLine("\n--- Unknown BinFields not in meta (sample up to 40) ---");
            foreach (ulong hash in unknownsFieldsNoMeta.OrderBy(h => h).Take(40))
                Console.WriteLine($"  0x{hash:x8}");
            Console.WriteLine($"  ... total {unknownsFieldsNoMeta.Count}");

            Console.WriteLine("\n--- Unknown BinEntries (sample up to 40) ---");
            foreach (ulong hash in unknownsEntries.OrderBy(h => h).Take(40))
                Console.WriteLine($"  0x{hash:x8}");
            Console.WriteLine($"  ... total {unknownsEntries.Count}");
            Console.WriteLine("=======================================================");
        }

        private static Dictionary<ulong, string> LoadKnown(string path)
        {
            var result = new Dictionary<ulong, string>();
            if (!File.Exists(path)) return result;
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length <= 8 || line[8] != ' ') continue;
                if (ulong.TryParse(line.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    result.TryAdd(hash, line[9..]);
            }
            return result;
        }

        private static void ReadBinInventory(
            BinTree tree,
            HashSet<ulong> entries,
            HashSet<ulong> fields,
            HashSet<ulong> types,
            HashSet<ulong> hashes)
        {
            foreach (var pair in tree.Objects)
            {
                if (pair.Key != 0) entries.Add(pair.Key);
                if (pair.Value.ClassHash != 0) types.Add(pair.Value.ClassHash);
                foreach (var property in pair.Value.Properties.Values) VisitProperty(property, entries, fields, types, hashes);
            }
            foreach (var item in tree.DataOverrides)
            {
                if (item.ObjectPathHash != 0) entries.Add(item.ObjectPathHash);
                VisitProperty(item.Property, entries, fields, types, hashes);
            }
        }

        private static void VisitProperty(
            BinTreeProperty property,
            HashSet<ulong> entries,
            HashSet<ulong> fields,
            HashSet<ulong> types,
            HashSet<ulong> hashes)
        {
            if (property.NameHash != 0) fields.Add(property.NameHash);
            switch (property)
            {
                case BinTreeHash hash when hash.Value != 0: hashes.Add(hash.Value); break;
                case BinTreeObjectLink link when link.Value != 0: entries.Add(link.Value); break;
                case BinTreeStruct structure:
                    if (structure.ClassHash != 0) types.Add(structure.ClassHash);
                    foreach (var child in structure.Properties.Values) VisitProperty(child, entries, fields, types, hashes);
                    break;
                case BinTreeContainer container:
                    foreach (var child in container.Elements) VisitProperty(child, entries, fields, types, hashes);
                    break;
                case BinTreeOptional option when option.Value != null: VisitProperty(option.Value, entries, fields, types, hashes); break;
                case BinTreeMap map:
                    foreach (var child in map)
                    {
                        VisitProperty(child.Key, entries, fields, types, hashes);
                        VisitProperty(child.Value, entries, fields, types, hashes);
                    }
                    break;
            }
        }

        private static string GetChunkSignature(LeagueToolkit.Core.Wad.WadFile wad, LeagueToolkit.Core.Wad.WadChunk chunk)
        {
            try
            {
                using Stream stream = wad.OpenChunk(chunk);
                byte[] buffer = new byte[4];
                int read = stream.Read(buffer, 0, 4);
                if (read < 3) return string.Empty;
                if (read == 3) return Encoding.ASCII.GetString(buffer, 0, 3);
                return Encoding.ASCII.GetString(buffer);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
