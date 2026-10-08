using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    // Runs the production GAME GrepWad over an install against the current unknown inventory
    // without persisting. --bin-names=FILE adds BIN hash names ("kind<TAB>hash<TAB>...<TAB>name"
    // lines from bin-context-dryrun) to the BIN resolver the animation guesser reads clip names from.
    internal static class GameGrepDryRunDiagnostic
    {
        public static async Task Run(string[] args)
        {
            string root = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? @"C:\Riot Games\League of Legends (PBE)";
            string outPath = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal))?[6..];
            string extraNames = args.FirstOrDefault(a => a.StartsWith("--bin-names=", StringComparison.Ordinal))?[12..];
            var directories = new DirectoriesCreator();
            var log = new LogService(new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger());
            using var resolver = new HashResolverService(directories, log);
            await resolver.LoadAllHashesAsync();

            var extra = new Dictionary<uint, string>();
            if (extraNames != null)
                foreach (string line in File.ReadLines(extraNames))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length >= 4 && parts[0] == "BinHashes" &&
                        uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                        extra.TryAdd(hash, parts[3]);
                }
            string ResolveBin(uint hash) => extra.TryGetValue(hash, out string name) ? name : resolver.ResolveBinHashGeneral(hash);

            var unknown = File.ReadLines(Path.Combine(directories.HashLabPath, "unknowns.game.txt"))
                .Select(l => ulong.TryParse(l.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong h) ? h : 0)
                .Where(h => h != 0).ToHashSet();
            int initial = unknown.Count;
            var guesser = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Path.Combine(directories.HashesPath, "hashes.game.txt")), log, ResolveBin, resolver.ResolveBinXxh3);
            var engine = new HashGuessEngine(HashGuessDomain.Game, unknown, null);
            var stopwatch = Stopwatch.StartNew();

            foreach (string wadPath in guesser.FindWads(root))
            {
                using var wad = new WadFile(wadPath);
                foreach (var chunk in wad.Chunks.Values)
                {
                    if (chunk.Compression == WadChunkCompression.Satellite) continue;
                    string path = resolver.ResolveHash(chunk.PathHash);
                    string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                    if (resolver.IsKnownHash(chunk.PathHash) && extension.Length > 0 && !guesser.ShouldGrepExtension(extension)) continue;
                    try
                    {
                        using var owner = wad.LoadChunkDecompressed(chunk);
                        guesser.GrepWad(engine, owner.DangerousGetArray(), path, wadPath, chunk.PathHash, CancellationToken.None);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }
                }
            }

            var matches = engine.Matches.Values.OrderBy(m => m.Path, StringComparer.OrdinalIgnoreCase).ToList();
            var lines = new List<string>
            {
                $"GrepWad dry run: {matches.Count} resolved of {initial} unknown in {stopwatch.Elapsed:mm\\:ss} (extra BIN names: {extra.Count})"
            };
            lines.AddRange(matches.GroupBy(m => Path.GetExtension(m.Path)).OrderByDescending(g => g.Count()).Select(g => $"  {g.Key,-8} {g.Count()}"));
            lines.AddRange(matches.Select(m => $"{m.Hash:x16} {m.Path}"));
            foreach (string line in lines.Take(40)) Console.WriteLine(line);
            if (outPath != null) File.WriteAllLines(outPath, lines);
        }
    }
}
