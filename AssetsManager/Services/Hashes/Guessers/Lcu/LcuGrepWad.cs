using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using AssetsManager.Services.Hashes;

namespace AssetsManager.Services.Hashes.Guessers.Lcu
{
    internal sealed partial class LcuHashGuesser
    {
        internal override bool ShouldGrepExtension(string extension) =>
            extension is not ("png" or "jpg" or "jpeg" or "webp" or "gif" or "svg" or "ico" or
                "ttf" or "otf" or "woff" or "woff2" or "eot" or
                "ogg" or "mp3" or "wav" or "webm" or "mp4" or "dds" or "tga");


        internal override void GrepWad(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
            if (!ShouldGrepExtension(extension))
            {
                return;
            }

            if (data.Count == 0 || !TryDecodeWadText(data, out string text)) return;
            void CheckLcuCandidates(IEnumerable<HashGuessCandidate> candidates) =>
                CheckIter(engine, candidates, sourceWadPath, cancellationToken, sourceChunkHash: sourceChunkHash);

            if (Path.GetExtension(sourcePath).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                var structuredCandidates = new List<HashGuessCandidate>();
                bool stopAfterStructuredJson = ExtractStructuredJsonCandidates(data, sourcePath, structuredCandidates);
                CheckLcuCandidates(structuredCandidates);
                if (stopAfterStructuredJson) return;
            }

            CheckLcuCandidates(
                Regex.Matches(text, @"plugins/[0-9a-z_./@-]+", RegexOptions.IgnoreCase).Cast<Match>().Select(match =>
                    new HashGuessCandidate(NormalizePath(match.Value), HashGuessStrategy.LcuEmbeddedPath)));
            CheckLcuCandidates(
                Regex.Matches(text, @"\bfe/([^/]+)/([a-zA-Z0-9/_.@-]+)").Cast<Match>().Select(match =>
                    new HashGuessCandidate(
                        $"plugins/rcp-fe-{match.Groups[1].Value}/global/default/{match.Groups[2].Value}".ToLowerInvariant(),
                        HashGuessStrategy.LcuEmbeddedPath)));
            CheckLcuCandidates(
                Regex.Matches(text, @"/DATA/([a-zA-Z0-9/_.@-]+)").Cast<Match>().Select(match =>
                    new HashGuessCandidate(
                        $"plugins/rcp-be-lol-game-data/global/default/data/{match.Groups[1].Value}".ToLowerInvariant(),
                        HashGuessStrategy.LcuEmbeddedPath)));
            CheckLcuCandidates(
                Regex.Matches(text, @"\blol-game-data/assets/([a-zA-Z0-9/_.@-]+)").Cast<Match>().Select(match =>
                    new HashGuessCandidate(
                        $"plugins/rcp-be-lol-game-data/global/default/{match.Groups[1].Value}".ToLowerInvariant(),
                        HashGuessStrategy.LcuEmbeddedPath)));

            foreach (Match match in Regex.Matches(text, @"url\(\s*[""']?([^""')?#]+)", RegexOptions.IgnoreCase))
            {
                string contextualPath = ResolveRelativePath(sourcePath, match.Groups[1].Value);
                if (contextualPath.Length > 0)
                    CheckLcuCandidates(new[] { new HashGuessCandidate(contextualPath, HashGuessStrategy.LcuEmbeddedPath) });
            }
            foreach (Match match in Regex.Matches(text, @"(?:src|href|poster|data-src)\s*=\s*[""']([^""'?#]+)", RegexOptions.IgnoreCase))
            {
                string contextualPath = ResolveRelativePath(sourcePath, match.Groups[1].Value);
                if (contextualPath.Length > 0)
                    CheckLcuCandidates(new[] { new HashGuessCandidate(contextualPath, HashGuessStrategy.LcuEmbeddedPath) });
            }

            var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(text, @"[^a-zA-Z0-9/_.\\-]((?:\.|\.\.)/[a-zA-Z0-9/_.-]+)"))
            {
                string relativePath = match.Groups[1].Value;
                relativePaths.Add(relativePath);
                string contextualPath = ResolveRelativePath(sourcePath, relativePath);
                if (contextualPath.Length > 0)
                    CheckLcuCandidates(new[] { new HashGuessCandidate(contextualPath, HashGuessStrategy.LcuEmbeddedPath) });
            }
            foreach (Match match in Regex.Matches(text, @"[""']([a-zA-Z0-9][a-zA-Z0-9/_.@-]*\.(?:js|json|webm|html|[a-z]{3}))\b"))
                relativePaths.Add(match.Groups[1].Value);
            foreach (Match match in Regex.Matches(text, @"<template id=""[^""]*-template-([^""]+)"""))
                relativePaths.Add(match.Groups[1].Value + "/template.html");
            foreach (Match match in Regex.Matches(text, @"sourceMappingURL=(.*?\.js)\.map"))
                relativePaths.Add(match.Groups[1].Value);

            CheckBasenames(engine, relativePaths.Select(p => p.ToLowerInvariant()), cancellationToken, sourceWadPath);
        }


        private bool ExtractStructuredJsonCandidates(
            ArraySegment<byte> data,
            string sourcePath,
            ICollection<HashGuessCandidate> candidates)
        {
            try
            {
                using var document = JsonDocument.Parse(data.AsMemory());
                JsonElement root = document.RootElement;
                if (sourcePath.Equals("plugins/rcp-fe-lol-loot/global/default/trans.json", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (JsonProperty property in root.EnumerateObject())
                        candidates.Add(new HashGuessCandidate(
                            $"plugins/rcp-be-lol-game-data/global/default/v1/hextech-images/{property.Name}.png",
                            HashGuessStrategy.LcuPattern));
                    return true;
                }
                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("pluginDependencies", out _) && root.TryGetProperty("name", out _))
                {
                    AddPluginDescriptionCandidates(root, candidates);
                }
                else if (root.ValueKind == JsonValueKind.Object &&
                         root.TryGetProperty("musicVolume", out _) && root.TryGetProperty("files", out _))
                {
                    AddSplashCandidates(root, candidates);
                    return true;
                }
                else if (sourcePath.Equals("plugins/rcp-be-lol-game-data/global/default/v1/champion-summary.json", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (JsonElement element in root.EnumerateArray())
                    {
                        if (!element.TryGetProperty("id", out JsonElement idProperty)) continue;
                        string championId = idProperty.ToString();
                        candidates.Add(new HashGuessCandidate(
                            $"plugins/rcp-be-lol-game-data/global/default/v1/champions/{championId}.json",
                            HashGuessStrategy.LcuPattern));
                        candidates.Add(new HashGuessCandidate(
                            $"plugins/rcp-be-lol-game-data/global/default/v1/champion-splashes/{championId}/metadata.json",
                            HashGuessStrategy.LcuPattern));
                    }
                }
                else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("recommendedItemDefaults", out _))
                {
                    AddRecommendedItemCandidates(root, candidates);
                }
                return false;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogInvalidJson("LCU", sourcePath, exception);
                return false;
            }
        }


        private static void AddPluginDescriptionCandidates(JsonElement root, ICollection<HashGuessCandidate> candidates)
        {
            if (!root.TryGetProperty("name", out JsonElement nameProperty)) return;
            string name = nameProperty.GetString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(name)) return;
            foreach (string subpath in new[] { "index.html", "init.js", "init.js.map", "bundle.js", "trans.json", "css/main.css", "license.json" })
                candidates.Add(new HashGuessCandidate($"plugins/{name}/global/default/{subpath}", HashGuessStrategy.LcuPattern));
        }


        private static void AddSplashCandidates(JsonElement root, ICollection<HashGuessCandidate> candidates)
        {
            if (!root.TryGetProperty("files", out JsonElement filesProperty) || filesProperty.ValueKind != JsonValueKind.Object)
                return;

            var filePaths = filesProperty.EnumerateObject()
                .Select(property => property.Value.GetString()?.ToLowerInvariant() ?? string.Empty)
                .ToList();
            var splashNames = filePaths
                .SelectMany(path => Regex.Matches(path, @"-splash-([^.]+)").Select(match => match.Groups[1].Value.ToLowerInvariant()))
                .ToHashSet(StringComparer.Ordinal);

            foreach (string splashName in splashNames)
            {
                candidates.Add(new HashGuessCandidate(
                    $"plugins/rcp-fe-lol-splash/global/default/splash-assets/{splashName}/config.json",
                    HashGuessStrategy.LcuPattern));
                foreach (string filePath in filePaths)
                {
                    candidates.Add(new HashGuessCandidate(
                        $"plugins/rcp-fe-lol-splash/global/default/splash-assets/{splashName}/{filePath}",
                        HashGuessStrategy.LcuPattern));
                }
            }
        }


        private static void AddRecommendedItemCandidates(JsonElement root, ICollection<HashGuessCandidate> candidates)
        {
            if (!root.TryGetProperty("recommendedItemDefaults", out JsonElement property) || property.ValueKind != JsonValueKind.Array)
                return;
            foreach (JsonElement value in property.EnumerateArray())
            {
                string path = value.GetString()?.ToLowerInvariant();
                if (!string.IsNullOrEmpty(path))
                    candidates.Add(new HashGuessCandidate($"plugins/rcp-be-lol-game-data/global/default{path}", HashGuessStrategy.LcuPattern));
            }
        }


        private static string ResolveRelativePath(string sourcePath, string relativePath)
        {
            if (!sourcePath.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase)) return string.Empty;
            relativePath = relativePath.Trim();
            if (relativePath.Length == 0 || relativePath.StartsWith('/') || relativePath.StartsWith('#') ||
                relativePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return string.Empty;

            int separator = sourcePath.LastIndexOf('/');
            if (separator < 0) return string.Empty;
            var segments = new List<string>(sourcePath[..separator].Split('/', StringSplitOptions.RemoveEmptyEntries));
            foreach (string segment in PathUtils.NormalizeSeparators(relativePath).Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".") continue;
                if (segment == "..")
                {
                    if (segments.Count <= 1) return string.Empty;
                    segments.RemoveAt(segments.Count - 1);
                }
                else
                {
                    segments.Add(segment);
                }
            }
            return NormalizePath(string.Join('/', segments));
        }

    }
}
