using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers.Lcu;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class LcuUnknownTrackerDiagnostic
    {
        private sealed class UnknownEvidence
        {
            public ulong Hash { get; init; }
            public HashSet<string> Wads { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> Extensions { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<long> Sizes { get; } = new();
            public HashSet<string> Fingerprints { get; } = new(StringComparer.Ordinal);
            public HashSet<ulong> PayloadTwins { get; } = new();
            public HashSet<string> KnownPayloadPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> VerifiedPayloadCandidates { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed record KnownPayloadEntry(string Plugin, string Path, string Fingerprint);
        private sealed record PayloadAliasPair(string SourcePlugin, string SourcePath, string TargetPlugin, string TargetPath);

        public static void Run(string[] args)
        {
            string pbeRoot = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                ?? @"C:\Riot Games\League of Legends (PBE)";
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string unknownsPath = Path.Combine(localAppData, "AssetsManager", "hash_lab", "unknowns.lcu.txt");
            string catalogPath = Path.Combine(localAppData, "AssetsManager", "hashes", "hashes.lcu.txt");
            string reportPath = args.FirstOrDefault(arg => arg.StartsWith("--out=", StringComparison.OrdinalIgnoreCase))?[6..]
                ?? Path.Combine(localAppData, "AssetsManager", "hash_lab", "unresolved.lcu.tsv");

            if (!File.Exists(unknownsPath))
            {
                Console.WriteLine($"Unknowns file not found: {unknownsPath}");
                return;
            }
            if (!File.Exists(catalogPath))
            {
                Console.WriteLine($"LCU hash catalog not found: {catalogPath}");
                return;
            }

            HashSet<ulong> targets = ReadUnknowns(unknownsPath);
            Dictionary<ulong, string> knownPaths = ReadKnownPaths(catalogPath);
            string pluginsDir = FindPluginsDirectory(pbeRoot);
            if (pluginsDir.Length == 0)
            {
                Console.WriteLine($"Plugins directory not found below: {pbeRoot}");
                return;
            }

            List<string> wads = Directory.EnumerateFiles(pluginsDir, "*.wad", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var evidence = targets.ToDictionary(hash => hash, hash => new UnknownEvidence { Hash = hash });
            var fingerprintToUnknowns = new Dictionary<string, HashSet<ulong>>(StringComparer.Ordinal);
            var targetSizes = new HashSet<long>();

            Console.WriteLine($"Tracking {targets.Count} unresolved LCU hashes across {wads.Count} WADs...");
            foreach (string wadPath in wads)
            {
                string wadName = Path.GetRelativePath(pluginsDir, wadPath).Replace('\\', '/');
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (!targets.Contains(pair.Key)) continue;
                        try
                        {
                            byte[] data = ReadChunk(wad, pair.Value);
                            string fingerprint = Fingerprint(data);
                            UnknownEvidence item = evidence[pair.Key];
                            item.Wads.Add(wadName);
                            item.Sizes.Add(data.LongLength);
                            item.Fingerprints.Add(fingerprint);
                            string extension = FileTypeDetector.GuessExtension(data);
                            item.Extensions.Add(string.IsNullOrWhiteSpace(extension) ? "bin" : extension.TrimStart('.').ToLowerInvariant());
                            targetSizes.Add(data.LongLength);

                            if (!fingerprintToUnknowns.TryGetValue(fingerprint, out HashSet<ulong> hashes))
                                fingerprintToUnknowns[fingerprint] = hashes = new HashSet<ulong>();
                            hashes.Add(pair.Key);
                        }
                        catch (Exception exception)
                        {
                            Console.WriteLine($"  Failed to inspect {pair.Key:x16} in {wadName}: {exception.Message}");
                        }
                    }
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"  Failed to open {wadName}: {exception.Message}");
                }
            }

            foreach (HashSet<ulong> group in fingerprintToUnknowns.Values.Where(group => group.Count > 1))
            {
                foreach (ulong hash in group)
                    evidence[hash].PayloadTwins.UnionWith(group.Where(other => other != hash));
            }

            Console.WriteLine("Correlating unresolved payloads with already-known LCU files...");
            foreach (string wadPath in wads)
            {
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (!knownPaths.TryGetValue(pair.Key, out string knownPath)) continue;
                        if (!targetSizes.Contains(pair.Value.UncompressedSize)) continue;
                        try
                        {
                            byte[] data = ReadChunk(wad, pair.Value);
                            string fingerprint = Fingerprint(data);
                            if (!fingerprintToUnknowns.TryGetValue(fingerprint, out HashSet<ulong> hashes)) continue;
                            foreach (ulong hash in hashes)
                                evidence[hash].KnownPayloadPaths.Add(knownPath);
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }

            Dictionary<string, HashSet<string>> knownDirectoriesByPlugin = BuildKnownDirectoriesByPlugin(knownPaths.Values);
            foreach (UnknownEvidence item in evidence.Values.Where(item => item.KnownPayloadPaths.Count > 0))
            {
                foreach (string targetPlugin in item.Wads.Select(ExtractPluginNameFromWad).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (string knownPayloadPath in item.KnownPayloadPaths)
                    {
                        if (TrySplitPluginPath(knownPayloadPath, out _, out string relativeTail))
                            TestVerifiedCandidate(item, $"plugins/{targetPlugin}/{relativeTail}");

                        string basename = Path.GetFileName(knownPayloadPath);
                        if (basename.Length == 0 || !knownDirectoriesByPlugin.TryGetValue(targetPlugin, out HashSet<string> directories))
                            continue;
                        foreach (string directory in directories)
                            TestVerifiedCandidate(item, $"{directory}/{basename}");
                    }
                }
            }

            Console.WriteLine("Replaying production LCU GrepWad against resolved source chunks...");
            IReadOnlyDictionary<ulong, HashGuessMatch> grepMatches = ReplayGrepWad(targets, knownPaths, wads);

            Console.WriteLine("Testing evidence-driven LCU family candidates...");
            IReadOnlyDictionary<ulong, string> familyMatches = TestEvidenceDrivenFamilies(targets, knownPaths.Values);

            Console.WriteLine("Mining known-payload alias transforms for unresolved twins...");
            IReadOnlyDictionary<ulong, string> aliasMatches = MinePayloadAliasTransforms(
                targets,
                evidence,
                knownPaths,
                wads);

            Directory.CreateDirectory(Path.GetDirectoryName(reportPath) ?? ".");
            using (var writer = new StreamWriter(reportPath, false))
            {
                writer.WriteLine("hash\twads\textensions\tsizes\tpayload_twins\tknown_payload_paths\tverified_payload_candidates");
                foreach (UnknownEvidence item in evidence.Values.OrderBy(item => item.Hash))
                {
                    writer.WriteLine(string.Join('\t', new[]
                    {
                        item.Hash.ToString("x16", CultureInfo.InvariantCulture),
                        Escape(string.Join(';', item.Wads.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))),
                        Escape(string.Join(';', item.Extensions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))),
                        Escape(string.Join(';', item.Sizes.OrderBy(value => value))),
                        Escape(string.Join(';', item.PayloadTwins.OrderBy(value => value).Select(value => value.ToString("x16", CultureInfo.InvariantCulture)))),
                        Escape(string.Join(';', item.KnownPayloadPaths.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))),
                        Escape(string.Join(';', item.VerifiedPayloadCandidates.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)))
                    }));
                }
            }

            int located = evidence.Values.Count(item => item.Wads.Count > 0);
            int withUnknownTwin = evidence.Values.Count(item => item.PayloadTwins.Count > 0);
            int withKnownTwin = evidence.Values.Count(item => item.KnownPayloadPaths.Count > 0);
            int verifiedFromPayload = evidence.Values.Count(item => item.VerifiedPayloadCandidates.Count > 0);
            int duplicateGroups = fingerprintToUnknowns.Values.Count(group => group.Count > 1);

            Console.WriteLine();
            Console.WriteLine("==================================================");
            Console.WriteLine("    LCU UNRESOLVED TRACKER");
            Console.WriteLine("==================================================");
            Console.WriteLine($"Targets:                  {targets.Count}");
            Console.WriteLine($"Located in current WADs:  {located}");
            Console.WriteLine($"Missing from current WADs: {targets.Count - located}");
            Console.WriteLine($"Duplicate payload groups: {duplicateGroups}");
            Console.WriteLine($"Hashes with unknown twin: {withUnknownTwin}");
            Console.WriteLine($"Hashes with known twin:   {withKnownTwin}");
            Console.WriteLine($"Verified payload cracks:  {verifiedFromPayload}");
            Console.WriteLine($"Production GrepWad hits:  {grepMatches.Count}");
            Console.WriteLine($"Evidence family hits:     {familyMatches.Count}");
            Console.WriteLine($"Payload alias hits:       {aliasMatches.Count}");
            Console.WriteLine();
            if (grepMatches.Count > 0)
            {
                Console.WriteLine("Production GrepWad matches:");
                foreach (HashGuessMatch match in grepMatches.Values.OrderBy(match => match.Hash))
                    Console.WriteLine($"  {match.Hash:x16}  {match.Path}  <- {match.SourceChunkHash:x16}");
                Console.WriteLine();
            }
            if (familyMatches.Count > 0)
            {
                Console.WriteLine("Evidence-driven family matches:");
                foreach (var match in familyMatches.OrderBy(match => match.Key))
                    Console.WriteLine($"  {match.Key:x16}  {match.Value}");
                Console.WriteLine();
            }
            if (aliasMatches.Count > 0)
            {
                Console.WriteLine("Payload-alias matches:");
                foreach (var match in aliasMatches.OrderBy(match => match.Key))
                    Console.WriteLine($"  {match.Key:x16}  {match.Value}");
                Console.WriteLine();
            }
            if (verifiedFromPayload > 0)
            {
                Console.WriteLine("Verified payload-derived paths:");
                foreach (UnknownEvidence item in evidence.Values.Where(item => item.VerifiedPayloadCandidates.Count > 0).OrderBy(item => item.Hash))
                {
                    foreach (string candidate in item.VerifiedPayloadCandidates.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                        Console.WriteLine($"  {item.Hash:x16}  {candidate}");
                }
                Console.WriteLine();
            }
            Console.WriteLine("Largest unresolved containers:");
            foreach (var group in evidence.Values
                         .SelectMany(item => item.Wads.Select(wad => (item.Hash, Wad: wad)))
                         .GroupBy(item => item.Wad, StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(group => group.Select(item => item.Hash).Distinct().Count())
                         .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                         .Take(12))
            {
                Console.WriteLine($"  {group.Select(item => item.Hash).Distinct().Count(),4}  {group.Key}");
            }

            Console.WriteLine();
            Console.WriteLine($"Report: {reportPath}");
        }

        private static IReadOnlyDictionary<ulong, string> TestEvidenceDrivenFamilies(
            IReadOnlySet<ulong> targets,
            IEnumerable<string> knownPaths)
        {
            var matches = new Dictionary<ulong, string>();

            void Test(string candidate)
            {
                string normalized = candidate.Replace('\\', '/').Trim().ToLowerInvariant();
                if (normalized.Length == 0) return;
                ulong hash = XxHash64Ext.Hash(normalized);
                if (targets.Contains(hash) && !matches.ContainsKey(hash))
                    matches[hash] = normalized;
            }

            var championDirectories = knownPaths
                .Select(path => path.Replace('\\', '/').Trim().ToLowerInvariant())
                .Where(path => path.StartsWith("plugins/rcp-be-lol-game-data/global/default/v1/champion-chroma-images/", StringComparison.Ordinal))
                .Select(path => Path.GetDirectoryName(path)?.Replace('\\', '/'))
                .Where(path => !string.IsNullOrEmpty(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (string directory in championDirectories)
            {
                string championText = directory[(directory.LastIndexOf('/') + 1)..];
                if (!int.TryParse(championText, NumberStyles.None, CultureInfo.InvariantCulture, out int championId)) continue;
                for (int suffix = 0; suffix <= 999; suffix++)
                    Test($"{directory}/{championId}{suffix:000}.png");
            }

            string[] championFamilies =
            {
                "champion-ability-icons", "champion-cards", "champion-chroma-images", "champion-icons",
                "champion-splashes", "champion-splash-videos", "champion-tiles"
            };
            foreach (string path in knownPaths
                         .Select(path => path.Replace('\\', '/').Trim().ToLowerInvariant())
                         .Where(path => path.Contains("/v1/champion-chroma-images/", StringComparison.Ordinal)))
            {
                const string marker = "/v1/champion-chroma-images/";
                int markerIndex = path.IndexOf(marker, StringComparison.Ordinal);
                string root = path[..markerIndex] + "/v1/";
                string tail = path[(markerIndex + marker.Length)..];
                foreach (string family in championFamilies)
                    Test(root + family + "/" + tail);
            }

            const string trovesTargetRoot = "plugins/rcp-fe-lol-tft-troves/global/default";
            var trovesTargetDirectories = knownPaths
                .Select(path => path.Replace('\\', '/').Trim().ToLowerInvariant())
                .Where(path => path.StartsWith(trovesTargetRoot + "/", StringComparison.Ordinal))
                .Select(path => path[..path.LastIndexOf('/')])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var trovesSourcePaths = knownPaths
                .Select(path => path.Replace('\\', '/').Trim().ToLowerInvariant())
                .Where(path => path.Contains("/assets/ux/tft/troves/", StringComparison.Ordinal) ||
                               path.Contains("/assets/ux/tft/troves_bannercontent/", StringComparison.Ordinal) ||
                               path.Contains("/assets/ux/tftmobile/currency/", StringComparison.Ordinal))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (string sourcePath in trovesSourcePaths)
            {
                string basename = Path.GetFileName(sourcePath);
                foreach (string directory in trovesTargetDirectories)
                    Test($"{directory}/{basename}");

                const string uxTftMarker = "/assets/ux/tft/";
                int uxIndex = sourcePath.IndexOf(uxTftMarker, StringComparison.Ordinal);
                if (uxIndex >= 0)
                {
                    string tail = sourcePath[(uxIndex + uxTftMarker.Length)..];
                    foreach (string prefix in new[] { "", "images/", "assets/", "assets/images/" })
                        Test($"{trovesTargetRoot}/{prefix}{tail}");
                    if (tail.StartsWith("troves/", StringComparison.Ordinal))
                    {
                        string shortTail = tail["troves/".Length..];
                        foreach (string prefix in new[] { "", "images/", "assets/", "assets/images/", "troves/", "images/troves/" })
                            Test($"{trovesTargetRoot}/{prefix}{shortTail}");
                    }
                }
            }

            return matches;
        }

        private static IReadOnlyDictionary<ulong, string> MinePayloadAliasTransforms(
            IReadOnlySet<ulong> targets,
            IReadOnlyDictionary<ulong, UnknownEvidence> evidence,
            IReadOnlyDictionary<ulong, string> knownPaths,
            IReadOnlyList<string> wads)
        {
            var relevantPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var relevantPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (UnknownEvidence item in evidence.Values.Where(item => item.KnownPayloadPaths.Count > 0))
            {
                foreach (string targetPlugin in item.Wads
                             .Select(ExtractPluginNameFromWad)
                             .Where(value => value.Length > 0)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    relevantPlugins.Add(targetPlugin);
                    foreach (string knownPayloadPath in item.KnownPayloadPaths)
                    {
                        if (!TrySplitPluginPath(knownPayloadPath, out string sourcePlugin, out _)) continue;
                        relevantPlugins.Add(sourcePlugin);
                        relevantPairs.Add(sourcePlugin + "\n" + targetPlugin);
                    }
                }
            }

            var fingerprintToKnown = new Dictionary<string, List<KnownPayloadEntry>>(StringComparer.Ordinal);
            foreach (string wadPath in wads)
            {
                string plugin = ExtractPluginNameFromWad(Path.GetDirectoryName(wadPath) == null
                    ? wadPath
                    : Path.GetFileName(Path.GetDirectoryName(wadPath)) + "/" + Path.GetFileName(wadPath));
                if (!relevantPlugins.Contains(plugin)) continue;

                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (!knownPaths.TryGetValue(pair.Key, out string knownPath)) continue;
                        try
                        {
                            byte[] data = ReadChunk(wad, pair.Value);
                            string fingerprint = Fingerprint(data);
                            if (!fingerprintToKnown.TryGetValue(fingerprint, out List<KnownPayloadEntry> entries))
                                fingerprintToKnown[fingerprint] = entries = new List<KnownPayloadEntry>();
                            entries.Add(new KnownPayloadEntry(plugin, knownPath.ToLowerInvariant(), fingerprint));
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }

            var aliasPairs = new List<PayloadAliasPair>();
            foreach (List<KnownPayloadEntry> group in fingerprintToKnown.Values.Where(entries => entries.Count > 1))
            {
                foreach (KnownPayloadEntry source in group)
                foreach (KnownPayloadEntry target in group)
                {
                    if (ReferenceEquals(source, target) || source.Path.Equals(target.Path, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!relevantPairs.Contains(source.Plugin + "\n" + target.Plugin)) continue;
                    aliasPairs.Add(new PayloadAliasPair(source.Plugin, source.Path, target.Plugin, target.Path));
                }
            }

            var matches = new Dictionary<ulong, string>();
            void Test(string candidate)
            {
                string normalized = candidate.Replace('\\', '/').Trim().ToLowerInvariant();
                if (normalized.Length == 0) return;
                ulong hash = XxHash64Ext.Hash(normalized);
                if (targets.Contains(hash) && !matches.ContainsKey(hash))
                    matches[hash] = normalized;
            }

            foreach (UnknownEvidence item in evidence.Values.Where(item => item.KnownPayloadPaths.Count > 0 && targets.Contains(item.Hash)))
            {
                foreach (string targetPlugin in item.Wads
                             .Select(ExtractPluginNameFromWad)
                             .Where(value => value.Length > 0)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    foreach (string sourcePath in item.KnownPayloadPaths)
                    {
                        if (!TrySplitPluginPath(sourcePath, out string sourcePlugin, out _)) continue;
                        List<PayloadAliasPair> pairSet = aliasPairs
                            .Where(pair => pair.SourcePlugin.Equals(sourcePlugin, StringComparison.OrdinalIgnoreCase) &&
                                           pair.TargetPlugin.Equals(targetPlugin, StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        if (pairSet.Count == 0) continue;

                        string normalizedSource = sourcePath.Replace('\\', '/').ToLowerInvariant();
                        string sourceDirectory = normalizedSource[..normalizedSource.LastIndexOf('/')];
                        string sourceFileName = Path.GetFileName(normalizedSource);
                        string sourceStem = Path.GetFileNameWithoutExtension(normalizedSource);
                        string sourceExtension = Path.GetExtension(normalizedSource);

                        foreach (PayloadAliasPair pair in pairSet)
                        {
                            string aliasSourceDirectory = pair.SourcePath[..pair.SourcePath.LastIndexOf('/')];
                            string aliasTargetDirectory = pair.TargetPath[..pair.TargetPath.LastIndexOf('/')];
                            string aliasSourceFile = Path.GetFileName(pair.SourcePath);
                            string aliasTargetFile = Path.GetFileName(pair.TargetPath);

                            if (sourceDirectory.Equals(aliasSourceDirectory, StringComparison.OrdinalIgnoreCase))
                            {
                                Test(aliasTargetDirectory + "/" + sourceFileName);
                                foreach (string fileVariant in ApplyFileNameTransform(sourceFileName, aliasSourceFile, aliasTargetFile))
                                    Test(aliasTargetDirectory + "/" + fileVariant);
                            }
                        }

                        foreach (var directoryGroup in pairSet
                                     .GroupBy(pair => pair.SourcePath[..pair.SourcePath.LastIndexOf('/')] + "\n" + pair.TargetPath[..pair.TargetPath.LastIndexOf('/')], StringComparer.OrdinalIgnoreCase)
                                     .Where(group => group.Count() >= 2))
                        {
                            string[] keyParts = directoryGroup.Key.Split('\n');
                            string aliasSourceDirectory = keyParts[0];
                            string aliasTargetDirectory = keyParts[1];
                            if (!sourceDirectory.Equals(aliasSourceDirectory, StringComparison.OrdinalIgnoreCase)) continue;

                            List<string> sourceStems = directoryGroup.Select(pair => Path.GetFileNameWithoutExtension(pair.SourcePath)).ToList();
                            List<string> targetStems = directoryGroup.Select(pair => Path.GetFileNameWithoutExtension(pair.TargetPath)).ToList();
                            string sourcePrefix = LongestCommonPrefix(sourceStems);
                            string targetPrefix = LongestCommonPrefix(targetStems);
                            if (sourcePrefix.Length >= 2 && sourceStem.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
                                Test(aliasTargetDirectory + "/" + targetPrefix + sourceStem[sourcePrefix.Length..] + sourceExtension);

                            string sourceSuffix = LongestCommonSuffix(sourceStems);
                            string targetSuffix = LongestCommonSuffix(targetStems);
                            if (sourceSuffix.Length >= 2 && sourceStem.EndsWith(sourceSuffix, StringComparison.OrdinalIgnoreCase))
                                Test(aliasTargetDirectory + "/" + sourceStem[..^sourceSuffix.Length] + targetSuffix + sourceExtension);
                        }
                    }
                }
            }

            Console.WriteLine($"  Relevant plugins: {relevantPlugins.Count}");
            Console.WriteLine($"  Known duplicate payload groups: {fingerprintToKnown.Values.Count(entries => entries.Count > 1)}");
            Console.WriteLine($"  Learned directed alias pairs: {aliasPairs.Count}");
            return matches;
        }

        private static IEnumerable<string> ApplyFileNameTransform(string sourceFileName, string aliasSourceFile, string aliasTargetFile)
        {
            string sourceExtension = Path.GetExtension(sourceFileName);
            if (!sourceExtension.Equals(Path.GetExtension(aliasSourceFile), StringComparison.OrdinalIgnoreCase) ||
                !sourceExtension.Equals(Path.GetExtension(aliasTargetFile), StringComparison.OrdinalIgnoreCase))
                yield break;

            string sourceStem = Path.GetFileNameWithoutExtension(sourceFileName);
            string aliasSourceStem = Path.GetFileNameWithoutExtension(aliasSourceFile);
            string aliasTargetStem = Path.GetFileNameWithoutExtension(aliasTargetFile);

            string commonPrefix = CommonPrefix(aliasSourceStem, aliasTargetStem);
            string commonSuffix = CommonSuffix(aliasSourceStem, aliasTargetStem, commonPrefix.Length);
            int sourceMiddleLength = aliasSourceStem.Length - commonPrefix.Length - commonSuffix.Length;
            int targetMiddleLength = aliasTargetStem.Length - commonPrefix.Length - commonSuffix.Length;
            if (sourceMiddleLength > 0 && targetMiddleLength >= 0 &&
                sourceStem.StartsWith(commonPrefix, StringComparison.OrdinalIgnoreCase) &&
                sourceStem.EndsWith(commonSuffix, StringComparison.OrdinalIgnoreCase) &&
                sourceStem.Length >= commonPrefix.Length + commonSuffix.Length)
            {
                string replacement = aliasTargetStem.Substring(commonPrefix.Length, targetMiddleLength);
                string sourceMiddle = sourceStem.Substring(commonPrefix.Length, sourceStem.Length - commonPrefix.Length - commonSuffix.Length);
                if (!sourceMiddle.Equals(aliasSourceStem.Substring(commonPrefix.Length, sourceMiddleLength), StringComparison.OrdinalIgnoreCase))
                    yield return commonPrefix + replacement + sourceMiddle + commonSuffix + sourceExtension;
            }
        }

        private static string LongestCommonPrefix(IReadOnlyList<string> values)
        {
            if (values.Count == 0) return string.Empty;
            string prefix = values[0];
            for (int i = 1; i < values.Count && prefix.Length > 0; i++)
                prefix = CommonPrefix(prefix, values[i]);
            return prefix;
        }

        private static string LongestCommonSuffix(IReadOnlyList<string> values)
        {
            if (values.Count == 0) return string.Empty;
            string suffix = values[0];
            for (int i = 1; i < values.Count && suffix.Length > 0; i++)
                suffix = CommonSuffix(suffix, values[i], 0);
            return suffix;
        }

        private static string CommonPrefix(string left, string right)
        {
            int count = Math.Min(left.Length, right.Length);
            int index = 0;
            while (index < count && char.ToLowerInvariant(left[index]) == char.ToLowerInvariant(right[index])) index++;
            return left[..index];
        }

        private static string CommonSuffix(string left, string right, int reservedPrefixLength)
        {
            int max = Math.Min(left.Length, right.Length) - reservedPrefixLength;
            int count = 0;
            while (count < max &&
                   char.ToLowerInvariant(left[left.Length - 1 - count]) == char.ToLowerInvariant(right[right.Length - 1 - count]))
                count++;
            return count == 0 ? string.Empty : left[^count..];
        }

        private static IReadOnlyDictionary<ulong, HashGuessMatch> ReplayGrepWad(
            IReadOnlySet<ulong> targets,
            IReadOnlyDictionary<ulong, string> knownPaths,
            IReadOnlyList<string> wads)
        {
            var engine = new HashGuessEngine(HashGuessDomain.Lcu, targets.ToHashSet());
            var guesser = new LcuHashGuesser(knownPaths.Values, null);

            foreach (string wadPath in wads)
            {
                if (engine.RemainingUnknownCount == 0) break;
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (!knownPaths.TryGetValue(pair.Key, out string sourcePath)) continue;
                        if (!guesser.ShouldGrepExtension(Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant())) continue;
                        try
                        {
                            using var owner = wad.LoadChunkDecompressed(pair.Value);
                            guesser.GrepWad(
                                engine,
                                owner.DangerousGetArray(),
                                sourcePath,
                                wadPath,
                                pair.Key,
                                CancellationToken.None);
                        }
                        catch
                        {
                        }
                    }
                }
                catch
                {
                }
            }

            return engine.Matches;
        }

        private static HashSet<ulong> ReadUnknowns(string path)
        {
            var result = new HashSet<ulong>();
            foreach (string line in File.ReadLines(path))
            {
                if (ulong.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    result.Add(hash);
            }
            return result;
        }

        private static Dictionary<ulong, string> ReadKnownPaths(string path)
        {
            var result = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(path))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                int space = trimmed.IndexOf(' ');
                if (space > 0 && ulong.TryParse(trimmed[..space], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                {
                    string knownPath = trimmed[(space + 1)..].Trim();
                    if (knownPath.Length > 0) result[hash] = knownPath;
                }
                else if (space < 0)
                {
                    string knownPath = trimmed.ToLowerInvariant();
                    result[XxHash64Ext.Hash(knownPath)] = knownPath;
                }
            }
            return result;
        }

        private static Dictionary<string, HashSet<string>> BuildKnownDirectoriesByPlugin(IEnumerable<string> paths)
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (!TrySplitPluginPath(path, out string plugin, out _)) continue;
                int separator = path.LastIndexOf('/');
                if (separator <= 0) continue;
                if (!result.TryGetValue(plugin, out HashSet<string> directories))
                    result[plugin] = directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                directories.Add(path[..separator].ToLowerInvariant());
            }
            return result;
        }

        private static bool TrySplitPluginPath(string path, out string plugin, out string relativeTail)
        {
            plugin = string.Empty;
            relativeTail = string.Empty;
            string normalized = path.Replace('\\', '/').Trim().ToLowerInvariant();
            const string prefix = "plugins/";
            if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) return false;
            int pluginEnd = normalized.IndexOf('/', prefix.Length);
            if (pluginEnd < 0 || pluginEnd + 1 >= normalized.Length) return false;
            plugin = normalized[prefix.Length..pluginEnd];
            relativeTail = normalized[(pluginEnd + 1)..];
            return plugin.Length > 0 && relativeTail.Length > 0;
        }

        private static string ExtractPluginNameFromWad(string wad)
        {
            string normalized = wad.Replace('\\', '/').Trim('/');
            int separator = normalized.IndexOf('/');
            return separator > 0 ? normalized[..separator].ToLowerInvariant() : string.Empty;
        }

        private static void TestVerifiedCandidate(UnknownEvidence item, string candidate)
        {
            string normalized = candidate.Replace('\\', '/').Trim().ToLowerInvariant();
            if (normalized.Length == 0) return;
            if (XxHash64Ext.Hash(normalized) == item.Hash)
                item.VerifiedPayloadCandidates.Add(normalized);
        }

        private static string FindPluginsDirectory(string pbeRoot)
        {
            foreach (string candidate in new[]
                     {
                         Path.Combine(pbeRoot, "Plugins"),
                         Path.Combine(pbeRoot, "LeagueClient", "Plugins")
                     })
            {
                if (Directory.Exists(candidate)) return candidate;
            }
            return string.Empty;
        }

        private static byte[] ReadChunk(WadFile wad, WadChunk chunk)
        {
            using var owner = wad.LoadChunkDecompressed(chunk);
            ArraySegment<byte> segment = owner.DangerousGetArray();
            return segment.Array[segment.Offset..(segment.Offset + segment.Count)];
        }

        private static string Fingerprint(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

        private static string Escape(string value) => value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
