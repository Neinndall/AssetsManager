using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Services.Parsers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Hashes;

// WAD checksums identify naming evidence; only the path hash can confirm a candidate.
internal static class GameOriginDiagnostic
{
    internal static void Run(string[] args)
    {
        string root = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? @"C:\Riot Games\League of Legends (PBE)";
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager");
        var catalog = new HashFile(HashGuessDomain.Game, Path.Combine(local, "hashes", "hashes.game.txt")).Load();
        var pending = File.ReadLines(Path.Combine(local, "hash_lab", "unknowns.game.txt"))
            .Where(l => ulong.TryParse(l.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            .Select(l => ulong.Parse(l.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToHashSet();
        if (args.Contains("--basic-copies", StringComparer.Ordinal) || args.Contains("--banners", StringComparer.Ordinal))
        {
            var production = new GameHashGuesser(new HashFile(HashGuessDomain.Game, Path.Combine(local, "hashes", "hashes.game.txt")));
            var dryRun = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(pending), m => Console.WriteLine($"MATCH {m.Hash:x16} {m.Path}"));
            if (args.Contains("--basic-copies", StringComparer.Ordinal)) production.GuessIdenticalCopies(dryRun, root, CancellationToken.None);
            else production.GuessEsportsBanners(dryRun, null, CancellationToken.None);
            Console.WriteLine($"Production dry run: {dryRun.Matches.Count} hits; {dryRun.CheckedCandidates:N0} candidates; nothing persisted");
            return;
        }
        string[] wads = Directory.GetFiles(Path.Combine(root, "Game"), "*.wad.client", SearchOption.AllDirectories);
        if (args.Contains("--local-names", StringComparer.Ordinal))
        {
            string filter = args.FirstOrDefault(a => a.StartsWith("--wad=", StringComparison.Ordinal))?[6..];
            var probe = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(pending), m => Console.WriteLine($"MATCH {m.Hash:x16} {m.Path}"));
            foreach (string file in wads)
            {
                if (filter != null && !file.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                using var wad = new WadFile(file);
                if (!wad.Chunks.Keys.Any(pending.Contains)) continue;
                bool textures = args.Contains("--textures", StringComparer.Ordinal);
                var paths = wad.Chunks.Keys.Where(catalog.ContainsKey).Select(h => catalog[h])
                    .Where(p => p.Contains('/') && (textures
                        ? p.EndsWith(".tex", StringComparison.Ordinal) || p.EndsWith(".dds", StringComparison.Ordinal)
                        : p.EndsWith(".anm", StringComparison.Ordinal))).ToArray();
                var dirs = paths.Select(p => p[..p.LastIndexOf('/')]).Distinct(StringComparer.Ordinal).ToArray();
                var names = paths.Select(Path.GetFileName).Distinct(StringComparer.Ordinal).ToArray();
                long before = probe.CheckedCandidates;
                int hits = probe.Matches.Count;
                foreach (string name in names)
                {
                    foreach (string directory in dirs)
                    {
                        probe.CheckNormalizedParts(directory, "/", name, HashGuessStrategy.WordlistVariant, "WAD-local basename");
                        if (probe.CheckedCandidates - before >= 10_000_000) break;
                    }
                    if (probe.CheckedCandidates - before >= 10_000_000) break;
                }
                Console.WriteLine($"LOCAL {Path.GetFileName(file)}: {probe.CheckedCandidates - before:N0} candidates, {probe.Matches.Count - hits} hits; {(probe.CheckedCandidates - before >= 10_000_000 ? "capped" : "complete")}");
            }
            Console.WriteLine($"Local names total: {probe.Matches.Count} hits; {probe.CheckedCandidates:N0} candidates; no persistence");
            return;
        }
        var physical = new Dictionary<ulong, (string Wad, ulong Checksum, int Size)>();
        var copies = new Dictionary<ulong, HashSet<string>>();
        var directories = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var virtualSprites = new Dictionary<ulong, HashSet<string>>();
        var atlasDirectories = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string file in wads)
        {
            using var wad = new WadFile(file);
            var dirs = new HashSet<string>(StringComparer.Ordinal);
            directories[file] = dirs;
            foreach (var (hash, chunk) in wad.Chunks)
            {
                if (chunk.Compression == WadChunkCompression.Satellite) continue;
                if (pending.Contains(hash)) physical.TryAdd(hash, (file, chunk.Checksum, chunk.UncompressedSize));
                if (!catalog.TryGetValue(hash, out string path)) continue;
                int slash = path.LastIndexOf('/');
                if (slash >= 0) dirs.Add(path[..slash]);
                if (chunk.Checksum != 0)
                {
                    if (!copies.TryGetValue(chunk.Checksum, out var names)) copies[chunk.Checksum] = names = new(StringComparer.Ordinal);
                    names.Add(path);
                }
                if (!path.EndsWith("atlas_info.bin", StringComparison.OrdinalIgnoreCase)) continue;
                using var data = wad.LoadChunkDecompressed(chunk);
                if (!ImageAutoAtlas.TryRead(data.Span.ToArray(), out var atlas)) continue;
                var spriteDirs = new HashSet<string>(StringComparer.Ordinal);
                foreach (var sprite in atlas.Sprites)
                {
                    if (catalog.TryGetValue(sprite.SpriteHash, out string spritePath) && spritePath.Contains('/'))
                        spriteDirs.Add(spritePath[..spritePath.LastIndexOf('/')]);
                    if (!pending.Contains(sprite.SpriteHash)) continue;
                    if (!virtualSprites.TryGetValue(sprite.SpriteHash, out var sources)) virtualSprites[sprite.SpriteHash] = sources = new(StringComparer.Ordinal);
                    sources.Add(path);
                }
                atlasDirectories[path] = spriteDirs;
            }
        }
        Console.WriteLine($"Inventory snapshot: {pending.Count}; physical {physical.Count}; atlas sprites {virtualSprites.Count}; neither {pending.Count(h => !physical.ContainsKey(h) && !virtualSprites.ContainsKey(h))}; no persistence");
        foreach (var group in physical.GroupBy(p => Path.GetFileName(p.Value.Wad)).OrderByDescending(g => g.Count()))
            Console.WriteLine($"WAD {group.Key}: {group.Count()}");
        foreach (var group in virtualSprites.SelectMany(p => p.Value.Select(source => (Hash: p.Key, Source: source))).GroupBy(p => p.Source))
            Console.WriteLine($"ATLAS {group.Key}: {group.Select(p => p.Hash).Distinct().Count()} pending; sibling folders: {string.Join(" | ", atlasDirectories[group.Key].Take(8))}");
        var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(pending),
            m => Console.WriteLine($"MATCH {m.Hash:x16} {m.Path}"));
        int withTwins = 0;
        foreach (var (hash, target) in physical)
        {
            if (target.Checksum == 0 || !copies.TryGetValue(target.Checksum, out var names)) continue;
            withTwins++;
            Console.WriteLine($"TWIN {hash:x16} {Path.GetFileName(target.Wad)} {target.Size}B ({names.Count} named copies): {string.Join(" | ", names.OrderBy(n => n, StringComparer.Ordinal).Take(6))}");
            if (target.Size <= 16 || !args.Contains("--probe-twins", StringComparer.Ordinal)) continue;
            foreach (string name in names.Select(Path.GetFileName).Distinct(StringComparer.Ordinal))
            foreach (string directory in directories[target.Wad])
                engine.CheckNormalizedParts(directory, "/", name, HashGuessStrategy.WordlistVariant, "WAD-local checksum twin");
        }
        // Sprite source folders can differ from the folder holding the atlas metadata.
        if (args.Contains("--probe-twins", StringComparer.Ordinal))
        foreach (var group in virtualSprites.SelectMany(p => p.Value.Select(source => (Hash: p.Key, Source: source))).GroupBy(p => p.Source))
        {
            var files = catalog.Values.Where(p => p.Contains('/') && atlasDirectories[group.Key].Contains(p[..p.LastIndexOf('/')]))
                .Select(Path.GetFileName).Distinct(StringComparer.Ordinal).ToArray();
            foreach (string dir in atlasDirectories[group.Key])
            foreach (string name in files)
                engine.CheckNormalizedParts(dir, "/", name, HashGuessStrategy.AtlasReference, "atlas sibling directory");
        }
        Console.WriteLine($"Checksum twins: {withTwins}; {engine.CheckedCandidates:N0} candidates; {engine.Matches.Count} exact path matches; nothing persisted");
    }
}
