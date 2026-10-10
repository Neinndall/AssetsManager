using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes;

// Bounded, read-only experiments. Each experiment checks the same pending inventory.
internal static class GameContextPatternsDiagnostic
{
    internal static void Run(string[] args)
    {
        string Option(string key) => args.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
        if (args.Contains("--help"))
        {
            Console.WriteLine("game-context-patterns [--state=directory] [--context=bin-context.txt] [--map-context=map12-bin-context.txt] [--seconds=25] [--budget=50000000] [--experiment=shaders]");
            Console.WriteLine("Experiments: augment-icons, icon-relocation, map-themes, shaders, image-extensions, cross-catalog, animations. Results go to stdout; nothing is persisted.");
            return;
        }
        string state = Option("--state") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager");
        double seconds = Option("--seconds") is string time ? double.Parse(time, CultureInfo.InvariantCulture) : 25;
        long budget = Option("--budget") is string limit ? long.Parse(limit, CultureInfo.InvariantCulture) : 50_000_000;
        if (!double.IsFinite(seconds) || seconds <= 0 || budget <= 0)
            throw new ArgumentException("Time and candidate budgets must be positive and finite.");
        string selection = Option("--experiment");
        string[] experiments = { "augment-icons", "icon-relocation", "map-themes", "shaders", "image-extensions", "cross-catalog", "animations" };
        if (selection != null && !experiments.Contains(selection, StringComparer.Ordinal))
            throw new ArgumentException("Unknown experiment. Use --help for available experiments.");

        var pending = File.ReadLines(Path.Combine(state, "hash_lab", "unknowns.game.txt"))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => ulong.Parse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToHashSet();
        string[] paths = Catalog(Path.Combine(state, "hashes", "hashes.game.txt")).ToArray();
        string context = ReadContext(Option("--context"));
        string mapContext = ReadContext(Option("--map-context"));
        var located = Regex.Matches(context, @"^([0-9a-fA-F]{16})\s", RegexOptions.Multiline)
            .Select(match => ulong.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToHashSet();
        Console.WriteLine($"Read-only context patterns: {pending.Count:N0} pending hashes; {paths.Length:N0} known paths; {seconds}s and {budget:N0} candidates per experiment.");

        foreach (string experiment in experiments)
        {
            if (selection != null && selection != experiment) continue;
            if ((experiment == "augment-icons" && mapContext.Length == 0) || (experiment == "animations" && context.Length == 0))
            {
                Console.WriteLine($"SKIP {experiment}: provide {(experiment == "animations" ? "--context" : "--map-context")} to use BIN evidence.");
                continue;
            }
            IEnumerable<string> candidates = experiment switch
            {
                "augment-icons" => AugmentIcons(),
                "icon-relocation" => RelocateIcons(),
                "map-themes" => MapThemes(),
                "shaders" => Shaders(),
                "image-extensions" => ImageExtensions(),
                "cross-catalog" => CrossCatalog(),
                _ => Animations()
            };
            var timer = Stopwatch.StartNew();
            var hits = new Dictionary<ulong, string>();
            long checks = 0;
            bool limited = false;
            foreach (string candidate in candidates)
            {
                if (checks >= budget || timer.Elapsed.TotalSeconds >= seconds)
                {
                    limited = true;
                    break;
                }
                checks++;
                ulong hash = XxHash64Ext.Hash(candidate);
                if (pending.Contains(hash)) hits[hash] = candidate;
            }
            Console.WriteLine($"{experiment}: {checks:N0} checks; {timer.Elapsed.TotalSeconds:F3}s; limited={limited}; {hits.Count} matches");
            foreach (var (hash, path) in hits.OrderBy(pair => pair.Key))
                Console.WriteLine($"MATCH {hash:x16} {path} located_in_context={(context.Length == 0 ? "unknown" : located.Contains(hash).ToString())}");
        }

        IEnumerable<string> AugmentIcons()
        {
            var names = Regex.Matches(mapContext, "AugmentNameId=\"([^\"]+)\"").Select(m => m.Groups[1].Value)
                .Concat(Regex.Matches(mapContext, @"HeartsteelAugmentModifier_([A-Za-z]+)_").Select(m => m.Groups[1].Value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            var directories = paths.Where(p => p.Contains("/augments/", StringComparison.Ordinal) && Texture(p)).Select(DirectoryOf).Distinct().Order(StringComparer.Ordinal).ToArray();
            foreach (string name in names)
            {
                string lower = name.ToLowerInvariant();
                string snake = Regex.Replace(name, @"(?<=[a-z0-9])(?=[A-Z])", "_").ToLowerInvariant();
                foreach (string stem in new[] { lower, snake, "aram_" + lower, "kiwi_" + lower, "heartsteel_" + lower }.Distinct())
                foreach (string directory in directories)
                foreach (string suffix in new[] { "", "_large", "_small", "_icon", "_large_icon", "_small_icon", "_gold", "_silver", "_prismatic" })
                foreach (string extension in new[] { ".tex", ".dds" })
                    yield return directory + stem + suffix + extension;
            }
        }

        IEnumerable<string> RelocateIcons()
        {
            var names = paths.Where(p => p.Contains("/augments/", StringComparison.Ordinal) && Texture(p)).Select(NameOf).Distinct().Order(StringComparer.Ordinal).ToArray();
            var directories = paths.Where(p => p.Contains("/kiwi/", StringComparison.Ordinal) && Texture(p)).Select(DirectoryOf).Distinct().Order(StringComparer.Ordinal);
            foreach (string directory in directories)
            foreach (string name in names) yield return directory + name;
        }

        IEnumerable<string> MapThemes()
        {
            string[] themes = { "base", "bloom", "crepe", "bilgewater", "butchersbridge", "kiwi", "howling_abyss", "ha_base", "ha_bloom", "ha_crepe", "ha_bilgewater" };
            foreach (string path in paths)
            {
                if (!(Texture(path) || path.EndsWith(".jpg", StringComparison.Ordinal)) ||
                    !new[] { "/howling_abyss/", "/crepe/", "/bloom/", "/bilgewater/" }.Any(path.Contains)) continue;
                foreach (string old in themes.Where(path.Contains))
                foreach (string replacement in themes)
                {
                    yield return path.Replace("/" + old + "/", "/" + replacement + "/", StringComparison.Ordinal);
                    yield return path.Replace(old, replacement, StringComparison.Ordinal);
                }
            }
        }

        IEnumerable<string> Shaders()
        {
            var shaders = paths.Where(p => p.Contains("/shaders/", StringComparison.Ordinal)).ToArray();
            var stems = shaders.Select(p => NameOf(p).Split('.')[0]).Distinct().Order(StringComparer.Ordinal).ToArray();
            foreach (string directory in shaders.Select(DirectoryOf).Distinct().Order(StringComparer.Ordinal))
            foreach (string stem in stems)
            foreach (string stage in new[] { "ps", "vs", "cs" })
            foreach (string platform in new[] { ".dx11", "-dx11" })
            {
                string candidate = directory + stem + "." + stage + platform;
                yield return candidate;
                yield return candidate + "_0";
            }
        }

        IEnumerable<string> ImageExtensions()
        {
            foreach (string path in paths.Where(Image))
            foreach (string extension in new[] { ".tex", ".dds", ".jpg", ".png" })
                yield return path[..^4] + extension;
        }

        IEnumerable<string> CrossCatalog()
        {
            foreach (string file in Directory.EnumerateFiles(Path.Combine(state, "hashes"), "hashes.*.txt").Order(StringComparer.Ordinal))
            {
                if (Path.GetFileName(file) == "hashes.game.txt") continue;
                foreach (string original in Catalog(file))
                {
                    int start = original.IndexOf("assets/", StringComparison.Ordinal);
                    if (start < 0) continue;
                    string path = original[start..];
                    yield return path;
                    if (!Image(path)) continue;
                    yield return path[..^4] + ".tex";
                    yield return path[..^4] + ".dds";
                }
            }
        }

        IEnumerable<string> Animations()
        {
            var active = Regex.Matches(context, @"AnimationResourceData\.mAnimationFilePath.*?\(data/characters/([^/]+)/animations/([^/.]+)\.bin")
                .Select(m => (Character: m.Groups[1].Value.ToLowerInvariant(), Container: m.Groups[2].Value.ToLowerInvariant()))
                .ToHashSet();
            var folders = active.GroupBy(a => a.Character).ToDictionary(g => g.Key,
                g => g.SelectMany(a => a.Container == "base" ? new[] { "base", "skin0" } : new[] { a.Container }).ToHashSet());
            var tails = new HashSet<string>(StringComparer.Ordinal);
            var prefixes = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths.Where(p => p.EndsWith(".anm", StringComparison.Ordinal)))
            {
                string directory = DirectoryOf(path);
                string[] parts = NameOf(path)[..^4].Split('_');
                for (int count = 1; count < Math.Min(5, parts.Length); count++)
                    tails.Add(string.Join('_', parts[^count..]) + ".anm");
                Match character = Regex.Match(path, @"^(?:assets|data)/characters/([^/]+)/");
                if (!character.Success || !folders.TryGetValue(character.Groups[1].Value, out var containers) ||
                    !containers.Any(folder => directory.Contains("/" + folder + "/", StringComparison.Ordinal))) continue;
                for (int count = 1; count < Math.Min(4, parts.Length); count++)
                    prefixes.Add(directory + string.Join('_', parts[..count]) + "_");
            }
            Console.WriteLine($"Animation index: {active.Count} BIN containers; {prefixes.Count:N0} prefixes; {tails.Count:N0} suffixes");
            var ordered = prefixes.Order(StringComparer.Ordinal).ToArray();
            foreach (string tail in tails.Order(StringComparer.Ordinal))
            foreach (string prefix in ordered) yield return prefix + tail;
        }
    }

    private static string ReadContext(string path) => path == null ? "" : File.ReadAllText(path);
    private static bool Texture(string path) => path.EndsWith(".tex", StringComparison.Ordinal) || path.EndsWith(".dds", StringComparison.Ordinal);
    private static bool Image(string path) => Texture(path) || path.EndsWith(".jpg", StringComparison.Ordinal) || path.EndsWith(".png", StringComparison.Ordinal);
    private static string DirectoryOf(string path) => path[..(path.LastIndexOf('/') + 1)];
    private static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];
    private static IEnumerable<string> Catalog(string file)
    {
        foreach (string line in File.ReadLines(file))
        {
            int separator = line.IndexOf(' ');
            if (separator >= 0) yield return line[(separator + 1)..].Trim().Replace('\\', '/').ToLowerInvariant();
        }
    }
}
