using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Parsers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes.Guessers.Game
{
    internal sealed partial class GameHashGuesser : HashGuesser
    {
        private static readonly string[] ShaderExtensions = { ".ps_2_0", ".ps_3_0", ".vs_2_0", ".vs_3_0", ".ps", ".vs", ".cs" };
        private static readonly string[] ShaderVariants = { ".dx11", ".dx9", ".dx9sm3", ".glsl", ".metal", "-dx11", "-metal" };
        private readonly ConditionalWeakTable<HashGuessEngine, ConcurrentDictionary<string, byte>> _scannedWadCharacters = new();

        private readonly LogService _logService;
        private readonly Func<uint, string> _resolveBinHash;

        internal GameHashGuesser(HashFile hashFile, LogService logService = null, Func<uint, string> resolveBinHash = null)
            : base(hashFile, "*.wad.client")
        {
            if (hashFile.Domain != HashGuessDomain.Game) throw new ArgumentException("GAME guesser requires a GAME hash file.", nameof(hashFile));
            _logService = logService;
            _resolveBinHash = resolveBinHash;
        }

        internal GameHashGuesser() : this(new HashFile(HashGuessDomain.Game, Array.Empty<string>())) { }

        internal override IReadOnlyList<string> BuildWordlist() =>
            Corpus.GetOrCreate("wordlist", HashGuessEngine.BuildWordlist);

        internal IReadOnlyList<string> BuildSwordlist() =>
            Corpus.GetOrCreate(
                "swordlist",
                values => HashGuessEngine.BuildWordlist(
                    values
                        .Where(path => path.Contains(".bin", StringComparison.Ordinal))
                        .Select(GetBasename)));

        internal IEnumerable<HashGuessCandidate> GuessFromBinEntryBasenames(IEnumerable<string> binEntryPaths)
        {
            var basenames = binEntryPaths.Select(Path.GetFileName)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.ToLowerInvariant())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            var extensions = KnownPaths.Select(Path.GetExtension)
                .Where(extension => extension.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(extension => !Regex.IsMatch(extension, @"(?:glsl|dx9|dx9sm3|dx11|metal)_", RegexOptions.IgnoreCase))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();

            foreach (string extension in extensions)
            foreach (string basename in basenames)
                yield return new HashGuessCandidate(basename + extension, HashGuessStrategy.CrossDomainGame);
        }

        private const int MaxGlobalAnimationWords = 100_000;

        private static readonly string[] BaseAnimationActions =
        {
            "idle", "idle1", "idle2", "run", "run_fast", "walk",
            "attack1", "attack2", "attack3", "crit",
            "spell1", "spell2", "spell3", "spell4",
            "death", "recall", "dance", "taunt", "laugh", "joke",
            "channel", "spawn", "signature_move"
        };

        private IReadOnlyList<string> GetGlobalAnimationActions(CancellationToken cancellationToken = default)
        {
            return Corpus.GetOrCreate("global-animation-actions", knownPaths =>
            {
                var actionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var animRegex = new Regex(
                    @"^(?:assets|data)/characters/(?<char>[^/]+)/(?:(?:skins/(?<skin>[^/]+)|themes/(?<theme>[^/]+))/animations/|animations/)(?<file>[^/]+)\.anm$",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled);

                for (int pathIndex = 0; pathIndex < knownPaths.Count; pathIndex++)
                {
                    if ((pathIndex & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    string path = knownPaths[pathIndex];
                    if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ||
                        !path.Contains("/characters/", StringComparison.OrdinalIgnoreCase) ||
                        !path.Contains("/animations/", StringComparison.OrdinalIgnoreCase)) continue;

                    string basename = GetBasename(path);
                    if (basename.Length <= 4) continue;
                    string stem = basename[..^4].ToLowerInvariant();

                    int dotIdx = stem.IndexOf('.');
                    if (dotIdx > 0) stem = stem[..dotIdx];

                    if (stem.Length < 2 || stem.Length > 100) continue;
                    if (stem.All(char.IsDigit) || (stem.Length == 16 && stem.All(c => char.IsAsciiHexDigitLower(c)))) continue;

                    bool isValid = true;
                    for (int i = 0; i < stem.Length; i++)
                    {
                        char c = stem[i];
                        if (!char.IsAsciiLetterOrDigit(c) && c != '_') { isValid = false; break; }
                    }
                    if (!isValid) continue;

                    AddCount(stem);

                    Match m = animRegex.Match(path);
                    if (m.Success)
                    {
                        string charName = m.Groups["char"].Value.ToLowerInvariant();
                        string container = (m.Groups["skin"].Success ? m.Groups["skin"].Value : (m.Groups["theme"].Success ? m.Groups["theme"].Value : null))?.ToLowerInvariant();

                        if (!string.IsNullOrEmpty(container) && stem.StartsWith($"{charName}_{container}_", StringComparison.OrdinalIgnoreCase))
                        {
                            string action = stem[(charName.Length + container.Length + 2)..];
                            if (action.Length >= 2) AddCount(action);
                        }
                        else if (stem.StartsWith($"{charName}_", StringComparison.OrdinalIgnoreCase))
                        {
                            string action = stem[(charName.Length + 1)..];
                            if (action.Length >= 2) AddCount(action);
                        }
                        else if (!string.IsNullOrEmpty(container) && stem.StartsWith($"{container}_", StringComparison.OrdinalIgnoreCase))
                        {
                            string action = stem[(container.Length + 1)..];
                            if (action.Length >= 2) AddCount(action);
                        }
                    }

                    int sep = stem.IndexOf('_');
                    while (sep >= 0 && sep < stem.Length - 1)
                    {
                        string sub = stem[(sep + 1)..];
                        if (sub.Length >= 2 && sub.Length <= 100 && !sub.All(char.IsDigit))
                            AddCount(sub);
                        sep = stem.IndexOf('_', sep + 1);
                    }
                }

                foreach (string baseAction in BaseAnimationActions)
                {
                    if (!actionCounts.ContainsKey(baseAction))
                        actionCounts[baseAction] = 1;
                }

                return actionCounts
                    .OrderByDescending(kv => kv.Value)
                    .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                    .Take(MaxGlobalAnimationWords)
                    .Select(kv => kv.Key)
                    .ToList();

                void AddCount(string action)
                {
                    if (actionCounts.TryGetValue(action, out int count))
                    {
                        if (count < int.MaxValue) actionCounts[action] = count + 1;
                    }
                    else
                    {
                        actionCounts[action] = 1;
                    }
                }
            });
        }

        private IReadOnlyList<string> GetExpandedGlobalAnimationActions(CancellationToken cancellationToken = default)
        {
            return Corpus.GetOrCreate("expanded-global-animation-actions", _ =>
            {
                IReadOnlyList<string> actions = GetGlobalAnimationActions(cancellationToken);
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

                return expandedActions.OrderBy(action => action, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private IReadOnlyList<byte[]> GetExpandedGlobalAnimationActionBytes(CancellationToken cancellationToken = default)
        {
            return Corpus.GetOrCreate(
                "expanded-global-animation-action-bytes",
                _ => GetExpandedGlobalAnimationActions(cancellationToken).Select(Encoding.UTF8.GetBytes).ToList());
        }

        private IReadOnlyList<string> GetCharacterAnimationActions(string character)
        {
            return Corpus.GetOrCreate($"champion-animation-actions/{character.ToLowerInvariant()}", knownPaths =>
            {
                var actions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                string baseChar = character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ? character[5..]
                    : (character.StartsWith("tft_", StringComparison.OrdinalIgnoreCase) || (character.StartsWith("tft", StringComparison.OrdinalIgnoreCase) && character.Length > 5 && character.Contains('_'))) ? character[(character.IndexOf('_') + 1)..]
                    : (character.StartsWith("cherry_", StringComparison.OrdinalIgnoreCase) || character.StartsWith("strawberry_", StringComparison.OrdinalIgnoreCase) || character.StartsWith("crepe_", StringComparison.OrdinalIgnoreCase) || character.StartsWith("ruby_", StringComparison.OrdinalIgnoreCase)) ? character[(character.IndexOf('_') + 1)..]
                    : character.Equals("oriannaball", StringComparison.OrdinalIgnoreCase) ? "orianna"
                    : character.Equals("tibbers", StringComparison.OrdinalIgnoreCase) ? "annie"
                    : character.Equals("heimergarrison", StringComparison.OrdinalIgnoreCase) ? "heimerdinger"
                    : character.Equals("quinnvalor", StringComparison.OrdinalIgnoreCase) ? "quinn"
                    : character.Equals("yorickghoul", StringComparison.OrdinalIgnoreCase) ? "yorick"
                    : character.Equals("kalistaspawn", StringComparison.OrdinalIgnoreCase) ? "kalista"
                    : character.Equals("malzaharvoidling", StringComparison.OrdinalIgnoreCase) ? "malzahar"
                    : null;

                string charPrefix = $"assets/characters/{character}/";
                string dataCharPrefix = $"data/characters/{character}/";
                string basePrefix = baseChar != null ? $"assets/characters/{baseChar}/" : null;
                string dataBasePrefix = baseChar != null ? $"data/characters/{baseChar}/" : null;

                for (int i = 0; i < knownPaths.Count; i++)
                {
                    string path = knownPaths[i];
                    if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase)) continue;

                    string rel = null;
                    if (path.StartsWith(charPrefix, StringComparison.OrdinalIgnoreCase))
                        rel = path[charPrefix.Length..];
                    else if (path.StartsWith(dataCharPrefix, StringComparison.OrdinalIgnoreCase))
                        rel = path[dataCharPrefix.Length..];
                    else if (basePrefix != null && path.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
                        rel = path[basePrefix.Length..];
                    else if (dataBasePrefix != null && path.StartsWith(dataBasePrefix, StringComparison.OrdinalIgnoreCase))
                        rel = path[dataBasePrefix.Length..];

                    if (string.IsNullOrEmpty(rel)) continue;

                    string basename = GetBasename(path);
                    string stem = basename.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) ? basename[..^4] : basename;
                    if (stem.Length == 0 || stem.Length > 50) continue;

                    // Versioned variants (attack1.pie_c_11_15) only bloat the list and starve
                    // later skins under the fallback budget; the clean action covers them and
                    // structural generators rebuild versioned names, mirroring the global list.
                    int dotIndex = stem.IndexOf('.');
                    if (dotIndex > 0) stem = stem[..dotIndex];
                    if (stem.Length == 0) continue;

                    actions.Add(stem);

                    if (stem.StartsWith(character + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        string sub = stem[(character.Length + 1)..];
                        if (sub.Length > 0) actions.Add(sub);
                    }
                    if (baseChar != null && stem.StartsWith(baseChar + "_", StringComparison.OrdinalIgnoreCase))
                    {
                        string sub = stem[(baseChar.Length + 1)..];
                        if (sub.Length > 0) actions.Add(sub);
                    }

                    Match containerMatch = Regex.Match(
                        rel,
                        @"^(?:skins|themes)/(?<container>[^/]+)/animations/",
                        RegexOptions.IgnoreCase);
                    if (containerMatch.Success)
                    {
                        string container = containerMatch.Groups["container"].Value;
                        AddAnimationActionAfterPrefix($"{character}_{container}_");
                        if (baseChar != null) AddAnimationActionAfterPrefix($"{baseChar}_{container}_");
                    }

                    Match skinToken = Regex.Match(stem, @"(?:^|_)skin\d+_(?<action>.+)$", RegexOptions.IgnoreCase);
                    if (skinToken.Success) actions.Add(skinToken.Groups["action"].Value);

                    void AddAnimationActionAfterPrefix(string prefix)
                    {
                        if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
                        string action = stem[prefix.Length..];
                        if (action.Length > 0) actions.Add(action);
                    }
                }

                if (actions.Count == 0)
                {
                    foreach (string globalAction in GetGlobalAnimationActions())
                        actions.Add(globalAction);
                }

                return actions.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private static bool IsShaderReference(StringBuilder value)
        {
            foreach (string extension in ShaderExtensions)
                if (EndsWith(value, extension)) return true;
            return false;
        }

        private static bool EndsWith(StringBuilder value, string suffix)
        {
            if (value.Length < suffix.Length) return false;
            int offset = value.Length - suffix.Length;
            for (int index = 0; index < suffix.Length; index++)
                if (char.ToLowerInvariant(value[offset + index]) != suffix[index]) return false;
            return true;
        }

    }
}
