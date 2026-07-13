using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes
{
    public sealed class BinRstHashGuessingService
    {
        private const int MaximumTextChunkSize = 16 * 1024 * 1024;
        private const int NumericBudget = 5_000_000;
        private const string ExportManifestUrl = "https://raw.communitydragon.org/pbe/cdragon/files.exported.txt";
        private const string PbeRawBaseUrl = "https://raw.communitydragon.org/pbe/";
        private static readonly Regex NumberRegex = new(@"[0-9]+", RegexOptions.Compiled);
        private readonly BinRstHashGuessingStore _store;
        private readonly HashResolverService _resolver;
        private readonly DirectoriesCreator _directories;
        private readonly LogService _log;
        private readonly HttpClient _httpClient;

        public BinRstHashGuessingService(
            BinRstHashGuessingStore store,
            HashResolverService resolver,
            DirectoriesCreator directories,
            LogService log,
            HttpClient httpClient)
        {
            _store = store;
            _resolver = resolver;
            _directories = directories;
            _log = log;
            _httpClient = httpClient;
        }

        public Task<InternalHashSummary> GetSummaryAsync(CancellationToken cancellationToken) => _store.LoadSummaryAsync(cancellationToken);

        public async Task<InternalHashInventory> BuildInventoryAsync(
            string rootDirectory,
            bool includeBin,
            bool includeRst,
            IProgress<InternalHashProgress> progress,
            CancellationToken cancellationToken)
        {
            ValidateRoot(rootDirectory);
            if (!includeBin && !includeRst) throw new ArgumentException("At least one internal hash domain must be selected.");
            await _resolver.LoadAllHashesAsync();
            string[] wads = EnumerateWadContainers(rootDirectory);
            var gamePaths = await LoadGamePathsAsync(cancellationToken);
            var observed = CreateObservedSets();
            int scannedBins = 0, scannedRst = 0;

            await Task.Run(() =>
            {
                for (int index = 0; index < wads.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string wadPath = wads[index];
                    try
                    {
                        using var wad = new WadFile(wadPath);
                        foreach (var pair in wad.Chunks)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!gamePaths.TryGetValue(pair.Key, out string path)) continue;
                            bool isBin = includeBin && path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
                            bool isRst = includeRst && path.EndsWith(".stringtable", StringComparison.OrdinalIgnoreCase);
                            if (!isBin && !isRst) continue;
                            try
                            {
                                using var data = wad.LoadChunkDecompressed(pair.Value);
                                using var stream = new MemoryStream(data.Memory.ToArray(), false);
                                if (isBin)
                                {
                                    ReadBinInventory(stream, observed);
                                    scannedBins++;
                                }
                                else
                                {
                                    ReadRstInventory(stream, observed[InternalHashKind.RstXxh3]);
                                    scannedRst++;
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _log.LogDebug($"Internal Hash Lab skipped '{path}' in {Path.GetFileName(wadPath)}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogError(ex, $"Internal Hash Lab could not read WAD '{wadPath}'.");
                    }
                    progress?.Report(new InternalHashProgress
                    {
                        ProcessedWads = index + 1, TotalWads = wads.Length,
                        ProcessedFiles = scannedBins + scannedRst,
                        CurrentStage = includeBin ? "Building BIN inventory" : "Building RST inventory"
                    });
                }
            }, cancellationToken);

            string fingerprint = BuildFingerprint(wads);
            var selectedObserved = observed.Where(pair =>
                (includeBin && pair.Key is not (InternalHashKind.RstXxh3 or InternalHashKind.RstXxh64)) ||
                (includeRst && pair.Key is InternalHashKind.RstXxh3 or InternalHashKind.RstXxh64))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            await HashResolverService._hashFileAccessLock.WaitAsync(CancellationToken.None);
            try
            {
                await _store.SaveInventoryAsync(selectedObserved, fingerprint, includeBin ? "bin" : "rst", CancellationToken.None);
            }
            finally
            {
                HashResolverService._hashFileAccessLock.Release();
            }
            var unknowns = new Dictionary<InternalHashKind, HashSet<ulong>>();
            foreach (InternalHashKind kind in Enum.GetValues<InternalHashKind>())
                unknowns[kind] = await _store.LoadUnknownAsync(kind, cancellationToken);
            _log.LogSuccess($"Internal Hash Lab inventory completed: {scannedBins} BIN and {scannedRst} RST files parsed.");
            return new InternalHashInventory
            {
                Unknowns = unknowns, PatchFingerprint = fingerprint,
                ScannedBins = scannedBins, ScannedStringTables = scannedRst
            };
        }

        public async Task<InternalHashRunResult> RunContentGuessingAsync(
            string rootDirectory,
            bool includeBin,
            bool includeRst,
            IProgress<InternalHashProgress> progress,
            CancellationToken cancellationToken)
        {
            ValidateRoot(rootDirectory);
            await EnsureInventoryAsync(rootDirectory, includeBin, includeRst, progress, cancellationToken);
            var matcher = await CreateMatcherAsync(includeBin, includeRst, cancellationToken);
            int initial = matcher.Remaining;
            string[] wads = EnumerateWadContainers(rootDirectory);
            var gamePaths = await LoadGamePathsAsync(cancellationToken);
            int scanned = 0;

            await Task.Run(() =>
            {
                for (int index = 0; index < wads.Length && matcher.Remaining > 0; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string wadPath = wads[index];
                    try
                    {
                        using var wad = new WadFile(wadPath);
                        foreach (var pair in wad.Chunks)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!gamePaths.TryGetValue(pair.Key, out string path)) continue;
                            bool isBin = path.EndsWith(".bin", StringComparison.OrdinalIgnoreCase);
                            bool isText = IsTextCandidatePath(path) && pair.Value.UncompressedSize <= MaximumTextChunkSize;
                            if (!isBin && !isText) continue;
                            try
                            {
                                using var data = wad.LoadChunkDecompressed(pair.Value);
                                if (isBin)
                                {
                                    using var stream = new MemoryStream(data.Memory.ToArray(), false);
                                    var tree = new BinTree(stream);
                                    VisitBinStrings(tree, value => matcher.Check(value, InternalHashGuessStrategy.BinContent, path));
                                }
                                else
                                {
                                    CheckTextCandidates(data.Memory.Span, value => matcher.Check(value, InternalHashGuessStrategy.TextContent, path));
                                }
                                scanned++;
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                _log.LogDebug($"Internal Hash Lab content scan skipped '{path}': {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogError(ex, $"Internal Hash Lab could not scan WAD '{wadPath}'.");
                    }
                    progress?.Report(new InternalHashProgress
                    {
                        ProcessedWads = index + 1, TotalWads = wads.Length, ProcessedFiles = scanned,
                        FoundMatches = matcher.Matches.Count, CurrentStage = "Scanning BIN and text content"
                    });
                }
            }, cancellationToken);

            string executable = Directory.EnumerateFiles(rootDirectory, "League of Legends.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (executable != null && matcher.Remaining > 0)
            {
                await ScanTextFileAsync(executable, value => matcher.Check(value, InternalHashGuessStrategy.TextContent, executable), cancellationToken);
                scanned++;
            }
            return await CompleteRunAsync(matcher, initial, scanned, cancellationToken);
        }

        public async Task<InternalHashRunResult> RunRemoteContentGuessingAsync(
            string rootDirectory,
            IProgress<InternalHashProgress> progress,
            CancellationToken cancellationToken)
        {
            ValidateRoot(rootDirectory);
            await EnsureInventoryAsync(rootDirectory, true, false, progress, cancellationToken);
            var matcher = await CreateMatcherAsync(true, false, cancellationToken);
            int initial = matcher.Remaining;
            int scanned = matcher.Remaining == 0 ? 0 : await ScanRemoteJsonAsync(matcher, progress, cancellationToken);
            return await CompleteRunAsync(matcher, initial, scanned, cancellationToken);
        }

        private async Task<int> ScanRemoteJsonAsync(CandidateMatcher matcher, IProgress<InternalHashProgress> progress, CancellationToken cancellationToken)
        {
            string manifest;
            try
            {
                manifest = await _httpClient.GetStringAsync(ExportManifestUrl, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning($"Internal Hash Lab could not load the CommunityDragon export manifest: {ex.Message}");
                return 0;
            }

            string[] paths = manifest.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(path => path.StartsWith("game/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            int processed = 0;
            object matcherLock = new();
            await Parallel.ForEachAsync(paths, new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = cancellationToken
            }, async (path, token) =>
            {
                try
                {
                    using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
                    requestCancellation.CancelAfter(TimeSpan.FromSeconds(5));
                    using var response = await _httpClient.GetAsync(PbeRawBaseUrl + path, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token);
                    if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaximumTextChunkSize) return;
                    byte[] data = await response.Content.ReadAsByteArrayAsync(requestCancellation.Token);
                    if (data.Length > MaximumTextChunkSize) return;
                    CheckTextCandidates(data, candidate =>
                    {
                        lock (matcherLock)
                            matcher.Check(candidate, InternalHashGuessStrategy.RemoteContent, path);
                    });
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    _log.LogDebug($"Internal Hash Lab timed out remote export '{path}'.");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogDebug($"Internal Hash Lab skipped remote export '{path}': {ex.Message}");
                }
                finally
                {
                    int current = Interlocked.Increment(ref processed);
                    if ((current & 0x7f) == 0 || current == paths.Length)
                    {
                        int found;
                        lock (matcherLock) found = matcher.Matches.Count;
                        progress?.Report(new InternalHashProgress
                        {
                            ProcessedFiles = current,
                            FoundMatches = found,
                            CurrentStage = "Scanning CommunityDragon JSON exports"
                        });
                    }
                }
            });
            return processed;
        }

        public async Task<InternalHashRunResult> RunStructuralGuessingAsync(
            string rootDirectory,
            bool includeBin,
            bool includeRst,
            IProgress<InternalHashProgress> progress,
            CancellationToken cancellationToken)
        {
            ValidateRoot(rootDirectory);
            await EnsureInventoryAsync(rootDirectory, includeBin, includeRst, progress, cancellationToken);
            var matcher = await CreateMatcherAsync(includeBin, includeRst, cancellationToken);
            int initial = matcher.Remaining;
            var binKnown = new List<string>();
            foreach (InternalHashKind kind in new[] { InternalHashKind.BinEntries, InternalHashKind.BinFields, InternalHashKind.BinTypes, InternalHashKind.BinHashes })
                binKnown.AddRange((await _store.LoadKnownAsync(kind, cancellationToken)).Values);
            var rst3 = (await _store.LoadKnownAsync(InternalHashKind.RstXxh3, cancellationToken)).Values.ToList();
            var rst64 = (await _store.LoadKnownAsync(InternalHashKind.RstXxh64, cancellationToken)).Values.ToList();
            var gamePaths = (await LoadGamePathsAsync(cancellationToken)).Values;
            long checkedCandidates = 0;

            await Task.Run(() =>
            {
                if (includeBin)
                {
                    CheckCandidates(binKnown, InternalHashGuessStrategy.CrossDictionary, "BIN dictionaries");
                    CheckCandidates(gamePaths, InternalHashGuessStrategy.GamePath, "GAME paths");
                }
                if (includeRst)
                {
                    CheckCandidates(binKnown, InternalHashGuessStrategy.CrossDictionary, "BIN dictionary keys");
                    CheckCandidates(rst3, InternalHashGuessStrategy.CrossVersion, "RST XXH3 keys");
                    CheckCandidates(rst64, InternalHashGuessStrategy.CrossVersion, "RST XXH64 keys");
                    CheckCandidates(GenerateNumberCandidates(rst3, 500, NumericBudget, cancellationToken), InternalHashGuessStrategy.NumericVariant, "RST numeric variants");
                }

                void CheckCandidates(IEnumerable<string> candidates, InternalHashGuessStrategy strategy, string source)
                {
                    foreach (string candidate in candidates)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        matcher.Check(candidate, strategy, source);
                        checkedCandidates++;
                        if ((checkedCandidates & 0x3ffff) == 0)
                            progress?.Report(new InternalHashProgress
                            {
                                ProcessedFiles = checkedCandidates > int.MaxValue ? int.MaxValue : (int)checkedCandidates,
                                FoundMatches = matcher.Matches.Count, CurrentStage = source
                            });
                        if (matcher.Remaining == 0) break;
                    }
                }
            }, cancellationToken);

            return await CompleteRunAsync(matcher, initial, checkedCandidates > int.MaxValue ? int.MaxValue : (int)checkedCandidates, cancellationToken);
        }

        private async Task<InternalHashRunResult> CompleteRunAsync(CandidateMatcher matcher, int initial, int scanned, CancellationToken cancellationToken)
        {
            var matches = matcher.Matches.OrderBy(match => match.Kind).ThenBy(match => match.Value, StringComparer.Ordinal).ToList();
            cancellationToken.ThrowIfCancellationRequested();
            await HashResolverService._hashFileAccessLock.WaitAsync(CancellationToken.None);
            try
            {
                await _store.SaveMatchesAsync(matches, CancellationToken.None);
            }
            finally
            {
                HashResolverService._hashFileAccessLock.Release();
            }
            if (matches.Count > 0) await _resolver.ForceReloadHashesAsync();
            _log.LogSuccess($"Internal Hash Lab completed: {matches.Count} values resolved from {initial} unknown hashes.");
            return new InternalHashRunResult { UnknownHashesAtStart = initial, ScannedFiles = scanned, Matches = matches };
        }

        private async Task EnsureInventoryAsync(string rootDirectory, bool includeBin, bool includeRst, IProgress<InternalHashProgress> progress, CancellationToken cancellationToken)
        {
            string[] wads = EnumerateWadContainers(rootDirectory);
            string fingerprint = BuildFingerprint(wads);
            foreach (string domain in GetSelectedDomains(includeBin, includeRst))
            {
                string marker = Path.Combine(_directories.HashLabPath, $"internal.{domain}.patch.txt");
                string stored = File.Exists(marker) ? (await File.ReadAllTextAsync(marker, cancellationToken)).Trim() : string.Empty;
                if (!string.Equals(stored, fingerprint, StringComparison.Ordinal))
                    await BuildInventoryAsync(rootDirectory, domain == "bin", domain == "rst", progress, cancellationToken);
            }
        }

        private static IEnumerable<string> GetSelectedDomains(bool includeBin, bool includeRst)
        {
            if (includeBin) yield return "bin";
            if (includeRst) yield return "rst";
        }

        private static string[] EnumerateWadContainers(string rootDirectory) =>
            Directory.EnumerateFiles(rootDirectory, "*.wad*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private async Task<CandidateMatcher> CreateMatcherAsync(bool includeBin, bool includeRst, CancellationToken cancellationToken)
        {
            var targets = new Dictionary<InternalHashKind, HashSet<ulong>>();
            foreach (InternalHashKind kind in Enum.GetValues<InternalHashKind>())
            {
                bool isRst = kind is InternalHashKind.RstXxh3 or InternalHashKind.RstXxh64;
                targets[kind] = (isRst ? includeRst : includeBin)
                    ? await _store.LoadUnknownAsync(kind, cancellationToken)
                    : new HashSet<ulong>();
            }
            return new CandidateMatcher(targets);
        }

        private async Task<Dictionary<ulong, string>> LoadGamePathsAsync(CancellationToken cancellationToken)
        {
            var result = new Dictionary<ulong, string>();
            string path = Path.Combine(_directories.HashesPath, "hashes.game.txt");
            if (!File.Exists(path)) return result;
            using var reader = new StreamReader(path);
            while (await reader.ReadLineAsync(cancellationToken) is string line)
                if (line.Length > 17 && ulong.TryParse(line.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    result[hash] = line[17..];
            return result;
        }

        private static Dictionary<InternalHashKind, HashSet<ulong>> CreateObservedSets() => new()
        {
            [InternalHashKind.BinEntries] = new(), [InternalHashKind.BinFields] = new(),
            [InternalHashKind.BinTypes] = new(), [InternalHashKind.BinHashes] = new(),
            [InternalHashKind.RstXxh3] = new(), [InternalHashKind.RstXxh64] = new()
        };

        private static void ReadBinInventory(Stream stream, Dictionary<InternalHashKind, HashSet<ulong>> observed)
        {
            var tree = new BinTree(stream);
            foreach (var pair in tree.Objects)
            {
                if (pair.Key != 0) observed[InternalHashKind.BinEntries].Add(pair.Key);
                if (pair.Value.ClassHash != 0) observed[InternalHashKind.BinTypes].Add(pair.Value.ClassHash);
                foreach (var property in pair.Value.Properties.Values) VisitBinProperty(property, observed);
            }
            foreach (var item in tree.DataOverrides)
            {
                if (item.ObjectPathHash != 0) observed[InternalHashKind.BinEntries].Add(item.ObjectPathHash);
                VisitBinProperty(item.Property, observed);
            }
        }

        private static void VisitBinProperty(BinTreeProperty property, Dictionary<InternalHashKind, HashSet<ulong>> observed)
        {
            if (property.NameHash != 0) observed[InternalHashKind.BinFields].Add(property.NameHash);
            switch (property)
            {
                case BinTreeHash hash when hash.Value != 0: observed[InternalHashKind.BinHashes].Add(hash.Value); break;
                case BinTreeStruct structure:
                    if (structure.ClassHash != 0) observed[InternalHashKind.BinTypes].Add(structure.ClassHash);
                    foreach (var child in structure.Properties.Values) VisitBinProperty(child, observed);
                    break;
                case BinTreeContainer container:
                    foreach (var child in container.Elements) VisitBinProperty(child, observed);
                    break;
                case BinTreeOptional option when option.Value != null: VisitBinProperty(option.Value, observed); break;
                case BinTreeMap map:
                    foreach (var child in map) { VisitBinProperty(child.Key, observed); VisitBinProperty(child.Value, observed); }
                    break;
            }
        }

        private static void VisitBinStrings(BinTree tree, Action<string> check)
        {
            foreach (string dependency in tree.Dependencies) check(dependency);
            foreach (var item in tree.Objects.Values)
                foreach (var property in item.Properties.Values) Visit(property);
            foreach (var item in tree.DataOverrides)
            {
                check(item.PropertyPath);
                Visit(item.Property);
            }

            void Visit(BinTreeProperty property)
            {
                switch (property)
                {
                    case BinTreeString text: check(text.Value); break;
                    case BinTreeStruct structure:
                        foreach (var child in structure.Properties.Values) Visit(child);
                        break;
                    case BinTreeContainer container:
                        foreach (var child in container.Elements) Visit(child);
                        break;
                    case BinTreeOptional option when option.Value != null: Visit(option.Value); break;
                    case BinTreeMap map:
                        foreach (var child in map) { Visit(child.Key); Visit(child.Value); }
                        break;
                }
            }
        }

        private static void ReadRstInventory(Stream stream, HashSet<ulong> observed)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            if (Encoding.ASCII.GetString(reader.ReadBytes(3)) != "RST") throw new InvalidDataException("Invalid RST signature.");
            int version = reader.ReadByte();
            int bits = 40;
            if (version == 2 && reader.ReadBoolean()) reader.BaseStream.Seek(reader.ReadUInt32(), SeekOrigin.Current);
            else if (version is 4 or 5) bits = 38;
            if (version is < 2 or > 5) throw new InvalidDataException($"Unsupported RST version {version}.");
            ulong mask = (1UL << bits) - 1;
            uint count = reader.ReadUInt32();
            for (int index = 0; index < count; index++) observed.Add(reader.ReadUInt64() & mask);
        }

        private static void CheckTextCandidates(ReadOnlySpan<byte> data, Action<string> check)
        {
            int start = -1;
            for (int index = 0; index <= data.Length; index++)
            {
                bool accepted = index < data.Length && IsCandidateByte(data[index]);
                if (accepted)
                {
                    if (start < 0) start = index;
                    continue;
                }
                if (start < 0) continue;
                int length = index - start;
                if (length is >= 5 and <= 512)
                {
                    CheckCandidateSlice(data, start, length, check);
                    if (start >= 2)
                    {
                        int declared = data[start - 2] | data[start - 1] << 8;
                        if (declared == 0 && start >= 4)
                            declared = data[start - 4] | data[start - 3] << 8 | data[start - 2] << 16 | data[start - 1] << 24;
                        if (declared is >= 5 and < 513 && declared < length) CheckCandidateSlice(data, start, declared, check);
                    }
                }
                start = -1;
            }

            static bool IsCandidateByte(byte value) => value is >= (byte)'0' and <= (byte)'9' or
                >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or
                (byte)'_' or (byte)'.' or (byte)' ' or (byte)'/' or (byte)'-' or
                (byte)'@' or (byte)'[' or (byte)']' or (byte)':' ;
        }

        private static void CheckCandidateSlice(ReadOnlySpan<byte> data, int offset, int length, Action<string> check)
        {
            string candidate = Encoding.ASCII.GetString(data.Slice(offset, length)).Trim();
            if (candidate.Length >= 5) check(candidate);
        }

        private static async Task ScanTextFileAsync(string path, Action<string> check, CancellationToken cancellationToken)
        {
            const int blockSize = 4 * 1024 * 1024;
            const int overlap = 512;
            byte[] buffer = new byte[blockSize + overlap];
            int carried = 0;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, blockSize, true);
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(carried, blockSize), cancellationToken);
                if (read == 0) break;
                int length = carried + read;
                CheckTextCandidates(buffer.AsSpan(0, length), check);
                carried = Math.Min(overlap, length);
                buffer.AsSpan(length - carried, carried).CopyTo(buffer);
            }
        }

        private static IEnumerable<string> GenerateNumberCandidates(IEnumerable<string> values, int limit, int budget, CancellationToken cancellationToken)
        {
            var formats = new HashSet<string>(StringComparer.Ordinal);
            foreach (string value in values)
                foreach (Match match in NumberRegex.Matches(value ?? string.Empty))
                    formats.Add(value[..match.Index] + "{0}" + value[(match.Index + match.Length)..]);
            int generated = 0;
            foreach (string format in formats.OrderBy(value => value, StringComparer.Ordinal))
                for (int number = 0; number < limit && generated < budget; number++, generated++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return string.Format(CultureInfo.InvariantCulture, format, number);
                }
        }

        private static bool IsTextCandidatePath(string path) => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".inibin", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase);

        private static string BuildFingerprint(IEnumerable<string> paths)
        {
            ulong xor = 0, sum = 0;
            long count = 0;
            foreach (string path in paths)
            {
                var info = new FileInfo(path);
                ulong value = unchecked((ulong)info.Length ^ (ulong)info.LastWriteTimeUtc.Ticks);
                xor ^= value; sum = unchecked(sum + value); count++;
            }
            return $"internal:{count}:{xor:x16}:{sum:x16}";
        }

        private static void ValidateRoot(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
                throw new DirectoryNotFoundException("The selected game directory does not exist.");
        }

        private sealed class CandidateMatcher
        {
            private const ulong Rst38Mask = (1UL << 38) - 1;
            private readonly Dictionary<InternalHashKind, HashSet<ulong>> _targets;
            private readonly Dictionary<(InternalHashKind Kind, ulong Hash), InternalHashGuessMatch> _matches = new();
            public CandidateMatcher(Dictionary<InternalHashKind, HashSet<ulong>> targets) => _targets = targets;
            public IReadOnlyCollection<InternalHashGuessMatch> Matches => _matches.Values;
            public int Remaining => _targets.Values.Sum(values => values.Count);

            public void Check(string value, InternalHashGuessStrategy strategy, string source)
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length > 512) return;
                string candidate = value.Trim().ToLowerInvariant();
                uint fnv = Fnv1a.HashLower(candidate);
                bool content = strategy is InternalHashGuessStrategy.BinContent or InternalHashGuessStrategy.TextContent or InternalHashGuessStrategy.RemoteContent;
                bool crossDictionary = strategy == InternalHashGuessStrategy.CrossDictionary;
                bool gamePath = strategy == InternalHashGuessStrategy.GamePath;
                if (content || crossDictionary || gamePath)
                {
                    if (candidate.Contains('/'))
                        Check32(InternalHashKind.BinEntries, fnv, candidate, strategy, source);
                    Check32(InternalHashKind.BinHashes, fnv, candidate, strategy, source);
                }
                if ((content || crossDictionary) && IsIdentifier(candidate))
                {
                    Check32(InternalHashKind.BinFields, fnv, candidate, strategy, source);
                    Check32(InternalHashKind.BinTypes, fnv, candidate, strategy, source);
                }

                if (content || strategy is InternalHashGuessStrategy.CrossDictionary or InternalHashGuessStrategy.CrossVersion or InternalHashGuessStrategy.NumericVariant)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(candidate);
                    ulong xxh3 = XxHash3.HashToUInt64(bytes);
                    CheckRst(InternalHashKind.RstXxh3, xxh3, candidate, strategy, source, new[] { 38 });
                    ulong xxh64 = XxHash64.HashToUInt64(bytes);
                    CheckRst(InternalHashKind.RstXxh64, xxh64, candidate, strategy, source, new[] { 64, 38, 39, 40 });
                }
            }

            private static bool IsIdentifier(string value)
            {
                if (value.Length == 0 || value.Length > 128 || !(char.IsLetter(value[0]) || value[0] == '_')) return false;
                for (int index = 1; index < value.Length; index++)
                    if (!(char.IsLetterOrDigit(value[index]) || value[index] == '_')) return false;
                return true;
            }

            private void Check32(InternalHashKind kind, uint hash, string value, InternalHashGuessStrategy strategy, string source)
            {
                if (!_targets[kind].Remove(hash)) return;
                _matches[(kind, hash)] = new InternalHashGuessMatch
                {
                    Hash = hash, LookupHash = hash, HashBits = 32, Value = value,
                    Kind = kind, Strategy = strategy, Source = source
                };
            }

            private void CheckRst(InternalHashKind kind, ulong fullHash, string value, InternalHashGuessStrategy strategy, string source, IEnumerable<int> bitOptions)
            {
                foreach (int bits in bitOptions)
                {
                    ulong lookup = bits == 64 ? fullHash : fullHash & ((1UL << bits) - 1);
                    if (!_targets[kind].Remove(lookup)) continue;
                    _matches[(kind, fullHash)] = new InternalHashGuessMatch
                    {
                        Hash = fullHash, LookupHash = lookup, HashBits = bits, Value = value,
                        Kind = kind, Strategy = strategy, Source = source
                    };
                    break;
                }
            }
        }
    }
}
