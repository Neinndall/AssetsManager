using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    // For every unresolved GAME hash: which BIN references it through a WadChunkLink (entry, class,
    // field) and which known sibling links sit next to it, plus the root entries of unresolved BINs,
    // so each missing path can be derived from the structure that owns it.
    internal static class GameUnknownContextDiagnostic
    {
        private sealed class Unknown
        {
            public string Wad;
            public string Type;
            public long Size;
            public List<string> RootEntries { get; } = new();
            public List<string> References { get; } = new();
        }

        public static async Task Run(string[] args)
        {
            string root = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? @"C:\Riot Games\League of Legends (PBE)";
            string outPath = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal))?[6..];
            var directories = new DirectoriesCreator();
            var unknown = File.ReadLines(Path.Combine(directories.HashLabPath, "unknowns.game.txt"))
                .Select(l => ulong.TryParse(l.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong h) ? h : 0)
                .Where(h => h != 0)
                .ToDictionary(h => h, _ => new Unknown());
            var log = new LogService(new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger());
            using var resolver = new HashResolverService(directories, log);
            await resolver.LoadAllHashesAsync();

            string Game(ulong hash)
            {
                string path = resolver.ResolveHash(hash);
                return path.Length == 16 && !path.Contains('/') ? null : path;
            }
            string Entry(uint hash)
            {
                string name = resolver.ResolveBinEntry(hash);
                return string.Equals(name, hash.ToString("x8"), StringComparison.OrdinalIgnoreCase) ? $"{{{hash:x8}}}" : name;
            }
            string Named(Func<uint, string> resolve, uint hash)
            {
                string name = resolve(hash);
                return string.Equals(name, hash.ToString("x8"), StringComparison.OrdinalIgnoreCase) ? $"{{{hash:x8}}}" : name;
            }

            int derivedBins = 0;
            var derivedLines = new List<string>();
            foreach (string wadPath in Directory.EnumerateFiles(Path.Combine(root, "Game"), "*.wad.client", SearchOption.AllDirectories))
            {
                string wadName = Path.GetRelativePath(Path.Combine(root, "Game"), wadPath).Replace('\\', '/');
                using var wad = new WadFile(wadPath);
                foreach (var (chunkHash, chunk) in wad.Chunks)
                {
                    if (chunk.Compression == WadChunkCompression.Satellite) continue;
                    bool isUnknown = unknown.TryGetValue(chunkHash, out Unknown self);
                    byte[] bytes;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        bytes = data.Span.ToArray();
                    }
                    catch { continue; }
                    if (isUnknown && self.Wad == null)
                    {
                        self.Wad = wadName;
                        self.Size = bytes.Length;
                        self.Type = bytes.Length >= 4 ? Encoding.ASCII.GetString(bytes, 0, 4).Replace('\0', '.') : "?";
                    }
                    if (bytes.Length < 4 || Encoding.ASCII.GetString(bytes, 0, 4) is not ("PROP" or "PTCH")) continue;
                    BinTree tree;
                    try { tree = new BinTree(new MemoryStream(bytes, false)); }
                    catch { continue; }

                    string binPath = Game(chunkHash) ?? $"<unknown {chunkHash:x16}>";
                    if (isUnknown)
                    {
                        foreach (var (entryHash, item) in tree.Objects.Take(4))
                            self.RootEntries.Add($"{Named(resolver.ResolveBinType, item.ClassHash)} {Entry(entryHash)}");
                        // Root entries are named after their file: test data/<entry>.bin directly.
                        foreach (uint entryHash in tree.Objects.Keys)
                        {
                            string entry = Entry(entryHash);
                            if (entry.StartsWith('{')) continue;
                            foreach (string candidate in new[] { $"data/{entry}.bin", $"data/{entry}.inibin" })
                            {
                                if (XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(candidate.ToLowerInvariant())) != chunkHash) continue;
                                derivedBins++;
                                derivedLines.Add($"{chunkHash:x16} {candidate.ToLowerInvariant()}");
                                goto derived;
                            }
                        }
                        derived:;
                    }

                    foreach (var (entryHash, item) in tree.Objects)
                        foreach (var property in item.Properties.Values)
                            Visit(property, $"{Named(resolver.ResolveBinType, item.ClassHash)}", item.Properties.Values, entryHash, binPath);
                }
            }

            void Visit(BinTreeProperty property, string owner, IEnumerable<BinTreeProperty> siblings, uint entryHash, string binPath)
            {
                string field = Named(resolver.ResolveBinField, property.NameHash);
                switch (property)
                {
                    case BinTreeWadChunkLink link when unknown.TryGetValue(link.Value, out Unknown target) && target.References.Count < 4:
                        var knownSiblings = siblings.OfType<BinTreeWadChunkLink>()
                            .Where(s => s.Value != link.Value)
                            .Select(s => (Field: Named(resolver.ResolveBinField, s.NameHash), Path: Game(s.Value)))
                            .Where(s => s.Path != null)
                            .Take(3)
                            .Select(s => $"{s.Field}={s.Path}");
                        var strings = siblings.OfType<BinTreeString>().Take(3).Select(s => $"{Named(resolver.ResolveBinField, s.NameHash)}=\"{s.Value}\"");
                        target.References.Add($"{owner}.{field} in {Entry(entryHash)} ({binPath})\n            siblings: {string.Join(" | ", knownSiblings.Concat(strings))}");
                        break;
                    case BinTreeStruct structure:
                        foreach (var child in structure.Properties.Values)
                            Visit(child, Named(resolver.ResolveBinType, structure.ClassHash), structure.Properties.Values, entryHash, binPath);
                        break;
                    case BinTreeContainer container:
                        foreach (var child in container.Elements) Visit(child, owner, siblings, entryHash, binPath);
                        break;
                    case BinTreeOptional option when option.Value != null:
                        Visit(option.Value, owner, siblings, entryHash, binPath);
                        break;
                    case BinTreeMap map:
                        foreach (var pair in map)
                        {
                            Visit(pair.Key, owner, siblings, entryHash, binPath);
                            Visit(pair.Value, owner, pair.Value is BinTreeStruct s ? s.Properties.Values : siblings, entryHash, binPath);
                        }
                        break;
                }
            }

            var output = new StringBuilder();
            void Line(string text = "") { Console.WriteLine(text); output.AppendLine(text); }
            var located = unknown.Where(p => p.Value.Wad != null).ToList();
            Line($"Unknown GAME hashes: {unknown.Count}; located {located.Count}; referenced by a BIN link {located.Count(p => p.Value.References.Count > 0)}; unresolved BINs derivable from their root entry: {derivedBins}");
            foreach (string line in derivedLines) Line($"  DERIVED {line}");
            foreach (var group in located.GroupBy(p => p.Value.Type).OrderByDescending(g => g.Count()))
            {
                Line();
                Line($"== {group.Key}: {group.Count()} ({group.Count(p => p.Value.References.Count > 0)} referenced)");
                foreach (var (hash, info) in group.OrderBy(p => p.Value.Wad).Take(40))
                {
                    Line($"{hash:x16} {info.Size,9:N0}B {info.Wad}");
                    foreach (string entry in info.RootEntries) Line($"      root: {entry}");
                    foreach (string reference in info.References) Line($"      ref: {reference}");
                }
            }
            if (outPath != null) File.WriteAllText(outPath, output.ToString());
        }
    }
}
