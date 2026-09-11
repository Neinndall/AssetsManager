using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    /// <summary>
    /// Read-only probe that drives the production GrepWad path for a single real
    /// animation BIN chunk and reports matches, checked candidates and remaining
    /// unknowns. Used to verify animation coverage without full WAD scans.
    /// Usage: anim-bin-probe <wadPath> <binLogicalPath>
    /// </summary>
    internal static class AnimBinProbeDiagnostic
    {
        public static void Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: anim-bin-probe <wadPath> <binLogicalPath>");
                return;
            }

            string wadPath = Path.GetFullPath(args[0]);
            string logical = args[1].Replace('\\', '/').Trim().ToLowerInvariant();
            if (!File.Exists(wadPath))
            {
                Console.WriteLine($"WAD not found: {wadPath}");
                return;
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string gameHashesPath = Path.Combine(localAppData, "AssetsManager", "hashes", "hashes.game.txt");
            string unknownsPath = Path.Combine(localAppData, "AssetsManager", "hash_lab", "unknowns.game.txt");
            var gameHashFile = new HashFile(HashGuessDomain.Game, gameHashesPath);
            IReadOnlyDictionary<ulong, string> gamePaths = gameHashFile.Load();
            var unknowns = new HashSet<ulong>();
            foreach (string line in File.ReadLines(unknownsPath))
                if (ulong.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash)
                    && !gamePaths.ContainsKey(hash))
                    unknowns.Add(hash);
            Console.WriteLine($"Unknowns loaded: {unknowns.Count}");

            ulong binHash = XxHash64Ext.Hash(logical);
            byte[] data;
            ulong chunkHash;
            using (var wad = new WadFile(wadPath))
            {
                if (!wad.Chunks.TryGetValue(binHash, out WadChunk chunk))
                {
                    Console.WriteLine($"Chunk not found for '{logical}' (hash {binHash:x16}).");
                    return;
                }

                chunkHash = binHash;
                using var owner = wad.LoadChunkDecompressed(chunk);
                ArraySegment<byte> seg = owner.DangerousGetArray();
                data = seg.Array[seg.Offset..(seg.Offset + seg.Count)];
            }

            Console.WriteLine($"BIN bytes: {data.Length} for '{logical}'");
            try
            {
                using var layoutStream = new MemoryStream(data, writable: false);
                var layoutTree = new BinTree(layoutStream);
                Console.WriteLine($"Objects: {layoutTree.Objects.Count}, overrides: {layoutTree.DataOverrides.Count}");
                var fieldCounts = new Dictionary<uint, int>();
                int clipMaps = 0;
                int animLinks = 0;
                foreach (BinTreeObject obj in layoutTree.Objects.Values)
                foreach (BinTreeProperty property in Walk(obj.Properties.Values))
                {
                    fieldCounts[property.NameHash] = fieldCounts.TryGetValue(property.NameHash, out int count) ? count + 1 : 1;
                    if (property.NameHash == Fnv1a.HashLower("mClipDataMap"))
                        clipMaps++;
                    if (property.NameHash == Fnv1a.HashLower("mAnimationFilePath") &&
                        property is BinTreeWadChunkLink)
                        animLinks++;
                }

                Console.WriteLine($"mClipDataMap nodes: {clipMaps}, mAnimationFilePath links (anywhere): {animLinks}");
                foreach (var pair in fieldCounts.OrderByDescending(p => p.Value).Take(15))
                    Console.WriteLine($"  field 0x{pair.Key:x8}: x{pair.Value}");
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Layout dump failed: {exception.Message}");
            }
            var guesser = new GameHashGuesser(gameHashFile, null, _ => string.Empty);
            var matches = new List<HashGuessMatch>();
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(unknowns), m => matches.Add(m));
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                guesser.GrepWad(
                    engine,
                    new ArraySegment<byte>(data),
                    logical,
                    Path.GetFileName(wadPath),
                    chunkHash,
                    System.Threading.CancellationToken.None);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"GrepWad threw {exception.GetType().Name}: {exception.Message}");
                Console.WriteLine(exception.StackTrace);
            }

            stopwatch.Stop();

            Console.WriteLine($"Elapsed: {stopwatch.Elapsed:hh\\:mm\\:ss}");

            static IEnumerable<BinTreeProperty> Walk(IEnumerable<BinTreeProperty> properties)
            {
                foreach (BinTreeProperty property in properties)
                {
                    if (property is null)
                        continue;
                    yield return property;
                    IEnumerable<BinTreeProperty> children = property switch
                    {
                        BinTreeStruct structure => structure.Properties.Values,
                        BinTreeOptional optional when optional.Value is not null => new[] { optional.Value },
                        BinTreeContainer container => container.Elements,
                        BinTreeMap map => map.SelectMany(pair => new[] { pair.Key, pair.Value }),
                        _ => System.Array.Empty<BinTreeProperty>()
                    };
                    foreach (BinTreeProperty child in children)
                    foreach (BinTreeProperty nested in Walk(new[] { child }))
                        yield return nested;
                }
            }
            Console.WriteLine($"Checked candidates: {engine.CheckedCandidates}");
            Console.WriteLine($"Matches: {matches.Count}, remaining: {engine.RemainingUnknownCount}");
            foreach (HashGuessMatch match in matches.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase))
                Console.WriteLine($"  {match.HashText} = {match.Path} [{match.Strategy}]");
        }
    }
}
