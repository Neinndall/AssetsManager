using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes;

internal static class GameAnimationLabelProbeDiagnostic
{
    private sealed record Link(ulong Target, string Character, string Container, string Bin);

    internal static void Run(string[] args)
    {
        string Option(string key) => args.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
        string state = Option("--state") ?? throw new ArgumentException("Provide --state.");
        string output = Option("--out") ?? throw new ArgumentException("Provide --out.");
        string references = Option("--references") ?? throw new ArgumentException("Provide --references.");
        int seconds = int.Parse(Option("--seconds") ?? "60", CultureInfo.InvariantCulture);
        var pending = File.ReadLines(Path.Combine(state, "hash_lab", "unknowns.game.txt"))
            .Select(s => ulong.Parse(s, NumberStyles.HexNumber)).ToHashSet();
        var known = new HashFile(HashGuessDomain.Game, Path.Combine(state, "hashes", "hashes.game.txt")).LoadPaths();
        using var document = JsonDocument.Parse(File.ReadAllText(references));
        var byName = new Dictionary<uint, HashSet<Link>>();
        foreach (var reference in document.RootElement.GetProperty("References").EnumerateArray())
        {
            ulong target = ulong.Parse(reference.GetProperty("Hash").GetString(), NumberStyles.HexNumber);
            if (!pending.Contains(target)) continue;
            string source = reference.GetProperty("Bin").GetString();
            string[] parts = source.Split('/');
            if (parts.Length != 5 || parts[3] != "animations") continue;
            foreach (var frame in reference.GetProperty("Context").EnumerateArray())
            {
                string position = frame.GetProperty("Position").GetString();
                if (!position.StartsWith("mClipDataMap[", StringComparison.Ordinal)) continue;
                string label = position[13..^1];
                uint name = label.StartsWith('{') ? uint.Parse(label.AsSpan(1, label.Length - 2), NumberStyles.HexNumber) : Fnv1a.HashLower(label);
                if (!byName.TryGetValue(name, out var links)) byName[name] = links = new();
                links.Add(new(target, parts[2], parts[4][..^4], source));
            }
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        var containers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string path in known)
        {
            if (!path.EndsWith(".anm", StringComparison.Ordinal) || !path.Contains("/animations/", StringComparison.Ordinal)) continue;
            string[] parts = path.Split('/');
            if (parts.Length < 7 || parts[1] != "characters") continue;
            if (!containers.TryGetValue(parts[2], out var set)) containers[parts[2]] = set = new();
            set.Add(parts[4]);
            string stem = parts[^1][..^4].Split('.')[0];
            names.Add(stem);
            if (stem.StartsWith(parts[2] + "_", StringComparison.Ordinal)) stem = stem[(parts[2].Length + 1)..];
            stem = Regex.Replace(stem, "^(?:skin[0-9]+|base)_", "");
            names.Add(stem);
        }
        var engine = new HashGuessEngine(HashGuessDomain.Game, pending);
        var labelHits = new Dictionary<uint, HashSet<string>>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var watch = Stopwatch.StartNew();
        long labels = 0;
        try
        {
            foreach (string name in names.Order(StringComparer.Ordinal))
            {
                CheckLabel(name);
                foreach (Match match in Regex.Matches(name, "[0-9]+"))
                foreach (int width in new[] { 0, match.Length, 2, 3 }.Distinct())
                for (int value = 0; value < 100; value++)
                    CheckLabel(name[..match.Index] + value.ToString(width == 0 ? "D" : "D" + width, CultureInfo.InvariantCulture) + name[(match.Index + match.Length)..]);
                foreach (string tail in new[] { "_in", "_out", "_loop", "_start", "_end", "_add" }) CheckLabel(name + tail);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        File.WriteAllLines(output, engine.Matches.Values.OrderBy(m => m.Path).Select(m => $"{m.Hash:x16} {m.Path}"));
        File.WriteAllText(output + ".labels.json", JsonSerializer.Serialize(labelHits.ToDictionary(p => p.Key.ToString("x8"), p => p.Value.ToArray())));
        Console.WriteLine($"RESULT {labels} label candidates; {labelHits.Count}/{byName.Count} label hashes recognized; {engine.CheckedCandidates} path checks; {engine.Matches.Count} matches; {watch.Elapsed.TotalSeconds:F2}s; no persistence.");

        void CheckLabel(string name)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            labels++;
            uint hash = Fnv1a.HashLower(name);
            if (!byName.TryGetValue(hash, out var links)) return;
            if (!labelHits.TryGetValue(hash, out var tried)) labelHits[hash] = tried = new();
            if (!tried.Add(name)) return;
            foreach (var link in links)
            {
                var targets = containers.GetValueOrDefault(link.Character, new()).Append(link.Container).Append("base").Distinct();
                foreach (string skin in targets)
                foreach (bool theme in new[] { false, true })
                foreach (string candidate in GameHashGuesser.EnumerateAnimationNameVariants(link.Character, skin, name, includeThemeLayout: theme))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (engine.CheckNormalizedParts(candidate, "", "", HashGuessStrategy.AnimationBinLink, link.Bin, expectedHash: link.Target))
                        Console.WriteLine($"MATCH {link.Target:x16} {candidate} label={name}");
                }
            }
        }
    }
}
