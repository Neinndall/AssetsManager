using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class GameGrepAnmProfileDiagnostic
    {
        private const int MaxGlobalAnimationActions = 3_000;
        private const int AnimationPathBufferLength = 300;

        public static void Run(string[] args)
        {
            string root = GetOption(args, "--root") ?? @"C:\Riot Games\League of Legends (PBE)";
            string hashesPath = GetOption(args, "--hashes") ?? FindHashes();
            bool verify = args.Any(value => value.Equals("--verify", StringComparison.OrdinalIgnoreCase));
            if (!Directory.Exists(root) || !File.Exists(hashesPath))
            {
                Console.WriteLine("Usage: game-grep-anm-profile [--root <PBE>] [--hashes <hashes.game.txt>] [--verify]");
                Console.WriteLine($"Root: {root}");
                Console.WriteLine($"Hashes: {hashesPath ?? "not found"}");
                return;
            }

            var total = Stopwatch.StartNew();
            var stage = Stopwatch.StartNew();
            var hashFile = new HashFile(AssetsManager.Views.Models.Hashes.HashGuessDomain.Game, hashesPath);
            IReadOnlyDictionary<ulong, string> knownHashes = hashFile.Load();
            IReadOnlyList<string> knownPaths = hashFile.LoadPaths();
            PrintStage("Load known paths", stage, $"{knownPaths.Count:N0} paths");

            stage.Restart();
            IReadOnlyList<string> actions = BuildGlobalAnimationActions(knownPaths, out IReadOnlyList<string> originalActions);
            IReadOnlyList<string> addedWords = actions.Except(originalActions, StringComparer.OrdinalIgnoreCase).ToList();
            PrintStage("Build global actions", stage, $"{originalActions.Count:N0} actions + {addedWords.Count:N0} words");

            stage.Restart();
            IReadOnlyDictionary<string, IReadOnlyList<string>> skinMap = BuildChampionSkinMap(knownPaths);
            PrintStage("Build champion skin map", stage, $"{skinMap.Count:N0} characters");

            stage.Restart();
            string[] wadPaths = Directory.EnumerateFiles(
                    Directory.Exists(Path.Combine(root, "Game")) ? Path.Combine(root, "Game") : root,
                    "*.wad.client",
                    SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            IReadOnlyList<string> wadNames = FindEligibleWadNames(wadPaths, knownHashes);
            PrintStage("Find BIN-bearing WADs", stage, $"{wadNames.Count:N0}/{wadPaths.Length:N0} WADs");

            stage.Restart();
            byte[][] actionBytes = actions.Select(Encoding.UTF8.GetBytes).ToArray();
            PrintStage("Encode action cache", stage, $"{actionBytes.Sum(bytes => bytes.Length):N0} bytes");

            stage.Restart();
            (long candidates, ulong checksum) = HashAnimationMatrix(wadNames, skinMap, actions, actionBytes, verify);
            PrintStage("Hash complete ANM matrix", stage, $"{candidates:N0} candidates | checksum {checksum:x16}{(verify ? " | verified" : string.Empty)}");

            stage.Restart();
            IReadOnlyList<string> knownAddedPaths = FindKnownAddedPaths(wadNames, skinMap, addedWords, knownHashes);
            PrintStage("Check added words", stage, $"{knownAddedPaths.Count:N0} known paths");
            foreach (string path in knownAddedPaths.Take(20)) Console.WriteLine($"  {path}");

            total.Stop();
            Console.WriteLine($"Total diagnostic time: {total.Elapsed:hh\\:mm\\:ss\\.fff}");
        }

        private static (long Candidates, ulong Checksum) HashAnimationMatrix(
            IReadOnlyList<string> wadNames,
            IReadOnlyDictionary<string, IReadOnlyList<string>> skinMap,
            IReadOnlyList<string> actions,
            IReadOnlyList<byte[]> actionBytes,
            bool verify)
        {
            Span<byte> buffer = stackalloc byte[Encoding.UTF8.GetMaxByteCount(AnimationPathBufferLength)];
            long candidates = 0;
            ulong checksum = 0;
            foreach (string wadName in wadNames)
            {
                string[] aliases = wadName.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ||
                                   wadName.StartsWith("pet", StringComparison.OrdinalIgnoreCase)
                    ? new[] { wadName }
                    : new[] { wadName, $"jade_{wadName}" };

                foreach (string alias in aliases)
                foreach (string skin in GetChampionSkinNames(alias, skinMap))
                {
                    string prefix = $"assets/characters/{alias}/skins/{skin}/animations/";
                    if (prefix.Length + 100 > AnimationPathBufferLength)
                    {
                        foreach (string action in actions)
                        {
                            checksum ^= XxHash64Ext.Hash($"{prefix}{action}.anm");
                            candidates++;
                        }
                        continue;
                    }

                    int prefixByteLength = Encoding.UTF8.GetBytes(prefix, buffer);
                    for (int actionIndex = 0; actionIndex < actions.Count; actionIndex++)
                    {
                        string action = actions[actionIndex];
                        int totalLength = prefix.Length + action.Length + 4;
                        ulong optimizedHash;
                        if (totalLength <= AnimationPathBufferLength)
                        {
                            byte[] encodedAction = actionBytes[actionIndex];
                            encodedAction.CopyTo(buffer[prefixByteLength..]);
                            ".anm"u8.CopyTo(buffer[(prefixByteLength + encodedAction.Length)..]);
                            optimizedHash = XxHash64.HashToUInt64(buffer[..(prefixByteLength + encodedAction.Length + 4)]);
                        }
                        else
                        {
                            optimizedHash = XxHash64Ext.Hash($"{prefix}{action}.anm");
                        }
                        checksum ^= optimizedHash;

                        if (verify)
                        {
                            ulong legacyHash = XxHash64Ext.Hash($"{prefix}{action}.anm");
                            if (optimizedHash != legacyHash)
                                throw new InvalidOperationException($"Hash mismatch for '{prefix}{action}.anm'.");
                        }
                        candidates++;
                    }
                }
            }
            return (candidates, checksum);
        }

        private static IReadOnlyList<string> BuildGlobalAnimationActions(
            IReadOnlyList<string> knownPaths,
            out IReadOnlyList<string> originalActions)
        {
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var regex = new Regex(
                @"^(?:assets|data)/characters/(?<char>[^/]+)/(?:skins/(?<skin>[^/]+)|themes/(?<theme>[^/]+)|animations)/animations/(?<file>[^/]+)\.anm$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);
            foreach (string path in knownPaths)
            {
                if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ||
                    !path.Contains("/characters/", StringComparison.OrdinalIgnoreCase)) continue;
                string basename = Path.GetFileName(path);
                if (basename.Length <= 4) continue;
                string stem = basename[..^4].ToLowerInvariant();
                int dot = stem.IndexOf('.');
                if (dot > 0) stem = stem[..dot];
                if (stem.Length < 2 || stem.Length > 100 || stem.All(char.IsDigit) ||
                    (stem.Length == 16 && stem.All(char.IsAsciiHexDigitLower)) ||
                    stem.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) continue;

                Add(stem);
                Match match = regex.Match(path);
                if (match.Success)
                {
                    string character = match.Groups["char"].Value.ToLowerInvariant();
                    string container = match.Groups["skin"].Success ? match.Groups["skin"].Value.ToLowerInvariant() : match.Groups["theme"].Value.ToLowerInvariant();
                    if (container.Length > 0 && stem.StartsWith($"{character}_{container}_", StringComparison.OrdinalIgnoreCase)) Add(stem[(character.Length + container.Length + 2)..]);
                    else if (stem.StartsWith($"{character}_", StringComparison.OrdinalIgnoreCase)) Add(stem[(character.Length + 1)..]);
                    else if (container.Length > 0 && stem.StartsWith($"{container}_", StringComparison.OrdinalIgnoreCase)) Add(stem[(container.Length + 1)..]);
                }

                int separator = stem.IndexOf('_');
                while (separator >= 0 && separator < stem.Length - 1)
                {
                    string suffix = stem[(separator + 1)..];
                    if (suffix.Length is >= 2 and <= 100 && !suffix.All(char.IsDigit)) Add(suffix);
                    separator = stem.IndexOf('_', separator + 1);
                }
            }

            List<string> actions = counts.OrderByDescending(pair => pair.Value)
                .Take(MaxGlobalAnimationActions)
                .Select(pair => pair.Key)
                .ToList();
            originalActions = actions;

            var expandedActions = new HashSet<string>(actions, StringComparer.OrdinalIgnoreCase);
            foreach (string action in actions)
            {
                foreach (Range range in action.AsSpan().Split('_'))
                {
                    ReadOnlySpan<char> word = action.AsSpan(range);
                    if (word.Length >= 2 && word.ContainsAnyExceptInRange('0', '9'))
                        expandedActions.Add(word.ToString());
                }
            }

            return expandedActions
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();

            void Add(string value)
            {
                if (value.Length < 2) return;
                counts.TryGetValue(value, out int count);
                counts[value] = count == int.MaxValue ? count : count + 1;
            }
        }

        private static IReadOnlyList<string> FindKnownAddedPaths(
            IReadOnlyList<string> wadNames,
            IReadOnlyDictionary<string, IReadOnlyList<string>> skinMap,
            IReadOnlyList<string> addedWords,
            IReadOnlyDictionary<ulong, string> knownHashes)
        {
            var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string wadName in wadNames)
            {
                string[] aliases = wadName.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ||
                                   wadName.StartsWith("pet", StringComparison.OrdinalIgnoreCase)
                    ? new[] { wadName }
                    : new[] { wadName, $"jade_{wadName}" };

                foreach (string alias in aliases)
                foreach (string skin in GetChampionSkinNames(alias, skinMap))
                foreach (string word in addedWords)
                {
                    string candidate = $"assets/characters/{alias}/skins/{skin}/animations/{word}.anm";
                    if (knownHashes.TryGetValue(XxHash64Ext.Hash(candidate), out string knownPath) &&
                        candidate.Equals(knownPath, StringComparison.OrdinalIgnoreCase))
                        matches.Add(candidate);
                }
            }

            return matches.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildChampionSkinMap(IReadOnlyList<string> knownPaths)
        {
            var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in knownPaths)
            {
                bool assets = path.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase);
                if (!assets && !path.StartsWith("data/characters/", StringComparison.OrdinalIgnoreCase)) continue;
                int skins = path.IndexOf("/skins/", StringComparison.OrdinalIgnoreCase);
                if (skins <= 0) continue;
                int prefixLength = assets ? 18 : 16;
                string character = path.Substring(prefixLength, skins - prefixLength).ToLowerInvariant();
                string relative = path[(skins + 7)..];
                int slash = relative.IndexOf('/');
                string skin = slash > 0 ? relative[..slash] : relative;
                if (skin.Length is < 3 or > 35 || skin.Contains('.')) continue;
                if (!map.TryGetValue(character, out HashSet<string> values))
                    map[character] = values = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base" };
                values.Add(skin);
            }
            return map.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<string> GetChampionSkinNames(string character, IReadOnlyDictionary<string, IReadOnlyList<string>> map)
        {
            string baseCharacter = character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ? character[5..] : character;
            var skins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base" };
            if (map.TryGetValue(character, out IReadOnlyList<string> direct)) skins.UnionWith(direct);
            if (baseCharacter != character && map.TryGetValue(baseCharacter, out IReadOnlyList<string> inherited)) skins.UnionWith(inherited);
            int maximum = skins.Where(value => value.StartsWith("skin", StringComparison.OrdinalIgnoreCase))
                .Select(value => int.TryParse(value.AsSpan(4), out int number) ? number : 0)
                .DefaultIfEmpty()
                .Max();
            int limit = character.Equals("sightward", StringComparison.OrdinalIgnoreCase) ? 500 : Math.Max(maximum + 15, 85);
            for (int index = 0; index <= limit; index++)
            {
                skins.Add($"skin{index}");
                if (index <= 9) skins.Add($"skin{index:D2}");
            }
            for (int index = 300; index <= 350; index++) skins.Add($"skin{index}");
            return skins.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IReadOnlyList<string> FindEligibleWadNames(IEnumerable<string> wadPaths, IReadOnlyDictionary<ulong, string> knownHashes)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string wadPath in wadPaths)
            {
                string wadName = Path.GetFileNameWithoutExtension(wadPath);
                if (wadName.EndsWith(".wad", StringComparison.OrdinalIgnoreCase)) wadName = Path.GetFileNameWithoutExtension(wadName);
                if (wadName.Equals("global", StringComparison.OrdinalIgnoreCase) || wadName.StartsWith("map", StringComparison.OrdinalIgnoreCase)) continue;
                using var wad = new WadFile(wadPath);
                if (wad.Chunks.Keys.Any(hash => knownHashes.TryGetValue(hash, out string path) &&
                                                Path.GetExtension(path) is ".bin" or ".inibin"))
                    result.Add(wadName.ToLowerInvariant());
            }
            return result.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void PrintStage(string name, Stopwatch stopwatch, string detail)
        {
            stopwatch.Stop();
            Console.WriteLine($"{name,-28} {stopwatch.Elapsed:hh\\:mm\\:ss\\.fff} | {detail}");
        }

        private static string GetOption(string[] args, string option)
        {
            int index = Array.FindIndex(args, value => value.Equals(option, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        private static string FindHashes()
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes", "hashes.game.txt");
            if (File.Exists(local)) return local;
            return Directory.EnumerateDirectories(Path.GetTempPath(), "assetsmanager-game-baseline-*")
                .Select(path => Path.Combine(path, "hashes", "hashes.game.txt"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
    }
}
