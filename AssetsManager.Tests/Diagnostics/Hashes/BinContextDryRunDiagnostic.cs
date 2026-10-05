using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    // Runs the production BIN Context sources over an install against the current unknown
    // inventory without persisting anything, so each sub-method's yield can be reviewed first.
    internal static class BinContextDryRunDiagnostic
    {
        public static async Task Run(string[] args)
        {
            string root = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? @"C:\Riot Games\League of Legends (PBE)";
            string outPath = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal))?[6..];
            string only = args.FirstOrDefault(a => a.StartsWith("--only=", StringComparison.Ordinal))?[7..];
            var selected = only == null
                ? null
                : new HashSet<string>(only.Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

            var directories = new DirectoriesCreator();
            var log = new LogService(new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger());
            using var resolver = new HashResolverService(directories, log);
            await resolver.LoadAllHashesAsync();
            var store = new BinRstHashGuessingStore(directories);
            var targets = new Dictionary<InternalHashKind, HashSet<ulong>>();
            foreach (InternalHashKind kind in new[] { InternalHashKind.BinEntries, InternalHashKind.BinFields, InternalHashKind.BinTypes, InternalHashKind.BinHashes })
                targets[kind] = await store.LoadUnknownAsync(kind, CancellationToken.None);
            var before = targets.ToDictionary(p => p.Key, p => p.Value.Count);
            var matcher = new InternalHashEvidenceMatcher(targets);
            var casing = BinPathCasing.FromKnownNames((await store.LoadKnownAsync(InternalHashKind.BinEntries, CancellationToken.None)).Values);
            bool learnedEnabled = selected == null || selected.Contains("bin-context-learned");
            var learned = learnedEnabled ? new BinLearnedTemplateSource(resolver, casing) : null;

            var stopwatch = Stopwatch.StartNew();
            int bins = 0;
            foreach (string wadPath in Directory.EnumerateFiles(Path.Combine(root, "Game"), "*.wad.client", SearchOption.AllDirectories))
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
                        ArraySegment<byte> buffer = data.DangerousGetArray();
                        using var stream = new MemoryStream(buffer.Array!, buffer.Offset, buffer.Count, false);
                        tree = new BinTree(stream);
                    }
                    catch { continue; }
                    string path = resolver.ResolveHash(chunkHash);
                    if (path.Length == 16 && !path.Contains('/')) path = $"[unknown_bin_{chunkHash:x16}]";
                    BinContentEvidenceSource.MatchBinContentEvidence(tree, matcher, path, wadPath, resolver, selected, casing);
                    learned?.Observe(tree, matcher, path, wadPath);
                    bins++;
                }
            }
            int learnedHits = learned?.Apply(matcher) ?? 0;
            var gate = matcher.ResolveUntargetedGate();

            var output = new StringBuilder();
            void Line(string text = "") { Console.WriteLine(text); output.AppendLine(text); }
            Line($"BINs: {bins}; elapsed {stopwatch.Elapsed:mm\\:ss}; learned-template hits {learnedHits}");
            Line($"Untargeted gate: {gate.Hits} hits, {gate.ExpectedChanceMatches:F4} expected chance matches, accepted={gate.Accepted}");
            if (learned?.LastVocabularyGate is { } vocabulary)
                Line($"Vocabulary gate: {vocabulary.Hits} hits, {vocabulary.ExpectedChanceMatches:F4} expected chance matches, accepted={vocabulary.Accepted}");
            foreach (var group in matcher.Matches.GroupBy(m => m.Kind).OrderBy(g => g.Key))
                Line($"{group.Key}: {group.Count(m => m.CanPromote)} promotable, {group.Count(m => !m.CanPromote)} candidates (of {before[group.Key]} unknown)");
            if (outPath != null)
            {
                File.WriteAllText(outPath, output.ToString());
                File.WriteAllLines(Path.ChangeExtension(outPath, ".matches.txt"), matcher.Matches
                    .OrderBy(m => m.Kind).ThenBy(m => m.Value, StringComparer.OrdinalIgnoreCase)
                    .Select(m => $"{m.Kind}\t{m.HashText}\t{(m.CanPromote ? "verified" : "candidate")}\t{m.Value}\t{m.Source}"));
            }
        }
    }
}
