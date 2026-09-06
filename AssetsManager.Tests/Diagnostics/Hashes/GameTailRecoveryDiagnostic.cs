using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    // Numerical preimages are research candidates, never persisted or promoted as resolved paths.
    internal static class GameTailRecoveryDiagnostic
    {
        private const ulong P1 = 11400714785074694791UL;
        private const ulong P2 = 14029467366897019727UL;
        private const ulong P3 = 1609587929392839161UL;
        private const ulong P4 = 9650029242287828579UL;
        private const ulong P5 = 2870177450012600261UL;
        private static readonly ulong I1 = Inverse(P1), I2 = Inverse(P2), I3 = Inverse(P3);

        internal static void Run(string[] args)
        {
            string root = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                ?? @"C:\Riot Games\League of Legends (PBE)";
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager");
            var hashFile = new HashFile(HashGuessDomain.Game, Path.Combine(local, "hashes", "hashes.game.txt"));
            var known = hashFile.Load();
            var unknown = File.ReadLines(Path.Combine(local, "hash_lab", "unknowns.game.txt"))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => ulong.Parse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToHashSet();
            var guesser = new GameHashGuesser(hashFile);
            if (args.Contains("--compare-shader-filters", StringComparer.Ordinal))
            {
                string[] original = null;
                foreach (string pattern in new[] { @".*\.[pv]s(?:_[23]_0|(?=$|[.-]))", @"\.[pv]s(?:_[23]_0|(?=$|[.-]))" })
                {
                    var regex = new System.Text.RegularExpressions.Regex(pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
                    var timer = Stopwatch.StartNew();
                    string[] selected = known.Values.Where(path => path.StartsWith("assets/shaders/", StringComparison.OrdinalIgnoreCase) || regex.IsMatch(path)).ToArray();
                    Console.WriteLine($"Filter {pattern}: {selected.Length:N0} paths; {timer.Elapsed.TotalSeconds:F3}s");
                    if (original != null && !original.SequenceEqual(selected)) throw new InvalidOperationException("Shader filter coverage changed");
                    original = selected;
                }
                Console.WriteLine("Identical shader paths and ordering. No guessing run or persistence.");
                return;
            }
            if (args.Contains("--compare-animations", StringComparer.Ordinal))
            {
                var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(unknown),
                    match => Console.WriteLine($"MATCH animation: {match.Hash:x16} {match.Path}"));
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var timer = Stopwatch.StartNew();
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                bool complete = true;
                try { guesser.SubstituteAnimationBuildListWords(engine, cancellation.Token); }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { complete = false; }
                Console.WriteLine($"Animation: {unknown.Count} unknowns; {engine.CheckedCandidates:N0} candidates; {engine.Matches.Count} matches; {timer.Elapsed.TotalSeconds:F1}s; {(GC.GetAllocatedBytesForCurrentThread() - allocated) / 1_000_000:N0} MB allocated; complete={complete}");
                Console.WriteLine("Nothing persisted. A partial run does not establish exhaustive coverage.");
                return;
            }
            if (args.Contains("--probe-animation-directions", StringComparer.Ordinal) || args.Contains("--probe-animation-basenames", StringComparer.Ordinal))
            {
                ProbeAnimationNames(root, known, unknown, guesser, args.Contains("--probe-animation-basenames", StringComparer.Ordinal));
                return;
            }
            if (args.Contains("--compare-bins", StringComparer.Ordinal))
            {
                CompareBins(hashFile, unknown);
                return;
            }
            if (args.Contains("--compare-textures", StringComparer.Ordinal))
            {
                CompareTextures(hashFile, unknown);
                return;
            }
            if (args.Contains("--compare-swordlist", StringComparer.Ordinal))
            {
                CompareSwordlist(hashFile, unknown);
                return;
            }
            if (args.Contains("--verify-patterns", StringComparer.Ordinal))
            {
                VerifyPatterns(root, known, unknown, guesser);
                return;
            }

            var words = known.Values.SelectMany(path => Path.GetFileName(path)
                .Split(new[] { '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.Ordinal);
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            long attempts = 0;
            int plausible = 0;
            Console.WriteLine($"Tail recovery: {unknown.Count} targets; numerical preimages are NOT confirmed paths.");
            foreach (string wadPath in guesser.FindWads(root))
            {
                using var wad = new WadFile(wadPath);
                var targets = new Dictionary<string, List<ulong>>(StringComparer.Ordinal);
                foreach (var pair in wad.Chunks)
                {
                    if (!unknown.Contains(pair.Key) || pair.Value.Compression == WadChunkCompression.Satellite) continue;
                    using var owner = wad.LoadChunkDecompressed(pair.Value);
                    string extension = HashGuessingService.InferChunkExtension(owner.DangerousGetArray(), detectJson: false);
                    if (extension.Length == 0) continue;
                    if (!targets.TryGetValue("." + extension, out var hashes)) targets["." + extension] = hashes = new();
                    hashes.Add(pair.Key);
                }
                if (targets.Count == 0) continue;
                Console.WriteLine($"Scanning {Path.GetFileName(wadPath)}: {targets.Values.Sum(v => v.Count)} targets");
                var templates = new HashSet<string>(StringComparer.Ordinal);
                foreach (ulong seedHash in wad.Chunks.Keys)
                {
                    if (!known.TryGetValue(seedHash, out string path) || !targets.TryGetValue(Path.GetExtension(path), out var hashes)) continue;
                    string extension = Path.GetExtension(path);
                    foreach ((string template, int offset) in Templates(path, extension))
                    {
                        if (!templates.Add(template + "|" + offset)) continue;
                        byte[] bytes = Encoding.ASCII.GetBytes(template);
                        foreach (ulong target in hashes)
                        {
                            attempts++;
                            ulong value = Recover(bytes, target, offset);
                            bool printable = true;
                            for (int i = 0; i < 8; i++)
                            {
                                byte c = (byte)(value >> (i * 8));
                                if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_' || c == '-' || c == '.'))
                                {
                                    printable = false;
                                    break;
                                }
                            }
                            if (!printable) continue;
                            byte[] candidateBytes = (byte[])bytes.Clone();
                            BinaryPrimitives.WriteUInt64LittleEndian(candidateBytes.AsSpan(offset), value);
                            string candidate = Encoding.ASCII.GetString(candidateBytes);
                            if (!candidate.EndsWith(extension, StringComparison.Ordinal) || !emitted.Add(candidate)) continue;
                            if (XxHash64Ext.Hash(candidate) != target) throw new InvalidOperationException("Invalid recovered hash");
                            bool attested = Path.GetFileNameWithoutExtension(candidate)
                                .Split(new[] { '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries)
                                .All(word => words.Contains(word) || word.All(char.IsDigit));
                            if (attested) plausible++;
                            Console.WriteLine($"{(attested ? "ATTESTED WORDS" : "UNCONFIRMED")} {target:x16} {candidate} [seed {path}]");
                        }
                    }
                }
            }
            Console.WriteLine($"Attempts: {attempts}; printable preimages: {emitted.Count}; attested-word candidates: {plausible}. Nothing persisted.");
        }

        private static void ProbeAnimationNames(string root, IReadOnlyDictionary<ulong, string> known,
            HashSet<ulong> unknown, GameHashGuesser guesser, bool fullBasenames)
        {
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(unknown),
                match => Console.WriteLine($"MATCH animation probe: {match.Hash:x16} {match.Path}"));
            var templates = new HashSet<string>(StringComparer.Ordinal);
            string[] basenames = known.Values.Where(path => path.EndsWith(".anm", StringComparison.Ordinal))
                .Select(Path.GetFileName).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var regex = new System.Text.RegularExpressions.Regex(@"(?:_?-?\d+[ab]?)?\.anm$");
            foreach (string wadPath in guesser.FindWads(root))
            {
                using var wad = new WadFile(wadPath);
                bool hasAnimation = false;
                foreach (var pair in wad.Chunks)
                {
                    if (!unknown.Contains(pair.Key) || pair.Value.Compression == WadChunkCompression.Satellite) continue;
                    using var owner = wad.LoadChunkDecompressed(pair.Value);
                    if (HashGuessingService.InferChunkExtension(owner.DangerousGetArray(), detectJson: false) == "anm")
                        hasAnimation = true;
                }
                if (!hasAnimation) continue;
                foreach (ulong hash in wad.Chunks.Keys)
                {
                    if (!known.TryGetValue(hash, out string path) || !path.EndsWith(".anm", StringComparison.Ordinal)) continue;
                    if (fullBasenames)
                    {
                        string directory = path[..(path.LastIndexOf('/') + 1)];
                        if (!templates.Add(directory)) continue;
                        foreach (string basename in basenames)
                            engine.CheckPrefixSuffix(directory, basename, HashGuessStrategy.WordlistVariant, "Animation basename probe");
                        continue;
                    }
                    string prefix = regex.Replace(path, "");
                    if (!templates.Add(prefix)) continue;
                    foreach (int number in new[] { -180, -135, -90, -45, 0, 45, 90, 135, 180 })
                    foreach (string separator in new[] { "", "_" })
                    foreach (string digits in new[] { number.ToString(CultureInfo.InvariantCulture), number.ToString("D2", CultureInfo.InvariantCulture) }.Distinct())
                    foreach (string letter in new[] { "", "a", "b" })
                        engine.CheckPrefixSuffix(prefix, separator + digits + letter + ".anm",
                            HashGuessStrategy.WordlistVariant, "Animation direction probe");
                }
            }
            Console.WriteLine($"Animation {(fullBasenames ? "basename" : "direction")} probe: {templates.Count:N0} templates; {engine.CheckedCandidates:N0} candidates; {engine.Matches.Count} matches. Nothing persisted.");
        }

        private static void CompareBins(HashFile hashFile, HashSet<ulong> unknown)
        {
            string[] allPaths = hashFile.LoadPaths().Where(path => path.EndsWith(".bin", StringComparison.Ordinal)).ToArray();
            string[] dataPaths = allPaths.Where(path => path.StartsWith("data/", StringComparison.Ordinal)).ToArray();
            var allWords = HashGuessEngine.BuildWordlist(allPaths.Select(Path.GetFileName)).Take(20_000).ToHashSet(StringComparer.Ordinal);
            var dataWords = HashGuessEngine.BuildWordlist(dataPaths.Select(Path.GetFileName)).Take(20_000).ToHashSet(StringComparer.Ordinal);
            var allFormats = HashGuesser.BuildBasenameWordFormats(allPaths, 1, 1).ToHashSet();
            var dataFormats = HashGuesser.BuildBasenameWordFormats(dataPaths, 1, 1).ToHashSet();
            Console.WriteLine($"BIN paths={allPaths.Length}, selected words={allWords.Count}, templates={allFormats.Count}; combinations={(long)allWords.Count * allFormats.Count:N0}");
            Console.WriteLine($"Data BIN paths={dataPaths.Length}, selected words={dataWords.Count}, templates={dataFormats.Count}; combinations={(long)dataWords.Count * dataFormats.Count:N0}");
            Console.WriteLine($"Data templates absent from BIN={dataFormats.Count(format => !allFormats.Contains(format))}; Data words absent from BIN={dataWords.Count(word => !allWords.Contains(word))}");
            Console.WriteLine("Data-only selected words: " + string.Join(", ", dataWords.Except(allWords).Take(30)));
            foreach (bool dataOnly in new[] { false, true })
            {
                string name = dataOnly ? "Data BIN" : "BIN";
                var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(unknown),
                    match => Console.WriteLine($"MATCH {name}: {match.Hash:x16} {match.Path}"));
                var guesser = new GameHashGuesser(hashFile);
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var timer = Stopwatch.StartNew();
                bool completed = true;
                try
                {
                    if (dataOnly) guesser.SubstituteDataBinBasenameWords(engine, cancellation.Token);
                    else guesser.SubstituteBinBasenameWords(engine, cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { completed = false; }
                Console.WriteLine($"{name}: {engine.CheckedCandidates:N0} candidates, {engine.Matches.Count} matches, {timer.Elapsed.TotalSeconds:F1}s; {(completed ? "complete" : "time-limited, NOT exhaustive")}");
            }
            Console.WriteLine("Nothing persisted. Template-word combinations are not unique candidate counts.");
        }

        private static void CompareTextures(HashFile hashFile, HashSet<ulong> unknown)
        {
            Console.WriteLine($"Texture comparison: {unknown.Count} real unknowns; independent engines; 30 seconds per method including index preparation.");
            var results = new List<(string Name, HashSet<ulong> Matches)>();
            foreach (string name in new[] { "DDS basename", "TEX basename", "Texture build-list" })
            {
                var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(unknown),
                    match => Console.WriteLine($"MATCH {name}: {match.Hash:x16} {match.Path}"));
                var guesser = new GameHashGuesser(hashFile);
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var timer = Stopwatch.StartNew();
                bool completed = true;
                try
                {
                    if (name == "DDS basename") guesser.SubstituteCharacterDdsBasenameWords(engine, cancellation.Token);
                    else if (name == "TEX basename") guesser.SubstituteCharacterTexBasenameWords(engine, cancellation.Token);
                    else guesser.SubstituteTextureBuildListWords(engine, cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { completed = false; }
                timer.Stop();
                Console.WriteLine($"{name}: {engine.CheckedCandidates:N0} candidates, {engine.Matches.Count} matches, {timer.Elapsed.TotalSeconds:F1}s; {(completed ? "complete" : "time-limited, NOT exhaustive")}");
                results.Add((name, engine.Matches.Keys.ToHashSet()));
            }
            foreach (var result in results)
            {
                var otherMatches = results.Where(other => other.Name != result.Name).SelectMany(other => other.Matches).ToHashSet();
                Console.WriteLine($"{result.Name}: {result.Matches.Count(hash => !otherMatches.Contains(hash))} exclusive matches in these measured passes");
            }
            Console.WriteLine("Nothing persisted. A time-limited zero does not establish that a method has no exclusive coverage.");
        }

        private static void CompareSwordlist(HashFile hashFile, HashSet<ulong> unknown)
        {
            Console.WriteLine($"Swordlist comparison: {unknown.Count} real unknowns; measuring coverage, overlap, and computational cost.");
            var guesser = new GameHashGuesser(hashFile);
            var knownPaths = hashFile.LoadPaths().ToList();
            var swordlist = guesser.BuildSwordlist();
            var binPaths = knownPaths.Where(p => p.EndsWith(".bin", StringComparison.Ordinal)).ToList();
            var nonBinPaths = knownPaths.Where(p => !p.EndsWith(".bin", StringComparison.Ordinal)).ToList();

            Console.WriteLine($"Corpus total paths: {knownPaths.Count:N0} (BIN: {binPaths.Count:N0}, non-BIN: {nonBinPaths.Count:N0})");
            Console.WriteLine($"Swordlist vocabulary: {swordlist.Count:N0} words extracted from .bin basenames.");

            foreach (bool excludeBin in new[] { false, true })
            {
                string label = excludeBin ? "Swordlist (non-BIN cross-domain, coordinated)" : "Swordlist (standalone full corpus)";
                var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(unknown),
                    match => Console.WriteLine($"MATCH {label}: {match.Hash:x16} {match.Path}"));
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var timer = Stopwatch.StartNew();
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                bool complete = true;
                try
                {
                    guesser.SubstituteSwordlistBasenameWords(engine, cancellation.Token, excludeCompletedBinPaths: excludeBin);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { complete = false; }
                long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Console.WriteLine($"{label}: {engine.CheckedCandidates:N0} candidates, {engine.Matches.Count} matches, {timer.Elapsed.TotalSeconds:F1}s, {bytes / 1_000_000:N0} MB allocated; {(complete ? "complete" : "time-limited, NOT exhaustive")}");
            }
            Console.WriteLine("Nothing persisted. A time-limited run does not establish exhaustive coverage.");
        }

        private static void VerifyPatterns(string root, IReadOnlyDictionary<ulong, string> known,
            HashSet<ulong> unknown, GameHashGuesser guesser)
        {
            var engine = new HashGuessEngine(HashGuessDomain.Game, new HashSet<ulong>(unknown),
                match => Console.WriteLine($"MATCH {match.Hash:x16} {match.Path}"));
            Console.WriteLine("Production regalia method:");
            guesser.GuessRegaliaAssets(engine, CancellationToken.None);
            Console.WriteLine($"Regalia matches: {engine.Matches.Count}");
            foreach (string wadPath in guesser.FindWads(root))
            {
                using var wad = new WadFile(wadPath);
                if (!wad.Chunks.Keys.Any(unknown.Contains)) continue;
                foreach (var pair in wad.Chunks)
                {
                    if (pair.Value.Compression == WadChunkCompression.Satellite ||
                        !known.TryGetValue(pair.Key, out string path) || !path.Contains("/animations/") || !path.EndsWith(".bin")) continue;
                    using var owner = wad.LoadChunkDecompressed(pair.Value);
                    guesser.GrepWad(engine, owner.DangerousGetArray(), path, wadPath, pair.Key, CancellationToken.None);
                }
            }
            Console.WriteLine($"After animation BINs: {engine.Matches.Count}");
            Console.WriteLine("Production texture build-list (diagnostic budget 500M, application budget unchanged):");
            long next = 10_000_000;
            guesser.SubstituteTextureBuildListWords(engine, CancellationToken.None, 500_000_000, count =>
            {
                if (count < next) return;
                Console.WriteLine($"Texture candidates {count}; total matches {engine.Matches.Count}");
                next += 10_000_000;
            });
            Console.WriteLine($"Production total matches: {engine.Matches.Count}, remaining {engine.RemainingUnknownCount}. Nothing persisted.");
        }

        private static IEnumerable<(string, int)> Templates(string path, string extension)
        {
            for (int offset = (path.LastIndexOf('/') / 8 + 1) * 8; offset < path.Length - extension.Length; offset += 8)
            {
                if (offset >= path.Length / 32 * 32 && offset + 8 <= path.Length)
                    yield return (path, offset);
                foreach (string suffix in extension is ".tex" or ".dds"
                    ? new[] { extension, "_tx" + extension, "_tx_cm" + extension, "_d" + extension }
                    : new[] { extension })
                {
                    string template = path[..offset] + "________" + suffix;
                    if (offset >= template.Length / 32 * 32) yield return (template, offset);
                }
            }
        }

        // Only an aligned eight-byte tail word is invertible here; full stripes mix four lanes.
        internal static ulong Recover(byte[] bytes, ulong target, int offset)
        {
            if (offset < bytes.Length / 32 * 32 || offset % 8 != 0 || offset + 8 > bytes.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
            unchecked
            {
                int length = bytes.Length, position = 0;
                ulong h;
                if (length >= 32)
                {
                    ulong a = P1 + P2, b = P2, c = 0, d = 0UL - P1;
                    while (position + 32 <= length)
                    {
                        a = Round(a, Read(bytes, position)); b = Round(b, Read(bytes, position + 8));
                        c = Round(c, Read(bytes, position + 16)); d = Round(d, Read(bytes, position + 24));
                        position += 32;
                    }
                    h = BitOperations.RotateLeft(a, 1) + BitOperations.RotateLeft(b, 7) + BitOperations.RotateLeft(c, 12) + BitOperations.RotateLeft(d, 18);
                    h = Merge(Merge(Merge(Merge(h, a), b), c), d);
                }
                else h = P5;
                h += (ulong)length;
                while (position < offset)
                {
                    h = BitOperations.RotateLeft(h ^ Round(0, Read(bytes, position)), 27) * P1 + P4;
                    position += 8;
                }
                ulong x = UndoXor(target, 32) * I3;
                x = UndoXor(x, 29) * I2;
                x = UndoXor(x, 33);
                int suffix = length / 8 * 8, tail = length - suffix;
                int singles = suffix + (tail >= 4 ? 4 : 0);
                for (int i = length - 1; i >= singles; i--)
                    x = BitOperations.RotateRight(x * I1, 11) ^ ((ulong)bytes[i] * P5);
                if (tail >= 4)
                    x = BitOperations.RotateRight((x - P3) * I2, 23) ^ ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(suffix)) * P1);
                for (int i = suffix - 8; i > offset; i -= 8)
                    x = BitOperations.RotateRight((x - P4) * I1, 27) ^ Round(0, Read(bytes, i));
                ulong round = BitOperations.RotateRight((x - P4) * I1, 27) ^ h;
                return BitOperations.RotateRight(round * I1, 31) * I2;
            }
        }

        private static ulong Read(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset));
        private static ulong Round(ulong acc, ulong value) => unchecked(BitOperations.RotateLeft(acc + value * P2, 31) * P1);
        private static ulong Merge(ulong acc, ulong value) => unchecked((acc ^ Round(0, value)) * P1 + P4);
        private static ulong UndoXor(ulong value, int shift)
        {
            ulong result = value;
            for (int i = shift; i < 64; i += shift) result ^= value >> i;
            return result;
        }
        private static ulong Inverse(ulong value)
        {
            ulong result = 1;
            for (int i = 0; i < 6; i++) result = unchecked(result * (2 - value * result));
            return result;
        }
    }
}
