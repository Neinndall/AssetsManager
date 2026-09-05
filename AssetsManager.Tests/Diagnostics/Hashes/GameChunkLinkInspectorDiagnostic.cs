using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class GameChunkLinkInspectorDiagnostic
    {
        public static void Run(string[] args)
        {
            string pbeRoot = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                ?? @"C:\Riot Games\League of Legends (PBE)\Game";

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string unknownsPath = Path.Combine(localAppData, "AssetsManager", "hash_lab", "unknowns.game.txt");

            if (!File.Exists(unknownsPath))
            {
                Console.WriteLine("Missing unknowns.game.txt");
                return;
            }

            var unknownHashes = new HashSet<ulong>();
            foreach (string line in File.ReadLines(unknownsPath))
                if (ulong.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong h))
                    unknownHashes.Add(h);

            Console.WriteLine("==================================================");
            Console.WriteLine($"   GAME CHUNK LINK INSPECTOR ({unknownHashes.Count:N0} unknowns)");
            Console.WriteLine("==================================================");

            string filter = args.Skip(1).FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));
            bool guess = args.Contains("--guess", StringComparer.OrdinalIgnoreCase);
            var stopwatch = Stopwatch.StartNew();
            var wads = Directory.EnumerateFiles(pbeRoot, "*.wad.client", SearchOption.AllDirectories)
                .Where(path => filter == null || path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
            int matchedLinks = 0;
            var hashFile = new HashFile(HashGuessDomain.Game,
                Path.Combine(localAppData, "AssetsManager", "hashes", "hashes.game.txt"));
            var known = hashFile.Load();
            var engine = new HashGuessEngine(HashGuessDomain.Game,
                new HashSet<ulong>(unknownHashes), match => Console.WriteLine($"MATCH {match.Hash:x16} {match.Path}"));
            var families = guess ? new GameTextureFamilyIndex(hashFile.LoadPaths(), CancellationToken.None) : null;
            var scanned = new HashSet<ulong>();

            foreach (string wadPath in wads)
            {
                string relWad = Path.GetRelativePath(pbeRoot, wadPath).Replace('\\', '/');
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (pair.Value.Compression == WadChunkCompression.Satellite) continue;
                        if (guess && !scanned.Add(pair.Key)) continue;
                        string sourcePath = known.GetValueOrDefault(pair.Key, pair.Key.ToString("x16") + ".bin");
                        if (!Path.GetExtension(sourcePath).Equals(".bin", StringComparison.OrdinalIgnoreCase)) continue;
                        using var owner = wad.LoadChunkDecompressed(pair.Value);
                        ArraySegment<byte> seg = owner.DangerousGetArray();
                        if (seg.Count < 4) continue;

                        if (!FileTypeDetector.IsPropertyBin(seg.AsSpan())) continue;

                        try
                        {
                            using var ms = new MemoryStream(seg.Array, seg.Offset, seg.Count, false);
                            var tree = new BinTree(ms);
                            if (guess)
                            {
                                families.Guess(engine, tree, sourcePath,
                                    relWad, pair.Key, CancellationToken.None);
                                continue;
                            }

                            foreach (var obj in tree.Objects.Values)
                            {
                                foreach (var prop in obj.Properties.Values)
                                {
                                    InspectProperty(obj, prop, unknownHashes, relWad, pair.Key, ref matchedLinks);
                                }
                            }
                        }
                        catch (Exception exception) { Console.WriteLine($"Inspection failed: {exception.Message}"); }
                    }
                }
                catch (Exception exception) { Console.WriteLine($"Inspection failed: {exception.Message}"); }
            }

            Console.WriteLine($"Elapsed: {stopwatch.Elapsed}");
            Console.WriteLine($"Candidates: {engine.CheckedCandidates}, matches: {engine.Matches.Count}, remaining: {engine.RemainingUnknownCount}");
            Console.WriteLine($"\nUnknown BIN chunk links: {matchedLinks}");
        }

        private static IEnumerable<string> Strings(BinTreeProperty property)
        {
            if (property is BinTreeString text) yield return text.Value;
            IEnumerable<BinTreeProperty> children = property switch
            {
                BinTreeStruct structure => structure.Properties.Values,
                BinTreeContainer container => container.Elements,
                BinTreeMap map => map.SelectMany(pair => new[] { pair.Key, pair.Value }),
                BinTreeOptional optional when optional.Value != null => new[] { optional.Value },
                _ => Array.Empty<BinTreeProperty>()
            };
            foreach (var child in children)
                foreach (string value in Strings(child)) yield return value;
        }
        private static void InspectProperty(BinTreeObject obj, BinTreeProperty prop, HashSet<ulong> unknowns, string relWad, ulong chunkHash, ref int matchedLinks)
        {
            if (prop == null) return;

            if (prop is BinTreeWadChunkLink link && unknowns.Contains(link.Value))
            {
                matchedLinks++;
                Console.WriteLine($"[LINK MATCH #{matchedLinks}]");
                Console.WriteLine($"  WAD:         {relWad}");
                Console.WriteLine($"  Chunk BIN:   {chunkHash:x16}");
                Console.WriteLine($"  Object Hash: {obj.PathHash:x8} (Class: {obj.ClassHash:x8})");
                Console.WriteLine($"  Prop Hash:   {link.NameHash:x8}");
                Console.WriteLine($"  Target Hash: {link.Value:x16}");
                foreach (var value in obj.Properties.Values.SelectMany(Strings).Take(24)) Console.WriteLine($"  Context: {value}");
            }

            switch (prop)
            {
                case BinTreeStruct str:
                    foreach (var child in str.Properties.Values)
                        InspectProperty(obj, child, unknowns, relWad, chunkHash, ref matchedLinks);
                    break;
                case BinTreeContainer cnt:
                    foreach (var child in cnt.Elements)
                        InspectProperty(obj, child, unknowns, relWad, chunkHash, ref matchedLinks);
                    break;
                case BinTreeMap map:
                    foreach (var pair in map)
                    {
                        InspectProperty(obj, pair.Key, unknowns, relWad, chunkHash, ref matchedLinks);
                        InspectProperty(obj, pair.Value, unknowns, relWad, chunkHash, ref matchedLinks);
                    }
                    break;
                case BinTreeOptional opt when opt.Value != null:
                    InspectProperty(obj, opt.Value, unknowns, relWad, chunkHash, ref matchedLinks);
                    break;
            }
        }
    }
}
