using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using AssetsManager.Services.Parsers;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Hashes;

// Read-only measurement against installed WADs. Default: learned links only;
// --grep: all production GrepWad branches for the same known BIN cohort.
internal static class GameLearnedLinksDiagnostic
{
    public static void Run(string[] args)
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager");
        var hashFile = new HashFile(HashGuessDomain.Game, Path.Combine(local, "hashes", "hashes.game.txt"));
        var known = hashFile.Load();
        var unknown = File.ReadLines(Path.Combine(local, "hash_lab", "unknowns.game.txt"))
            .Select(l => ulong.Parse(l.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToHashSet();
        var visited = new HashSet<(ulong, ulong)>();
        var index = new GameBinLinkTemplateIndex();
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(unknown),
            m => Console.WriteLine($"MATCH {m.Hash:x16} {m.Path}"));
        var names = new Dictionary<ulong, string>();
        foreach (string catalog in new[] { "hashes.binhashes.txt", "hashes.binentries.txt", "hashes.bin.xxh364.txt" })
        foreach (string line in File.ReadLines(Path.Combine(local, "hashes", catalog)))
        {
            int space = line.IndexOf(' ');
            if (space > 0 && ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong h))
                names.TryAdd(h, line[(space + 1)..]);
        }
        string Resolve(uint h) => names.GetValueOrDefault(h, h.ToString("x8"));
        string ResolveWide(ulong h) => names.GetValueOrDefault(h, h.ToString("x16"));
        var guesser = new GameHashGuesser(hashFile, null, Resolve, ResolveWide);
        bool grep = args.Contains("--grep");
        bool atlas = args.Contains("--atlas");
        string root = args.FirstOrDefault(a => a.StartsWith("--root="))?[7..]
            ?? @"C:\Riot Games\League of Legends (PBE)\Game";
        string filter = args.FirstOrDefault(a => a.StartsWith("--wad="))?[6..];
        string output = args.FirstOrDefault(a => a.StartsWith("--out="))?[6..];
        var watch = Stopwatch.StartNew();
        bool basic = args.Contains("--basic");
        if (basic)
        {
            string characters = args.FirstOrDefault(a => a.StartsWith("--characters="))?[13..];
            guesser.GuessCharactersFiles(engine, CancellationToken.None, characters?.Split(','));
        }
        foreach (string wadPath in basic ? Array.Empty<string>() : Directory.EnumerateFiles(root, "*.wad.client", SearchOption.AllDirectories))
        {
            if (filter != null && !wadPath.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            using var wad = new WadFile(wadPath);
            if (!atlas && !wad.Chunks.Keys.Any(unknown.Contains) && filter == null) continue;
            int count = 0;
            foreach (var chunk in wad.Chunks.Values)
            {
                known.TryGetValue(chunk.PathHash, out string path);
                if (!atlas && (path == null || !path.EndsWith(".bin", StringComparison.Ordinal))) continue;
                if (atlas && (chunk.Compression == WadChunkCompression.Satellite || chunk.UncompressedSize < 24 || chunk.UncompressedSize > 32_768)) continue;
                if (!visited.Add((chunk.PathHash, chunk.Checksum))) continue;
                try
                {
                    using var data = wad.LoadChunkDecompressed(chunk);
                    if (atlas && !ImageAutoAtlas.IsAtlas(data.Span)) continue;
                    path ??= chunk.PathHash.ToString("x16");
                    if (grep || atlas)
                        guesser.GrepWad(engine, data.DangerousGetArray(), path, wadPath, chunk.PathHash, CancellationToken.None);
                    else
                    {
                        var tree = new BinTree(new MemoryStream(data.Span.ToArray(), false));
                        index.Guess(engine, tree, known, Resolve, ResolveWide, wadPath, chunk.PathHash, CancellationToken.None);
                    }
                    count++;
                }
                catch (Exception ex) { Console.WriteLine($"SKIP {path}: {ex.GetType().Name}"); }
            }
            Console.WriteLine($"WAD {Path.GetFileName(wadPath)}: {count} BINs; {engine.CheckedCandidates} candidates; {engine.Matches.Count} matches");
        }
        string summary = $"{(basic ? "Character files" : atlas ? "GrepWad atlas cohort" : grep ? "GrepWad BIN cohort" : "Learned links")}: {unknown.Count} initial; {engine.CheckedCandidates} candidates; {engine.Matches.Count} matches; {engine.RemainingUnknownCount} remaining; {watch.Elapsed}. Nothing persisted.";
        Console.WriteLine(summary);
        if (output != null)
            File.WriteAllLines(output, new[] { summary }.Concat(engine.Matches.Values.OrderBy(m => m.Path)
                .Select(m => $"{m.Hash:x16}\t{m.Path}\t{m.SourceWadPath}\t{m.SourceChunkHash:x16}")));
    }
}
