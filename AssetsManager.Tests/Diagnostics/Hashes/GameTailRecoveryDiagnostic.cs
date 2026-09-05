using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
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
