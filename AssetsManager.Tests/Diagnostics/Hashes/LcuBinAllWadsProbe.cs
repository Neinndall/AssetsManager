using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class LcuBinAllWadsProbe
    {
        private static readonly (uint Hash, string Name)[] Targets =
        {
            (0x7ec8e5ed, "UiBehavior"),
            (0xc7a79d2b, "GameScreenContainerBase"),
            (0x65c802be, "IGameScreenNode"),
            (0xe75836d4, "GameEntityTemplateLocatorPreview"),
            (0xde5dac9e, "GameEntityTemplateProxyLink"),
            (0x174c7096, "UiComponent"),
        };

        public static void Run(string pbeRoot, string hashesPath)
        {
            Run(pbeRoot, hashesPath, allWads: false);
        }

        public static void Run(string pbeRoot, string hashesPath, bool allWads)
        {
            Console.WriteLine("=== LCU BIN ALL-WADS PROBE (replica BuildBinInventory sin saltarse WADs) ===");
            Console.WriteLine($"Root: {pbeRoot}");
            Console.WriteLine($"Hashes: {hashesPath}");

            string gameDirectory = Path.Combine(pbeRoot, "Game");
            if (!Directory.Exists(gameDirectory))
            {
                Console.WriteLine($"ERROR: no existe {gameDirectory}");
                return;
            }
            string[] wads;
            if (allWads)
            {
                wads = Directory.EnumerateFiles(pbeRoot, "*.wad*", SearchOption.AllDirectories)
                    .Where(path => path.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) ||
                                   path.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                Console.WriteLine($"WADs TODOS (Game + Plugins + Config): {wads.Length}");
            }
            else
            {
                wads = Directory.EnumerateFiles(gameDirectory, "*.wad.client", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                Console.WriteLine($"WADs Game\\*.wad.client: {wads.Length} (Plugins NO se escanean, igual que el inventory)");
            }

            var wadPaths = new Dictionary<ulong, string>();
            LoadWadPaths(Path.Combine(hashesPath, "hashes.game.txt"), wadPaths);
            LoadWadPaths(Path.Combine(hashesPath, "hashes.lcu.txt"), wadPaths);
            Console.WriteLine($"Paths resueltos (game+lcu): {wadPaths.Count}");

            var found = new List<(string Wad, ulong Chunk, string Path, string Target, string Where)>();
            int binsParsed = 0, binsFailed = 0, chunksSkipped = 0;

            foreach (string wadPath in wads)
            {
                try
                {
                    using var wad = new LeagueToolkit.Core.Wad.WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        bool resolved = wadPaths.TryGetValue(pair.Key, out string path);
                        bool isBinByPath = resolved && path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
                        string sig = string.Empty;
                        if (!isBinByPath)
                        {
                            sig = GetChunkSignature(wad, pair.Value);
                            if (sig != "PROP" && sig != "PTCH")
                            {
                                chunksSkipped++;
                                continue;
                            }
                        }
                        try
                        {
                            using var data = wad.LoadChunkDecompressed(pair.Value);
                            ArraySegment<byte> buffer = data.DangerousGetArray();
                            using var stream = new MemoryStream(buffer.Array, buffer.Offset, buffer.Count, false);
                            var tree = new BinTree(stream);
                            binsParsed++;
                            var classHashes = new HashSet<uint>();
                            CollectClassHashes(tree, classHashes);
                            foreach (var target in Targets)
                            {
                                if (classHashes.Contains(target.Hash))
                                    found.Add((Path.GetFileName(wadPath), pair.Key, path ?? $"[unknown_bin_{pair.Key:x16}]", target.Name, DescribeWhere(wadPath)));
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            binsFailed++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ! WAD {Path.GetFileName(wadPath)}: {ex.Message}");
                }
            }

            Console.WriteLine($"Bins parseados: {binsParsed}, fallidos: {binsFailed}, chunks no-bin saltados: {chunksSkipped}");
            Console.WriteLine();
            if (found.Count == 0)
            {
                Console.WriteLine("RESULTADO: NINGUNO de los 6 bintypes aparece como ClassHash en ningun bin de Game\\*.wad.client.");
                Console.WriteLine("=> Si los bins se parsean bien y no estan ahi, el inventory NO puede encontrarlos en los archivos del juego.");
            }
            else
            {
                Console.WriteLine($"RESULTADO: {found.Count} coincidencias:");
                foreach (var (wad, chunk, path, target, where) in found
                    .OrderBy(f => f.Target).ThenBy(f => f.Wad))
                {
                    Console.WriteLine($"  [{target}] {wad} :: {chunk:x16} :: {path}  ({where})");
                }
                var wadGroups = found.GroupBy(f => f.Wad).Select(g => (g.Key, g.Count())).OrderBy(g => g.Item2);
                Console.WriteLine();
                Console.WriteLine("Resumen por WAD:");
                foreach (var (wad, count) in wadGroups)
                    Console.WriteLine($"  {count}x {wad}");
            }
        }

        private static string DescribeWhere(string wadPath)
        {
            return "Game/*.wad.client (SI lo escanea el inventory)";
        }

        private static void CollectClassHashes(BinTree tree, HashSet<uint> set)
        {
            foreach (var pair in tree.Objects)
            {
                if (pair.Value.ClassHash != 0) set.Add(pair.Value.ClassHash);
                foreach (var property in pair.Value.Properties.Values)
                    CollectPropertyClassHashes(property, set);
            }
            foreach (var item in tree.DataOverrides)
            {
                CollectPropertyClassHashes(item.Property, set);
            }
        }

        private static void CollectPropertyClassHashes(BinTreeProperty property, HashSet<uint> set)
        {
            switch (property)
            {
                case BinTreeStruct structure:
                    if (structure.ClassHash != 0) set.Add(structure.ClassHash);
                    foreach (var child in structure.Properties.Values) CollectPropertyClassHashes(child, set);
                    break;
                case BinTreeContainer container:
                    foreach (var child in container.Elements) CollectPropertyClassHashes(child, set);
                    break;
                case BinTreeOptional option when option.Value != null:
                    CollectPropertyClassHashes(option.Value, set);
                    break;
                case BinTreeMap map:
                    foreach (var child in map) { CollectPropertyClassHashes(child.Key, set); CollectPropertyClassHashes(child.Value, set); }
                    break;
            }
        }

        private static void LoadWadPaths(string path, Dictionary<ulong, string> result)
        {
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length > 17 &&
                    ulong.TryParse(line.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                {
                    result.TryAdd(hash, line[17..]);
                }
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
