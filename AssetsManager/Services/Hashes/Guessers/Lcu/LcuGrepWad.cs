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

            CheckLcuCandidates(ExtractCssSpriteSourceCandidates(text, sourcePath));
            CheckLcuCandidates(ExtractDynamicCardFrameCandidates(text));

            var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void CheckRelativeReference(string value)
            {
                string relativePath = PathUtils.NormalizeSeparators(value.Trim());
                if (relativePath.Length == 0 || relativePath.StartsWith('#') || relativePath.StartsWith("//", StringComparison.Ordinal) ||
                    relativePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                    relativePath.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return;

                if (relativePath.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase))
                    CheckLcuCandidates(new[] { new HashGuessCandidate(NormalizePath(relativePath), HashGuessStrategy.LcuEmbeddedPath) });

                string contextualPath = ResolveRelativePath(sourcePath, relativePath);
                if (contextualPath.Length > 0)
                    CheckLcuCandidates(new[] { new HashGuessCandidate(contextualPath, HashGuessStrategy.LcuEmbeddedPath) });

                if (!relativePath.StartsWith('/'))
                    relativePaths.Add(relativePath.ToLowerInvariant());
            }

            foreach (Match match in Regex.Matches(text, @"url\(\s*[""']?([^""')?#]+)", RegexOptions.IgnoreCase))
                CheckRelativeReference(match.Groups[1].Value);
            foreach (Match match in Regex.Matches(text, @"(?:src|href|poster|data-src)\s*=\s*[""']([^""'?#]+)", RegexOptions.IgnoreCase))
                CheckRelativeReference(match.Groups[1].Value);
            foreach (Match match in Regex.Matches(text, @"[^a-zA-Z0-9/_.\\-]((?:\.|\.\.)/[a-zA-Z0-9/_.@-]+)"))
                CheckRelativeReference(match.Groups[1].Value);
            foreach (Match match in Regex.Matches(
                         text,
                         @"[""']([^""'?#\r\n]+?\.(?:js|json|css|html?|map|png|jpe?g|svg|webp|gif|ico|webm|mp4|ogg|mp3|wav|woff2?|ttf|otf|eot))(?=[""'?#\s])",
                         RegexOptions.IgnoreCase))
                CheckRelativeReference(match.Groups[1].Value);
            foreach (Match match in Regex.Matches(text, @"<template id=""[^""]*-template-([^""]+)"""))
                CheckRelativeReference(match.Groups[1].Value + "/template.html");
            foreach (Match match in Regex.Matches(text, @"sourceMappingURL=(.*?\.js)\.map"))
                CheckRelativeReference(match.Groups[1].Value);

            CheckBasenames(
                engine,
                relativePaths.Select(path => path.ToLowerInvariant()),
                cancellationToken,
                sourceWadPath,
                HashGuessStrategy.LcuRelativeBasename,
                sourceChunkHash);
        }


        private IEnumerable<HashGuessCandidate> ExtractDynamicCardFrameCandidates(string text)
        {
            const string staticAssetsRoot = "plugins/rcp-fe-lol-static-assets/global/default";
            const string sanctumTemplate = "/fe/lol-static-assets/videos/sanctum/card-frame-tier${";
            const string exaltedTemplate = "/fe/lol-static-assets/videos/exalted/card-frame-${";

            bool hasSanctumTemplate = text.IndexOf(sanctumTemplate, StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasExaltedTemplate = text.IndexOf(exaltedTemplate, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!hasSanctumTemplate && !hasExaltedTemplate)
                yield break;

            string[] states = Regex.Matches(
                    text,
                    @"getCardVideoAssetByTier\([^,]+,\s*[""']([a-zA-Z0-9_-]+)[""']\)",
                    RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(match => match.Groups[1].Value.ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (states.Length == 0)
                yield break;

            if (hasSanctumTemplate)
            {
                const string imagePrefix = staticAssetsRoot + "/images/sanctum/card-frame-tier";
                foreach (string tier in KnownPaths
                             .Where(path => path.StartsWith(imagePrefix, StringComparison.OrdinalIgnoreCase) &&
                                 path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                             .Select(path => path[imagePrefix.Length..^4])
                             .Where(tier => tier.Length > 0 && tier.All(char.IsDigit))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (string state in states)
                    yield return new HashGuessCandidate(
                        $"{staticAssetsRoot}/videos/sanctum/card-frame-tier{tier}-{state}.webm",
                        HashGuessStrategy.LcuPattern);
            }

            if (hasExaltedTemplate)
            {
                const string imagePrefix = staticAssetsRoot + "/images/exalted/card-frame-";
                foreach (string tier in KnownPaths
                             .Where(path => path.StartsWith(imagePrefix, StringComparison.OrdinalIgnoreCase) &&
                                 path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                             .Select(path => path[imagePrefix.Length..^4])
                             .Where(tier => tier.Length > 0 && tier.All(char.IsLetterOrDigit))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (string state in states)
                    yield return new HashGuessCandidate(
                        $"{staticAssetsRoot}/videos/exalted/card-frame-{tier}-{state}.webm",
                        HashGuessStrategy.LcuPattern);
            }
        }


        private IEnumerable<HashGuessCandidate> ExtractCssSpriteSourceCandidates(string text, string sourcePath)
        {
            if (text.IndexOf("background-position", StringComparison.OrdinalIgnoreCase) < 0)
                yield break;

            string normalizedSourcePath = NormalizePath(sourcePath);
            if (!normalizedSourcePath.StartsWith("plugins/", StringComparison.OrdinalIgnoreCase))
                yield break;

            int pluginEnd = normalizedSourcePath.IndexOf('/', "plugins/".Length);
            if (pluginEnd < 0)
                yield break;

            string pluginPrefix = normalizedSourcePath[..(pluginEnd + 1)];
            string[] spriteDirectories = GetKnownDirectories()
                .Where(directory => directory.StartsWith(pluginPrefix, StringComparison.OrdinalIgnoreCase))
                .Where(directory => directory.EndsWith("/sprite-source", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (spriteDirectories.Length == 0)
                yield break;

            var classNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const string backgroundPosition = "background-position";
            int searchIndex = 0;
            while (searchIndex < text.Length)
            {
                int propertyIndex = text.IndexOf(backgroundPosition, searchIndex, StringComparison.OrdinalIgnoreCase);
                if (propertyIndex < 0) break;
                searchIndex = propertyIndex + backgroundPosition.Length;

                int openBrace = text.LastIndexOf('{', propertyIndex);
                if (openBrace < 0) continue;
                int closeBrace = text.IndexOf('}', propertyIndex);
                if (closeBrace < 0) continue;

                int previousCloseBrace = text.LastIndexOf('}', openBrace);
                int selectorStart = previousCloseBrace < 0 ? 0 : previousCloseBrace + 1;
                int selectorLength = openBrace - selectorStart;
                if (selectorLength <= 0 || selectorLength > 2048) continue;

                string selectors = text.Substring(selectorStart, selectorLength);
                foreach (Match selector in Regex.Matches(selectors, @"\.([a-zA-Z_][a-zA-Z0-9_-]*)"))
                    classNames.Add(selector.Groups[1].Value.ToLowerInvariant());
            }

            foreach (string directory in spriteDirectories)
            foreach (string className in classNames)
                yield return new HashGuessCandidate(
                    $"{directory}/{className}.png",
                    HashGuessStrategy.LcuPattern);
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
            string normalizedSource = NormalizePath(sourcePath);
            if (!normalizedSource.StartsWith("plugins/", StringComparison.Ordinal)) return string.Empty;
            relativePath = PathUtils.NormalizeSeparators(relativePath.Trim());
            if (relativePath.Length == 0 || relativePath.StartsWith('#') || relativePath.StartsWith("//", StringComparison.Ordinal) ||
                relativePath.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith("https:", StringComparison.OrdinalIgnoreCase)) return string.Empty;

            string[] sourceSegments = normalizedSource.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int pluginRootLength = GetPluginRootLength(sourceSegments);
            if (pluginRootLength == 0) return string.Empty;

            if (relativePath.StartsWith('/'))
            {
                string pluginRoot = string.Join('/', sourceSegments.Take(pluginRootLength));
                return NormalizePath($"{pluginRoot}/{relativePath.TrimStart('/')}");
            }

            int separator = normalizedSource.LastIndexOf('/');
            if (separator < 0) return string.Empty;
            var segments = new List<string>(normalizedSource[..separator].Split('/', StringSplitOptions.RemoveEmptyEntries));
            foreach (string segment in relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".") continue;
                if (segment == "..")
                {
                    if (segments.Count <= pluginRootLength) return string.Empty;
                    segments.RemoveAt(segments.Count - 1);
                }
                else
                {
                    segments.Add(segment);
                }
            }
            return NormalizePath(string.Join('/', segments));
        }


        private static int GetPluginRootLength(IReadOnlyList<string> segments)
        {
            if (segments.Count < 2 || !segments[0].Equals("plugins", StringComparison.OrdinalIgnoreCase)) return 0;
            for (int index = 2; index + 1 < segments.Count; index++)
            {
                if (segments[index].Equals("global", StringComparison.OrdinalIgnoreCase))
                    return index + 2;
            }
            return 2;
        }

    }
}
