using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    // For every unresolved .anm linked from an AnimationGraphData clip, reports whether the clip
    // key is named (catalog or an optional extra BIN names file) and which known .anm the same
    // graph uses, to see what the animation guesser has to work with.
    internal static class GameAnimationClipAuditDiagnostic
    {
        public static async Task Run(string[] args)
        {
            string extraNames = args.FirstOrDefault(a => a.StartsWith("--bin-names=", StringComparison.Ordinal))?[12..];
            var directories = new DirectoriesCreator();
            var unknown = File.ReadLines(Path.Combine(directories.HashLabPath, "unknowns.game.txt"))
                .Select(l => ulong.TryParse(l.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong h) ? h : 0)
                .Where(h => h != 0).ToHashSet();
            var log = new LogService(new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger());
            using var resolver = new HashResolverService(directories, log);
            await resolver.LoadAllHashesAsync();
            var extra = new Dictionary<uint, string>();
            if (extraNames != null)
                foreach (string line in File.ReadLines(extraNames))
                {
                    string[] parts = line.Split('\t');
                    if (parts.Length >= 4 && uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint h)) extra.TryAdd(h, parts[3]);
                }
            string Clip(uint hash)
            {
                if (extra.TryGetValue(hash, out string name)) return name + " (new)";
                string known = resolver.ResolveBinHash(hash);
                return string.Equals(known, hash.ToString("x8"), StringComparison.OrdinalIgnoreCase) ? null : known;
            }

            int total = 0, named = 0;
            var seen = new HashSet<ulong>();
            uint clipMap = Fnv1a.HashLower("mClipDataMap");
            foreach (string wadPath in Directory.EnumerateFiles(@"C:\Riot Games\League of Legends (PBE)\Game", "*.wad.client", SearchOption.AllDirectories))
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
                        tree = new BinTree(new MemoryStream(data.Span.ToArray(), false));
                    }
                    catch { continue; }
                    foreach (var item in tree.Objects.Values)
                    {
                        if (!item.Properties.TryGetValue(clipMap, out var property) || property is not BinTreeMap map) continue;
                        var knownFiles = new List<string>();
                        var pendingClips = new List<(uint Clip, ulong File)>();
                        foreach (var pair in map)
                        {
                            if (pair.Key is not BinTreeHash key || pair.Value is not BinTreeStruct clip) continue;
                            foreach (var link in Links(clip))
                            {
                                if (unknown.Contains(link)) pendingClips.Add((key.Value, link));
                                else if (knownFiles.Count < 3 && resolver.ResolveHash(link) is string path && path.Contains('/')) knownFiles.Add(path);
                            }
                        }
                        foreach (var (clipHash, file) in pendingClips)
                        {
                            if (!seen.Add(file)) continue;
                            total++;
                            string clipName = Clip(clipHash);
                            if (clipName != null) named++;
                            if (total <= 60)
                                Console.WriteLine($"{file:x16} clip {clipHash:x8}={clipName ?? "?"} in {resolver.ResolveHash(chunkHash)}\n      known siblings: {string.Join(" | ", knownFiles)}");
                        }
                    }
                }
            }
            Console.WriteLine($"Unresolved .anm linked from clips: {total}; clip key named: {named}");

            static IEnumerable<ulong> Links(BinTreeProperty property)
            {
                switch (property)
                {
                    case BinTreeWadChunkLink link: yield return link.Value; break;
                    case BinTreeStruct structure:
                        foreach (var child in structure.Properties.Values) foreach (var l in Links(child)) yield return l;
                        break;
                    case BinTreeContainer container:
                        foreach (var child in container.Elements) foreach (var l in Links(child)) yield return l;
                        break;
                }
            }
        }
    }
}
