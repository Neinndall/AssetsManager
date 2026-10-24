using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Hashes.Guessers.Game
{
    internal sealed partial class GameHashGuesser
    {
        private readonly ConditionalWeakTable<HashGuessEngine, ConcurrentDictionary<string, byte>> _scannedWadCharacters = new();
        private readonly ConditionalWeakTable<HashGuessEngine, RegaliaMatchIndex> _regaliaMatches = new();
        private readonly ConditionalWeakTable<HashGuessEngine, GameBinLinkTemplateIndex> _linkTemplates = new();
        private sealed record RegaliaMatchIndex(HashSet<ulong> Targets, Dictionary<ulong, string> Paths);

        internal override void ReleaseMemory()
        {
            base.ReleaseMemory();
            _scannedWadCharacters.Clear();
            _regaliaMatches.Clear();
            _linkTemplates.Clear();
        }

        internal override bool ShouldGrepExtension(string extension) =>
            extension is not ("dds" or "jpg" or "png" or "tga" or "ttf" or "otf" or "ogg" or "webm" or
                "anm" or "skl" or "skn" or "scb" or "sco" or "troybin" or "bnk" or "wpk" or "tex");

        internal override void GrepWad(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Count == 0) return;

            // Local wrappers for Check (CDTB check) and CheckIter (CDTB check_iter) keep
            // the current engine, source WAD/chunk and cancellation context on every call.
            // The candidate overload also preserves each candidate's strategy.
            void CheckGame(string path, HashGuessStrategy strategy = HashGuessStrategy.BinLengthPath)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(path))
                    Check(engine, path, strategy, sourceWadPath, sourceChunkHash);
            }

            void CheckGameIter(IEnumerable<string> paths, HashGuessStrategy strategy = HashGuessStrategy.BinLengthPath) =>
                CheckIter(engine, paths, strategy, sourceWadPath, cancellationToken, sourceChunkHash);

            void CheckGameCandidates(IEnumerable<HashGuessCandidate> candidates) =>
                CheckIter(engine, candidates, sourceWadPath, cancellationToken, sourceChunkHash: sourceChunkHash);

            void CheckBinPath(string path)
            {
                if (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                    CheckGameCandidates(ExpandGrepFilePath(path, HashGuessStrategy.BinLengthPath));
                else CheckGame(path);
                if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                    CheckGame(path[..^4] + ".dds", HashGuessStrategy.ImageExtensionVariant);
            }

            if (sourcePath.Equals("data/all_lua_files.manifest", StringComparison.OrdinalIgnoreCase))
            {
                CheckGameCandidates(ExtractLuaManifestCandidates(data, cancellationToken));
                return;
            }

            if (ImageAutoAtlas.IsAtlas(data.AsSpan()))
            {
                GuessImageAutoAtlasPaths(engine, data, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (engine.RemainingUnknownCount == 0) return;

            string extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
            bool isPropertyBin = FileTypeDetector.IsPropertyBin(data.AsSpan());
            if (!isPropertyBin && !ShouldGrepExtension(extension))
            {
                return; // don't grep filetypes known to not contain full paths
            }

            bool isBin = isPropertyBin || extension is "bin" or "inibin";
            if (isBin)
            {
                string text = Encoding.Latin1.GetString(data.Array, data.Offset, data.Count);
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match match in Regex.Matches(text, @"(?:ASSETS|Common|DATA(?:_SOON)?|Characters|Shaders|Maps|Gameplay|ClientStates|Patching|Loadouts|Global|LEVELS|UX|UIAutoAtlas)[/\\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int offset = match.Index;
                    if (offset < 2) continue;
                    int length = ByteAt(data, offset - 2) | (ByteAt(data, offset - 1) << 8);
                    if (length <= 0 || offset + length > data.Count) continue;
                    string path = text.Substring(offset, length);
                    if (!IsAscii(path)) continue;
                    path = NormalizePath(path);
                    if (!seenPaths.Add(path)) continue;
                    CheckBinPath(path);
                    if (path.StartsWith("data_soon/", StringComparison.Ordinal))
                        CheckBinPath("data/" + path[10..]);

                    if (path.StartsWith("characters/", StringComparison.OrdinalIgnoreCase))
                    {
                        CheckBinPath($"assets/{path}");
                        CheckBinPath($"data/{path}");
                    }
                    else if (path.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("assets/shaders/", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("data/shaders/", StringComparison.OrdinalIgnoreCase))
                    {
                        string shaderExtension = ShaderExtensions.FirstOrDefault(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
                        string shaderBase = shaderExtension == null ? path : path[..^shaderExtension.Length];
                        var candidateBases = path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ||
                                             path.StartsWith("data/", StringComparison.OrdinalIgnoreCase)
                            ? new[] { shaderBase }
                            : new[] { $"assets/{shaderBase}", $"data/{shaderBase}", $"assets/shaders/generated/{shaderBase}" };
                        foreach (string candidateBase in candidateBases)
                        {
                            CheckGameIter(
                                ShaderExtensions.Select(extensionName =>
                                    $"{candidateBase}{extensionName}"));
                            if (engine.RemainingUnknownCount == 0) break;

                            CheckGameIter(
                                ShaderExtensions.SelectMany(extensionName =>
                                    ShaderVariants.Select(variant =>
                                        $"{candidateBase}{extensionName}{variant}")));
                            if (engine.RemainingUnknownCount == 0) break;

                            CheckGameIter(
                                ShaderExtensions.SelectMany(extensionName =>
                                    ShaderVariants.SelectMany(variant =>
                                        Enumerable.Range(0, ShaderBasePermutationCount).Select(index =>
                                            $"{candidateBase}{extensionName}{variant}_{index}"))));
                            if (engine.RemainingUnknownCount == 0) break;
                        }
                    }
                    else if (path.StartsWith("maps/mapgeometry/", StringComparison.OrdinalIgnoreCase))
                    {
                        string geometryBase = path.EndsWith(".materials.bin", StringComparison.OrdinalIgnoreCase)
                            ? path[..^14] : path.EndsWith(".mapgeo", StringComparison.OrdinalIgnoreCase) ? path[..^7] : path;
                        CheckGame($"data/{geometryBase}.mapgeo");
                        CheckGame($"data/{geometryBase}.materials.bin");
                        CheckGame($"data/{geometryBase}.navregions");
                        CheckGame($"data/{geometryBase}.navsdfinfo");
                    }
                    else if (path.StartsWith("clientstates/", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("patching/", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("loadouts/", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
                    {
                        int separator = path.LastIndexOf('/');
                        if (separator > 0)
                        {
                            string parent = path[..separator];
                            CheckGame(parent);
                            int parentSeparator = parent.LastIndexOf('/');
                            if (parentSeparator > 0) CheckGame(parent[..parentSeparator]);
                        }
                    }
                }

                GuessDottedBinPaths(engine, data, sourceWadPath, sourceChunkHash, cancellationToken);
                if (isPropertyBin)
                {
                    BinTree cachedTree = null;
                    bool binParseAttempted = false;
                    BinTree GetCachedBinTree()
                    {
                        if (!binParseAttempted)
                        {
                            binParseAttempted = true;
                            using var stream = new MemoryStream(data.Array, data.Offset, data.Count, writable: false);
                            cachedTree = new BinTree(stream);
                        }
                        return cachedTree;
                    }

                    GuessAnimationBinPaths(engine, data, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken, GetCachedBinTree);
                    GuessUiPortraitPaths(engine, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken, GetCachedBinTree);
                    GuessRegaliaBinChunkLinks(engine, data, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken, GetCachedBinTree);
                    GuessSkinRoleTextures(engine, data, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken, GetCachedBinTree);
                    GuessSkinCharacterBinChunkLinks(engine, data, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken, GetCachedBinTree);
                    BinTree materialTree = GetCachedBinTree();
                    if (GameMaterialTextureIndex.HasUnresolvedContext(engine, materialTree, sourcePath, cancellationToken))
                        GetMaterialTextureIndex(cancellationToken).Guess(engine, materialTree, sourcePath,
                            sourceWadPath, sourceChunkHash, cancellationToken);
                    GuessSiblingLinkPaths(engine, sourceWadPath, sourceChunkHash, cancellationToken, GetCachedBinTree);
                    BinTree linkTree = GetCachedBinTree();
                    if (linkTree != null)
                        _linkTemplates.GetValue(engine, static _ => new GameBinLinkTemplateIndex()).Guess(engine, linkTree, HashFile.Load(),
                            _resolveBinHash, _resolveBinHash64, sourceWadPath, sourceChunkHash, cancellationToken);
                }

                GuessChampionSpecialBins(engine, sourceWadPath, cancellationToken);
                return;
            }

            if (extension == "preload")
            {
                string text = Encoding.Latin1.GetString(data.Array, data.Offset, data.Count);
                string directory = PathUtils.NormalizeSeparators(Path.GetDirectoryName(sourcePath));

                foreach (Match match in Regex.Matches(text, @"\bName\s*=\s*(?:""([^""\r\n]+)""|'([^'\r\n]+)')"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                    if (!IsAscii(value)) continue;
                    string path = NormalizePath(value);
                    GuessNamespacedPreloadScripts(engine, path, sourceWadPath, sourceChunkHash, cancellationToken);
                    if (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                    {
                        string prefix = path[..^4];
                        CheckGame(path, HashGuessStrategy.PreloadReference);
                        CheckGame(prefix + ".luabin", HashGuessStrategy.LuaVariant);
                        CheckGame(prefix + ".luabin64", HashGuessStrategy.LuaVariant);
                    }
                    else if (path.EndsWith(".troy", StringComparison.OrdinalIgnoreCase))
                    {
                        CheckGame($"data/shared/particles/{path[..^5]}.troybin", HashGuessStrategy.PreloadReference);
                        if (path.StartsWith("data/", StringComparison.OrdinalIgnoreCase) || path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                            CheckGame(path[..^5] + ".troybin", HashGuessStrategy.PreloadReference);
                    }
                    else if (path.StartsWith("shaders", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string ext in ShaderExtensions)
                        {
                            CheckGame($"assets/shaders/generated/{path}{ext}", HashGuessStrategy.PreloadReference);
                            foreach (string variant in ShaderVariants)
                            {
                                CheckGame($"assets/shaders/generated/{path}{ext}{variant}", HashGuessStrategy.PreloadReference);
                            }
                        }
                    }
                    else if (!string.IsNullOrEmpty(directory))
                    {
                        string reference = path.EndsWith(".preload", StringComparison.OrdinalIgnoreCase) ? path : path + ".preload";
                        CheckGame(ResolveGrepReference(directory, reference), HashGuessStrategy.PreloadReference);
                    }
                }
                return;
            }

            if (extension is "hls" or "hlsl" or "ps_2_0" or "ps_3_0" or "vs_2_0" or "vs_3_0" or "ps" or "vs" or "cs")
            {
                string text = Encoding.Latin1.GetString(data.Array, data.Offset, data.Count);
                string directory = PathUtils.NormalizeSeparators(Path.GetDirectoryName(sourcePath));
                if (string.IsNullOrEmpty(directory)) return;
                foreach (Match match in Regex.Matches(text, @"#include ""([^""]+)"""))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string reference = match.Groups[1].Value;
                    if (!IsAscii(reference)) continue;
                    CheckGame(ResolveGrepReference(directory, reference), HashGuessStrategy.ShaderInclude);
                }
                return;
            }

            if (extension == "atlas")
            {
                string text = Encoding.Latin1.GetString(data.Array, data.Offset, data.Count);
                string directory = PathUtils.NormalizeSeparators(Path.GetDirectoryName(sourcePath));
                if (string.IsNullOrEmpty(directory)) return;
                foreach (string line in text.Split('\n'))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string reference = line.Trim();
                    if (reference.Length == 0 || !IsAscii(reference)) continue;
                    CheckGame(ResolveGrepReference(directory, reference), HashGuessStrategy.AtlasReference);
                }
                return;
            }

            CheckGameCandidates(GrepFile(data, cancellationToken));
        }

        private static string ResolveGrepReference(string directory, string reference)
        {
            string path = NormalizePath(reference);
            bool rooted = path.StartsWith('/') || path.StartsWith("assets/", StringComparison.Ordinal) || path.StartsWith("data/", StringComparison.Ordinal);
            return NormalizePath(PathUtils.NormalizeVirtualPath(rooted ? path.TrimStart('/') : $"{directory}/{path}"));
        }

        /// <summary>
        /// An unresolved texture link often lives next to its own name and to files in its folder
        /// (TFT shop data carries its item name and paths under the unit's images folder). Every
        /// sibling folder is combined with every sibling identifier of the same struct.
        /// </summary>
        private void GuessSiblingLinkPaths(
            HashGuessEngine engine,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken,
            Func<BinTree> binTreeFactory)
        {
            BinTree tree;
            try
            {
                tree = binTreeFactory();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return;
            }
            if (tree == null) return;
            IReadOnlyDictionary<ulong, string> knownPaths = null;

            foreach (BinTreeObject item in tree.Objects.Values)
                VisitScope(item.Properties.Values);
            foreach (var patch in tree.DataOverrides) VisitProperty(patch.Property);

            void VisitScope(IEnumerable<BinTreeProperty> properties)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool hasUnresolvedLink = false;
                foreach (BinTreeProperty property in properties)
                    if (property is BinTreeWadChunkLink link && link.Value != 0 && engine.UnknownHashes.Contains(link.Value))
                        hasUnresolvedLink = true;
                if (hasUnresolvedLink) GuessScope(properties);

                foreach (BinTreeProperty property in properties) VisitProperty(property);
            }

            void VisitProperty(BinTreeProperty property)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (property)
                {
                    case BinTreeStruct structure: VisitScope(structure.Properties.Values); break;
                    case BinTreeContainer container:
                        foreach (var child in container.Elements) VisitProperty(child);
                        break;
                    case BinTreeOptional { Value: not null } optional: VisitProperty(optional.Value); break;
                    case BinTreeMap map:
                        foreach (var pair in map) { VisitProperty(pair.Key); VisitProperty(pair.Value); }
                        break;
                }
            }

            void GuessScope(IEnumerable<BinTreeProperty> properties)
            {
                var folders = new HashSet<string>(StringComparer.Ordinal);
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (BinTreeProperty property in properties)
                {
                    string siblingPath = property switch
                    {
                        BinTreeString text => text.Value,
                        BinTreeWadChunkLink link when link.Value != 0 &&
                            (knownPaths ??= HashFile.Load()).TryGetValue(link.Value, out string path) => path,
                        _ => null
                    };
                    if (string.IsNullOrWhiteSpace(siblingPath)) continue;
                    string normalized = NormalizePath(siblingPath);
                    int slash = normalized.LastIndexOf('/');
                    if (slash > 0 && normalized.IndexOf('.', slash) > slash)
                    {
                        string folder = normalized[..slash];
                        folders.Add(folder);
                        // Unit art sits in the character's base images folder whatever the referencing path.
                        Match character = Regex.Match(folder, @"^assets/characters/[^/]+", RegexOptions.CultureInvariant);
                        if (character.Success) folders.Add($"{character.Value}/skins/base/images");
                    }
                    else if (property is BinTreeString && IsSiblingName(normalized))
                    {
                        names.Add(normalized);
                    }
                }

                foreach (string folder in folders)
                foreach (string name in names)
                {
                    Check(engine, $"{folder}/{name}.tex", HashGuessStrategy.BinLinkSibling, sourceWadPath, sourceChunkHash);
                    Check(engine, $"{folder}/{name}.dds", HashGuessStrategy.BinLinkSibling, sourceWadPath, sourceChunkHash);
                    if (engine.RemainingUnknownCount == 0) return;
                }
            }

            static bool IsSiblingName(string value) =>
                value.Length is >= 3 and <= 64 &&
                value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
        }

        private void GuessDottedBinPaths(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken)
        {
            if (data.Array is null || data.Count < 16) return;

            try
            {
                using var stream = new MemoryStream(data.Array, data.Offset, data.Count, writable: false);
                using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
                string magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
                if (magic == "PTCH")
                {
                    if (reader.ReadUInt32() != 1) return;
                    reader.ReadUInt32();
                    magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
                }
                if (magic != "PROP") return;

                uint version = reader.ReadUInt32();
                if (version is not (1 or 2 or 3)) return;
                if (version >= 2)
                {
                    uint dependencyCount = reader.ReadUInt32();
                    if (dependencyCount > data.Count / sizeof(ushort)) return;
                    for (uint index = 0; index < dependencyCount; index++)
                    {
                        ushort length = reader.ReadUInt16();
                        if (length > stream.Length - stream.Position) return;
                        stream.Position += length;
                    }
                }

                uint objectCount = reader.ReadUInt32();
                if (objectCount > data.Count / 10 || (long)objectCount * sizeof(uint) > stream.Length - stream.Position)
                    return;
                stream.Position += (long)objectCount * sizeof(uint);

                string[] dottedBinTargetPrefixes =
                {
                    "loadouts/companions",
                    "loadouts/summoneremotesvfx",
                    "loadouts/summoneremotes",
                    "loadouts/tftdamageskins",
                    "loadouts/tftzoomskins"
                };

                for (uint index = 0; index < objectCount; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long objectOffset = stream.Position;
                    uint objectSize = reader.ReadUInt32();
                    uint objectPathHash = reader.ReadUInt32();
                    long nextObjectOffset = objectOffset + sizeof(uint) + objectSize;
                    if (objectSize < 6 || nextObjectOffset < stream.Position || nextObjectOffset > stream.Length)
                        return;
                    foreach (string prefix in dottedBinTargetPrefixes)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Check(
                            engine,
                            $"{prefix}.{objectPathHash:x8}.bin",
                            HashGuessStrategy.BinEntry,
                            sourceWadPath,
                            sourceChunkHash);
                        if (engine.RemainingUnknownCount == 0) return;
                    }
                    stream.Position = nextObjectOffset;
                }
            }
            catch (Exception exception) when (exception is EndOfStreamException or IOException or ArgumentException)
            {
                _logService?.LogDebug($"GAME dotted BIN object scan skipped malformed data: {exception.Message}");
            }
        }

        // Basic Suite retains its sorted, unique candidate traversal and progress counts.
        private IReadOnlyList<string> GetDynamicLoadoutRegaliaPaths(CancellationToken cancellationToken) =>
            Corpus.GetOrCreate("dynamic-loadout-regalia-paths", _ =>
                EnumerateDynamicLoadoutRegaliaPaths(cancellationToken)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToList());

        private IEnumerable<string> EnumerateDynamicLoadoutRegaliaPaths(CancellationToken cancellationToken)
        {
            IReadOnlyList<string> knownPaths = KnownPaths;
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var queueTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "", "ranked_5s_", "ranked_solo_5s_", "ranked_flex_5s_", "ranked_3s_", "ranked_tft_", "arena_" };
            var tierTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var typeTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "", "_banner", "_crest", "_border", "_wings", "_flag", "_pedestal", "_badge" };
            var sizeTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "", "_512x512", "_256x256", "_1024x1024", "_128x128" };
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".tex", ".dds", ".png" };

            var regaliaRegex = new Regex(@"^(?<dir>(?:assets|data)/loadouts/regalia/[^/]+/)(?<file>[^.]+)(?<ext>\.[^.]+)$", RegexOptions.IgnoreCase);
            var trovesRegex = new Regex(@"^(?:plugins/rcp-be-lol-game-data/global/default/)?(?<dir>(?:assets|data)/ux/tft/troves_bannercontent/[^/]+/)(?<file>[^.]+)(?:\.[^./]+)?(?<ext>\.[^.]+)$", RegexOptions.IgnoreCase);

            for (int i = 0; i < knownPaths.Count; i++)
            {
                if ((i & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                string p = knownPaths[i];
                if (p.StartsWith("assets/loadouts/summoneremotes/", StringComparison.OrdinalIgnoreCase))
                    yield return Regex.Replace(p, @"_(?:inventory|glow)\.(?:tex|dds)$", "_selector.tex", RegexOptions.IgnoreCase);
                if (p.Contains("loadouts/regalia", StringComparison.OrdinalIgnoreCase))
                {
                    Match match = regaliaRegex.Match(p);
                    if (!match.Success) continue;

                    directories.Add(match.Groups["dir"].Value.ToLowerInvariant());
                    extensions.Add(match.Groups["ext"].Value.ToLowerInvariant());

                    string file = match.Groups["file"].Value.ToLowerInvariant();
                    string[] parts = file.Split('_', StringSplitOptions.RemoveEmptyEntries);
                    foreach (string part in parts)
                    {
                        if (part.Contains('x') && part.All(c => char.IsDigit(c) || c == 'x'))
                            sizeTokens.Add($"_{part}");
                        else if (part is "iron" or "bronze" or "silver" or "gold" or "platinum" or "emerald" or "diamond" or "master" or "grandmaster" or "challenger" or "unranked")
                            tierTokens.Add(part);
                    }
                }
                else if (p.Contains("troves_bannercontent", StringComparison.OrdinalIgnoreCase))
                {
                    Match match = trovesRegex.Match(p);
                    if (!match.Success) continue;

                    string dir = match.Groups["dir"].Value.ToLowerInvariant();
                    string file = match.Groups["file"].Value.ToLowerInvariant();

                    yield return $"{dir}{file}.tex";
                    yield return $"{dir}{file}.dds";
                    yield return $"{dir}{file}.png";
                }
                else if (p.Contains("loadouts/companions", StringComparison.OrdinalIgnoreCase) || p.Contains(".cutscene.bin", StringComparison.OrdinalIgnoreCase))
                {
                    string clean = p;
                    if (clean.StartsWith("plugins/rcp-be-lol-game-data/global/default/", StringComparison.OrdinalIgnoreCase))
                        clean = clean[44..];
                    if (clean.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                        clean = clean[7..];
                    if (clean.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                        clean = clean[5..];

                    yield return clean;
                    yield return $"data/{clean}";
                    yield return $"assets/{clean}";
                }
            }

            var cutscenePets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var petThemeTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base", "tier1" };
            for (int i = 0; i < knownPaths.Count; i++)
            {
                if ((i & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                string p = knownPaths[i];
                if (p.StartsWith("data/characters/pet", StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith("assets/characters/pet", StringComparison.OrdinalIgnoreCase))
                {
                    int slash1 = p.IndexOf('/', 16);
                    if (slash1 > 0)
                    {
                        string pet = p[16..slash1];
                        string cleanPet = pet.StartsWith("pet", StringComparison.OrdinalIgnoreCase) ? pet[3..] : pet;
                        cutscenePets.Add(cleanPet);
                        cutscenePets.Add(pet);
                    }
                }
                if (p.Contains("/themes/", StringComparison.OrdinalIgnoreCase))
                {
                    int themeIdx = p.IndexOf("/themes/", StringComparison.OrdinalIgnoreCase);
                    int nextSlash = p.IndexOf('/', themeIdx + 8);
                    if (nextSlash > 0)
                    {
                        petThemeTokens.Add(p.Substring(themeIdx + 8, nextSlash - (themeIdx + 8)).ToLowerInvariant());
                    }
                }
            }

            foreach (string pet in cutscenePets)
            foreach (string theme in petThemeTokens)
            {
                for (int tier = 1; tier <= 3; tier++)
                {
                    yield return $"loadouts/companions/{pet}_{theme}_{theme}_tier{tier}.cutscene.bin";
                    yield return $"loadouts/companions/{pet}_{theme}_tier{tier}.cutscene.bin";
                    yield return $"data/loadouts/companions/{pet}_{theme}_{theme}_tier{tier}.cutscene.bin";
                    yield return $"assets/loadouts/companions/{pet}_{theme}_{theme}_tier{tier}.cutscene.bin";
                }
            }

            var tftLoadoutStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < knownPaths.Count; i++)
            {
                if ((i & 0x3ff) == 0) cancellationToken.ThrowIfCancellationRequested();
                string p = knownPaths[i];
                if (p.Contains("tftdamageskins", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("tftzoomskins", StringComparison.OrdinalIgnoreCase))
                {
                    int lastSlash = p.LastIndexOf('/');
                    if (lastSlash < 0) continue;
                    string filename = p[(lastSlash + 1)..];
                    int dot = filename.IndexOf('.');
                    string stem = dot > 0 ? filename[..dot] : filename;
                    stem = stem.Replace("_tier1", "", StringComparison.OrdinalIgnoreCase)
                               .Replace("_tier2", "", StringComparison.OrdinalIgnoreCase)
                               .Replace("_tier3", "", StringComparison.OrdinalIgnoreCase)
                               .Replace("_small", "", StringComparison.OrdinalIgnoreCase)
                               .Replace("boom_", "", StringComparison.OrdinalIgnoreCase);
                    if (stem.Length > 0 && !stem.All(char.IsDigit))
                        tftLoadoutStems.Add(stem.ToLowerInvariant());
                }
            }

            string[] tftRootDirs = { "loadouts/", "global/loadouts/", "data/loadouts/", "assets/loadouts/" };
            string[] tftTemplates =
            {
                "{0}.vfxdefinition.bin",
                "{0}.resourcebin.bin",
                "{0}_tier1.resourcebin.bin",
                "{0}_tier2.resourcebin.bin",
                "{0}_tier3.resourcebin.bin",
                "{0}_tier1.cutscene.bin",
                "{0}_tier2.cutscene.bin",
                "{0}_tier3.cutscene.bin",
                "{0}.cutscene.bin"
            };

            foreach (string stem in tftLoadoutStems)
            {
                foreach (string root in tftRootDirs)
                {
                    foreach (string tpl in tftTemplates)
                    {
                        yield return $"{root}tftdamageskins/{string.Format(CultureInfo.InvariantCulture, tpl, stem)}";
                        yield return $"{root}tftzoomskins/{string.Format(CultureInfo.InvariantCulture, tpl, stem)}";
                    }
                }
            }

            foreach (string dir in directories)
            foreach (string queue in queueTokens)
            foreach (string tier in tierTokens)
            foreach (string type in typeTokens)
            foreach (string size in sizeTokens)
            foreach (string ext in extensions)
            {
                string candidate = $"{dir}{queue}{tier}{type}{size}{ext}".ToLowerInvariant().Replace("__", "_");
                yield return candidate;
            }
        }

        private static readonly Regex SkinPathRegex = new(
            @"characters/(?<champ>[^/]+)/(?:(?:skins|themes)/(?<skin>base|(?:skin|theme)0*(?<num>\d+))|themes/(?<skin>[^/]+))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MaterialPathRegex = new(
            @"characters/(?<champ>[^/]+)/(?<folder>skins|themes)/(?<skin>[^/]+)(?:/materials)?/(?<mat>[^/]+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
						


        private static readonly string[] CanonicalMaterialRoles =
        {
            "", "_mask", "_scroll", "_scrollmask", "_flowmap", "_matcap"
        };

        private void GuessRegaliaBinChunkLinks(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken,
            Func<BinTree> binTreeFactory = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Array is null || data.Count == 0) return;

            if (!sourcePath.Contains("regalia", StringComparison.OrdinalIgnoreCase) &&
                !sourcePath.Contains("loadouts", StringComparison.OrdinalIgnoreCase) &&
                !sourcePath.Contains("troves", StringComparison.OrdinalIgnoreCase) &&
                !sourcePath.Contains("companions", StringComparison.OrdinalIgnoreCase) &&
                !sourceWadPath.Contains("global", StringComparison.OrdinalIgnoreCase) &&
                !sourceWadPath.Contains("companion", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var unresolved = new HashSet<ulong>();
            try
            {
                BinTree tree = binTreeFactory != null
                    ? binTreeFactory()
                    : new BinTree(new MemoryStream(data.Array, data.Offset, data.Count, writable: false));
                if (tree == null) return;
                cancellationToken.ThrowIfCancellationRequested();
                foreach (ulong link in EnumerateChunkLinks(tree))
                {
                    if (engine.UnknownHashes.Contains(link))
                        unresolved.Add(link);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService?.LogDebug($"GAME regalia BIN link scan skipped '{sourcePath}': {exception.Message}");
                return;
            }

            if (unresolved.Count == 0) return;

            // Cache only paths that match this run's targets, not the entire Cartesian product.
            RegaliaMatchIndex index = _regaliaMatches.GetValue(engine, _ =>
                BuildRegaliaMatchIndex(engine.UnknownHashes, cancellationToken));
            var addedTargets = unresolved.Where(hash => !index.Targets.Contains(hash)).ToHashSet();
            if (addedTargets.Count > 0)
            {
                // Atlases can introduce unknown sprite hashes after the initial target snapshot.
                RegaliaMatchIndex additional = BuildRegaliaMatchIndex(addedTargets, cancellationToken);
                index.Targets.UnionWith(additional.Targets);
                foreach (var pair in additional.Paths) index.Paths.Add(pair.Key, pair.Value);
            }
            foreach (string path in unresolved.Where(index.Paths.ContainsKey).Select(hash => index.Paths[hash])
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (engine.RemainingUnknownCount == 0) return;
                Check(engine, path, HashGuessStrategy.BannerVariant, sourceWadPath, sourceChunkHash);
            }
        }

        private RegaliaMatchIndex BuildRegaliaMatchIndex(IEnumerable<ulong> targetHashes, CancellationToken cancellationToken)
        {
            var targets = targetHashes.ToHashSet();
            var paths = new Dictionary<ulong, string>();
            foreach (string path in EnumerateDynamicLoadoutRegaliaPaths(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong hash = XxHash64Ext.Hash(path);
                if (!targets.Contains(hash)) continue;
                // The old full index retained the first path in sorted order, even on a hash collision.
                if (!paths.TryGetValue(hash, out string previous) || StringComparer.OrdinalIgnoreCase.Compare(path, previous) < 0)
                    paths[hash] = path;
            }
            return new RegaliaMatchIndex(targets, paths);
        }

        private static void HarvestSubmeshTokens(string text, HashSet<string> smSet)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (string sm in text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string smLow = sm.ToLowerInvariant();
                if (smLow.Length >= 1)
                {
                    smSet.Add(smLow);
                    string[] parts = smLow.Split('_', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1)
                    {
                        for (int i = 1; i < parts.Length; i++)
                        {
                            smSet.Add(string.Join('_', parts.Take(i)));
                            smSet.Add(string.Join('_', parts.Skip(i)));
                        }
                        foreach (string part in parts)
                        {
                            if (part != "top" && part != "low" && part != "bot")
                                smSet.Add(part);
                        }
                    }
                }
            }
        }


        private static void HarvestSubmeshRoleTokens(string submeshName, HashSet<string> roles)
        {
            if (string.IsNullOrWhiteSpace(submeshName)) return;
            string clean = CleanRoleName(submeshName);
            if (string.IsNullOrEmpty(clean)) return;

            roles.Add(clean);

            string[] parts = clean.Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1)
            {
                foreach (string part in parts)
                {
                    if (!string.IsNullOrWhiteSpace(part))
                    {
                        roles.Add(part);
                    }
                }

                for (int i = 1; i < parts.Length; i++)
                {
                    roles.Add(string.Join('_', parts.Skip(i)));
                    roles.Add(string.Join('_', parts.Take(i)));
                }
            }
        }

        private static void HarvestRoleTokens(string text, HashSet<string> descriptors, HashSet<string> samplerStems = null)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Contains('/')) return;

            string clean = text.Trim();
            if (clean.EndsWith("Texture", StringComparison.OrdinalIgnoreCase)) clean = clean[..^7];
            else if (clean.EndsWith("Tex", StringComparison.OrdinalIgnoreCase)) clean = clean[..^3];
            else if (clean.EndsWith("Map", StringComparison.OrdinalIgnoreCase)) clean = clean[..^3];
            clean = clean.TrimEnd('_');

            string low = clean.ToLowerInvariant();
            if (low.Length >= 3)
            {
                descriptors.Add("_" + low);
                samplerStems?.Add(low);

                string[] parts = low.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    descriptors.Add("_" + string.Join("", parts));
                    descriptors.Add("_" + string.Join('_', parts));
                    samplerStems?.Add(string.Join("", parts));
                    samplerStems?.Add(string.Join('_', parts));
                    foreach (string part in parts)
                    {
                        if (part.Length >= 3 && part != "texture")
                        {
                            descriptors.Add("_" + part);
                            samplerStems?.Add(part);
                            if (part.EndsWith("scroll", StringComparison.Ordinal))
                            {
                                descriptors.Add("_scrollmask");
                                samplerStems?.Add("scrollmask");
                            }
                        }
                    }
                }
            }
        }

        // Built compositionally (map kind x variant x extension) instead of hardcoding every
        // ending; both consumers below are per-unknown gated loops, so extra combinations
        // only cost hash comparisons and can catch future variants with zero false positives.
        private static readonly string[] TextureMapKinds =
        {
            "_cm", "_tx_cm", "_tx", "_base_tx_cm", "_tx_gm", "_tx_rm", "_cm_tx", "_d", "_tx_cm2",
            "_tx_cm_2", "_flowmap", "_tx_flowmap", "_cubemap", "_base_cubemap", "_noise",
            "_diffuse", "_mult", "_base_cm_tx", "_base_tx", ""
        };
        private static readonly string[] TextureMapVariants = { "", ".project_jade" };
        private static readonly string[] TextureMapExtensions = { ".tex", ".dds" };
        private static readonly string[] TextureMapSuffixes = BuildTextureMapSuffixes();

        private static string[] BuildTextureMapSuffixes()
        {
            return (from kind in TextureMapKinds
                    from variant in TextureMapVariants
                    from extension in TextureMapExtensions
                    select kind + variant + extension).ToArray();
        }

        private void GuessSkinCharacterBinChunkLinks(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken,
            Func<BinTree> binTreeFactory = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Array is null || data.Count == 0 || engine.RemainingUnknownCount == 0) return;

            BinTree tree;
            try
            {
                tree = binTreeFactory != null
                    ? binTreeFactory()
                    : new BinTree(new MemoryStream(data.Array, data.Offset, data.Count, writable: false));
                if (tree == null) return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService?.LogDebug($"GAME skin character BIN link scan skipped '{sourcePath}': {exception.Message}");
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            void CollectChunkLinks(BinTreeProperty prop, HashSet<ulong> hashes)
            {
                switch (prop)
                {
                    case BinTreeWadChunkLink link:
                        if (link.Value != 0 && engine.UnknownHashes.Contains(link.Value))
                            hashes.Add(link.Value);
                        break;
                    case BinTreeStruct str:
                        foreach (BinTreeProperty p in str.Properties.Values)
                            CollectChunkLinks(p, hashes);
                        break;
                    case BinTreeContainer container:
                        foreach (BinTreeProperty p in container.Elements)
                            CollectChunkLinks(p, hashes);
                        break;
                    case BinTreeMap map:
                        foreach (var kv in map)
                        {
                            CollectChunkLinks(kv.Key, hashes);
                            CollectChunkLinks(kv.Value, hashes);
                        }
                        break;
                    case BinTreeOptional opt:
                        if (opt.Value != null)
                            CollectChunkLinks(opt.Value, hashes);
                        break;
                }
            }

            string ResolveMeshPath(BinTreeProperty property)
            {
                if (property is BinTreeString text) return text.Value;
                if (property is not BinTreeWadChunkLink link || link.Value == 0) return null;
                if (engine.Matches.TryGetValue(link.Value, out var match)) return match.Path;
                // Hashed mesh references still provide naming context when their paths are known.
                var knownPaths = HashFile.Load();
                return knownPaths.TryGetValue(link.Value, out string path) ? path : null;
            }

            var submeshesBySkin = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var skinObjects = new List<(BinTreeObject Obj, string SimpleSkin, string Skeleton, HashSet<ulong> Links)>();
            var materialObjects = new List<(string Champ, string Folder, string Skin, string RawMat, List<(ulong Hash, HashSet<string> Descriptors, HashSet<string> Stems)> Targets)>();
            int totalUnresolved = 0;

            // Phase 1: Fast single-pass metadata harvest across all BIN objects
            foreach (BinTreeObject obj in tree.Objects.Values)
            {
                if (obj.ClassHash == 0x9b67e9f6) // SkinCharacterDataProperties
                {
                    var targetTexHashes = new HashSet<ulong>();
                    string simpleSkin = null;
                    string skeleton = null;

                    if ((obj.Properties.TryGetValue(0x45ff5904, out BinTreeProperty meshProp) ||
                         obj.Properties.TryGetValue(0x5337242d, out meshProp)) &&
                        meshProp is BinTreeStruct meshStruct)
                    {
                        CollectChunkLinks(meshStruct, targetTexHashes);
                        if (meshStruct.Properties.TryGetValue(0xd6a00df6, out BinTreeProperty skinProp))
                        {
                            simpleSkin = ResolveMeshPath(skinProp);
                        }
                        if (meshStruct.Properties.TryGetValue(0xb14c976e, out BinTreeProperty skelProp))
                        {
                            skeleton = ResolveMeshPath(skelProp);
                        }

                        string refSkin = !string.IsNullOrEmpty(simpleSkin) ? simpleSkin : skeleton;
                        if (!string.IsNullOrEmpty(refSkin))
                        {
                            Match m = SkinPathRegex.Match(refSkin.Replace('\\', '/'));
                            if (m.Success)
                            {
                                if (obj.Properties.TryGetValue(Fnv1a.HashLower("championSkinName"), out var skinNameProperty) &&
                                    skinNameProperty is BinTreeString skinName && targetTexHashes.Any(engine.UnknownHashes.Contains))
                                {
                                    foreach (string candidate in EnumerateArenaPropLabelTextures(refSkin, skinName.Value))
                                    {
                                        cancellationToken.ThrowIfCancellationRequested();
                                        if (targetTexHashes.Contains(XxHash64Ext.Hash(candidate)))
                                            Check(engine, candidate, HashGuessStrategy.BinLengthPath, sourceWadPath, sourceChunkHash);
                                    }
                                }
                                string sKey = m.Groups["skin"].Value.ToLowerInvariant();
                                if (!submeshesBySkin.TryGetValue(sKey, out var smSet))
                                    submeshesBySkin[sKey] = smSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                                // Harvest submeshes from strings and override containers (submeshRenderOrder, materialOverride, etc.)
                                foreach (BinTreeProperty p in meshStruct.Properties.Values)
                                {
                                    if (p is BinTreeString sVal)
                                    {
                                        HarvestSubmeshTokens(sVal.Value, smSet);
                                    }
                                    else if (p is BinTreeContainer container)
                                    {
                                        foreach (BinTreeProperty elem in container.Elements)
                                        {
                                            if (elem is BinTreeStruct elemStruct)
                                            {
                                                foreach (BinTreeProperty ep in elemStruct.Properties.Values)
                                                {
                                                    if (ep is BinTreeString subStr)
                                                        HarvestSubmeshTokens(subStr.Value, smSet);
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }

                    foreach (BinTreeProperty p in obj.Properties.Values)
                        CollectChunkLinks(p, targetTexHashes);

                    targetTexHashes.RemoveWhere(hash => !engine.UnknownHashes.Contains(hash));

                    if (targetTexHashes.Count > 0)
                    {
                        skinObjects.Add((obj, simpleSkin, skeleton, targetTexHashes));
                        totalUnresolved += targetTexHashes.Count;
                    }
                }
                else if (obj.ClassHash == 0xff9d3409) // StaticMaterialDef
                {
                    string materialName = obj.Properties.TryGetValue(0x8d39bde6, out BinTreeProperty pMat)
                        ? pMat switch
                        {
                            BinTreeString text => text.Value,
                            BinTreeHash64 name => _resolveBinHash64?.Invoke(name.Value),
                            _ => null
                        }
                        : null;
                    Match matMatch = MaterialPathRegex.Match(materialName?.Replace('\\', '/') ?? string.Empty);
                    // Modern material names are XXH3 links; the entry still identifies the same material.
                    if (!matMatch.Success)
                        matMatch = MaterialPathRegex.Match(_resolveBinHash?.Invoke(obj.PathHash)?.Replace('\\', '/') ?? string.Empty);
                    if (matMatch.Success)
                    {
                        var matLinks = new HashSet<ulong>();
                        foreach (BinTreeProperty property in obj.Properties.Values)
                            CollectChunkLinks(property, matLinks);
                        matLinks.RemoveWhere(hash => !engine.UnknownHashes.Contains(hash));

                        var descriptors = new HashSet<string>(CanonicalMaterialRoles, StringComparer.OrdinalIgnoreCase);
                        var stemsByTarget = new Dictionary<ulong, HashSet<string>>();
                        foreach (BinTreeContainer container in obj.Properties.Values.OfType<BinTreeContainer>())
                        foreach (BinTreeStruct sampler in container.Elements.OfType<BinTreeStruct>())
                        {
                            var samplerStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (BinTreeString value in sampler.Properties.Values.OfType<BinTreeString>())
                                HarvestRoleTokens(value.Value, descriptors, samplerStems);

                            if (sampler.Properties.TryGetValue(TexturePathHash, out BinTreeProperty pathProperty) &&
                                pathProperty is BinTreeWadChunkLink link && matLinks.Contains(link.Value))
                            {
                                if (!stemsByTarget.TryGetValue(link.Value, out HashSet<string> targetStems))
                                    stemsByTarget[link.Value] = targetStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                targetStems.UnionWith(samplerStems);
                            }
                        }

                        var targets = matLinks
                            .Select(hash => (hash, descriptors,
                                stemsByTarget.TryGetValue(hash, out HashSet<string> stems)
                                    ? stems
                                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                            .ToList();

                        if (targets.Count > 0)
                        {
                            string mChamp = matMatch.Groups["champ"].Value.ToLowerInvariant();
                            string mFolder = matMatch.Groups["folder"].Value.ToLowerInvariant();
                            string mSkin = matMatch.Groups["skin"].Value.ToLowerInvariant();
                            string rawMat = matMatch.Groups["mat"].Value.ToLowerInvariant();
                            if (rawMat.EndsWith("_inst", StringComparison.OrdinalIgnoreCase)) rawMat = rawMat[..^5];
                            if (rawMat.EndsWith("_mat", StringComparison.OrdinalIgnoreCase)) rawMat = rawMat[..^4];
                            if (rawMat.StartsWith(mChamp + "_" + mSkin + "_", StringComparison.OrdinalIgnoreCase))
                                rawMat = rawMat[(mChamp.Length + mSkin.Length + 2)..];
                            else if (rawMat.StartsWith(mChamp + "_", StringComparison.OrdinalIgnoreCase))
                                rawMat = rawMat[(mChamp.Length + 1)..];

                            materialObjects.Add((mChamp, mFolder, mSkin, rawMat, targets));
                            totalUnresolved += targets.Count;
                        }
                    }
                }
            }

            if (totalUnresolved == 0 || engine.RemainingUnknownCount == 0) return;

            // Phase 2: Resolve Modern Skin Materials (StaticMaterialDef)
            foreach (var (mChamp, mFolder, mSkin, rawMat, targets) in materialObjects)
            {
                if (engine.RemainingUnknownCount == 0) break;

                string baseDir = $"assets/characters/{mChamp}/{mFolder}/{mSkin}/";
                Check(engine, $"{baseDir}{mChamp}_{mSkin}_tx_cm.tex", HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                Check(engine, $"{baseDir}{mChamp}_{mSkin}_mask_tx_cm.tex", HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                Check(engine, $"{baseDir}{mChamp}_{mSkin}_matcap.tex", HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);

                string championSkinPrefix = $"{baseDir}{mChamp}_{mSkin}_";
                string championPrefix = $"{baseDir}{mChamp}_";
                var materialPaths = new MaterialTexturePathMatcher(championSkinPrefix, championPrefix, baseDir);
                foreach (var (targetHash, matDescriptors, samplerStems) in targets)
                {
                    if (!engine.UnknownHashes.Contains(targetHash)) continue;

                    var baseStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rawMat };
                    string[] rawParts = rawMat.Split('_', StringSplitOptions.RemoveEmptyEntries);
                    if (rawParts.Length > 1)
                    {
                        foreach (string part in rawParts)
                        {
                            if (part.Length < 3 || part is "matcap" or "inst" or "mat" or "specular" or "fresnel") continue;
                            baseStems.Add(part);
                            if (part.EndsWith("ed", StringComparison.OrdinalIgnoreCase) && part.Length > 4)
                                baseStems.Add(part[..^2]);
                        }

                        string[] meaningful = rawParts
                            .Where(part => part.Length >= 3 && part is not ("matcap" or "inst" or "mat" or "specular" or "fresnel"))
                            .Select(part => part.EndsWith("ed", StringComparison.OrdinalIgnoreCase) && part.Length > 4 ? part[..^2] : part)
                            .ToArray();
                        if (meaningful.Length > 1)
                        {
                            baseStems.Add(string.Join("", meaningful));
                            baseStems.Add(string.Join('_', meaningful));
                        }
                    }

                    baseStems.UnionWith(samplerStems);
                    foreach (string samplerStem in samplerStems)
                    {
                        baseStems.Add(rawMat + samplerStem);
                        baseStems.Add(rawMat + "_" + samplerStem);
                        foreach (string part in rawParts)
                        {
                            if (part.Length < 3 || part is "matcap" or "inst" or "mat") continue;
                            baseStems.Add(part + samplerStem);
                            baseStems.Add(part + "_" + samplerStem);
                        }
                    }
                    foreach (string baseStem in baseStems)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string root = baseStem.EndsWith("_f1", StringComparison.OrdinalIgnoreCase) ? baseStem[..^3] : baseStem;
                        foreach (string stem in new[] { root, root + "_f1", root + "_f2", root + "_f3", root + "_f4", root + "_f1_f2", root + "_f2_f3", root + "_empowered", root + "_reset", "empowered_" + root })
                        {
                            if (materialPaths.TryMatch(stem, matDescriptors, targetHash, out string candidate))
                            {
                                Check(engine, candidate, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                                goto NextTarget;
                            }
                        }
                    }

                    NextTarget:;
                }

            }

            // Phase 3: Resolve Skin Mesh Links (SkinCharacterDataProperties)
            foreach (var (obj, simpleSkin, skeleton, targetTexHashes) in skinObjects)
            {
                targetTexHashes.RemoveWhere(hash => !engine.UnknownHashes.Contains(hash));
                if (engine.RemainingUnknownCount == 0) break;
                if (targetTexHashes.Count == 0) continue;

                string refPath = !string.IsNullOrEmpty(simpleSkin) ? simpleSkin : skeleton;
                if (string.IsNullOrEmpty(refPath)) continue;

                refPath = refPath.Replace('\\', '/').ToLowerInvariant();
                int lastSlash = refPath.LastIndexOf('/');
                string dir = lastSlash >= 0 ? refPath[..(lastSlash + 1)] : "";
                if (dir.EndsWith("/rig/", StringComparison.OrdinalIgnoreCase)) dir = dir[..^4];

                string fileStem = lastSlash >= 0 ? refPath[(lastSlash + 1)..] : refPath;
                int dot = fileStem.IndexOf('.');
                if (dot >= 0) fileStem = fileStem[..dot];

                Match champMatch = SkinPathRegex.Match(dir);
                string champ = champMatch.Success ? champMatch.Groups["champ"].Value.ToLowerInvariant() : "";
                string cleanChamp = champ.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ? champ[5..] : champ;

                var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { dir };
                if (dir.Contains("/jade_", StringComparison.OrdinalIgnoreCase))
                    candidateDirs.Add(dir.Replace("/jade_", "/", StringComparison.OrdinalIgnoreCase));
                if (dir.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                    candidateDirs.Add("data/" + dir[7..]);
                else if (dir.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                    candidateDirs.Add("assets/" + dir[5..]);
                else
                {
                    candidateDirs.Add("assets/" + dir);
                    candidateDirs.Add("data/" + dir);
                }

                // Companion models can live in TierN while their textures belong to the theme root.
                bool isThemeMesh = dir.Contains("/themes/", StringComparison.Ordinal);
                if (isThemeMesh)
                    foreach (string candidateDir in candidateDirs.ToArray())
                    {
                        Match themeDirectory = Regex.Match(candidateDir, @"/themes/[^/]+/", RegexOptions.CultureInvariant);
                        if (themeDirectory.Success) candidateDirs.Add(candidateDir[..(themeDirectory.Index + themeDirectory.Length)]);
                    }

                Match skinFolderMatch = Regex.Match(dir, @"/skins/skin30*(?<num>\d+)/", RegexOptions.IgnoreCase);
                if (skinFolderMatch.Success)
                {
                    string num = skinFolderMatch.Groups["num"].Value;
                    string skinPad = num.Length == 1 ? $"skin0{num}" : $"skin{num}";
                    string skinRaw = $"skin{num}";
                    var baseDirs = candidateDirs.ToList();
                    foreach (string bd in baseDirs)
                    {
                        candidateDirs.Add(Regex.Replace(bd, @"/skins/skin30*\d+/", $"/skins/{skinPad}/", RegexOptions.IgnoreCase));
                        candidateDirs.Add(Regex.Replace(bd, @"/skins/skin30*\d+/", $"/skins/{skinRaw}/", RegexOptions.IgnoreCase));
                    }
                }

                var candidateStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { fileStem };
                var themeTextureStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (isThemeMesh)
                {
                    string stem = Regex.Replace(fileStem, @"_tier[0-9]+$", "", RegexOptions.CultureInvariant);
                    candidateStems.Add(stem);
                    themeTextureStems.Add(stem);
                }
                if (fileStem.StartsWith("jade_", StringComparison.OrdinalIgnoreCase))
                    candidateStems.Add(fileStem[5..]);
                else
                    candidateStems.Add("jade_" + fileStem);

                Match sourceSkinMatch = SkinBinFileRegex.Match(PathUtils.NormalizePath(sourcePath));
                if (sourceSkinMatch.Success)
                {
                    string binChamp = sourceSkinMatch.Groups["champ"].Value.ToLowerInvariant();
                    string binSkin = sourceSkinMatch.Groups["skin"].Value.ToLowerInvariant();
                    candidateDirs.Add($"assets/characters/{binChamp}/skins/{binSkin}/");
                    candidateDirs.Add($"data/characters/{binChamp}/skins/{binSkin}/");
                    if (binSkin.Equals("skin0", StringComparison.OrdinalIgnoreCase))
                    {
                        candidateDirs.Add($"assets/characters/{binChamp}/skins/base/");
                        candidateDirs.Add($"data/characters/{binChamp}/skins/base/");
                    }

                    candidateStems.Add($"{binChamp}_{binSkin}");
                    candidateStems.Add($"{binChamp}_{binSkin}_body");
                    if (binSkin.Equals("skin0", StringComparison.OrdinalIgnoreCase))
                    {
                        candidateStems.Add($"{binChamp}_base");
                        candidateStems.Add($"{binChamp}_base_body");
                    }

                    if (binChamp.StartsWith("jade_", StringComparison.OrdinalIgnoreCase))
                    {
                        string unjaded = binChamp[5..];
                        candidateDirs.Add($"assets/characters/{unjaded}/skins/{binSkin}/");
                        candidateDirs.Add($"data/characters/{unjaded}/skins/{binSkin}/");
                        if (binSkin.Equals("skin0", StringComparison.OrdinalIgnoreCase))
                        {
                            candidateDirs.Add($"assets/characters/{unjaded}/skins/base/");
                            candidateDirs.Add($"data/characters/{unjaded}/skins/base/");
                        }
                        candidateStems.Add($"{unjaded}_{binSkin}");
                        candidateStems.Add($"{unjaded}_{binSkin}_body");
                        if (binSkin.Equals("skin0", StringComparison.OrdinalIgnoreCase))
                        {
                            candidateStems.Add($"{unjaded}_base");
                            candidateStems.Add($"{unjaded}_base_body");
                        }
                    }

                    if (binChamp.EndsWith("shark", StringComparison.OrdinalIgnoreCase))
                    {
                        string sharkSeparated = binChamp.Replace("shark", "_shark");
                        candidateStems.Add($"{sharkSeparated}_{binSkin}");
                        candidateStems.Add($"{sharkSeparated}_{binSkin}_body");
                        if (binSkin.Equals("skin0", StringComparison.OrdinalIgnoreCase))
                        {
                            candidateStems.Add($"{sharkSeparated}_base");
                            candidateStems.Add($"{sharkSeparated}_base_body");
                        }
                        if (sharkSeparated.StartsWith("jade_", StringComparison.OrdinalIgnoreCase))
                        {
                            string unjadedShark = sharkSeparated[5..];
                            candidateStems.Add($"{unjadedShark}_{binSkin}");
                            candidateStems.Add($"{unjadedShark}_{binSkin}_body");
                            if (binSkin.Equals("skin0", StringComparison.OrdinalIgnoreCase))
                            {
                                candidateStems.Add($"{unjadedShark}_base");
                                candidateStems.Add($"{unjadedShark}_base_body");
                            }
                        }
                    }

                    if (submeshesBySkin.TryGetValue(binSkin, out var binSmList))
                    {
                        foreach (string sm in binSmList)
                        {
                            candidateStems.Add($"{binChamp}_{binSkin}_{sm}");
                            candidateStems.Add($"{binChamp}_{sm}");
                        }
                    }
                }

                var champAliases = new List<string>();
                if (!string.IsNullOrEmpty(cleanChamp))
                {
                    champAliases.Add(cleanChamp);
                    if (cleanChamp.EndsWith("shark", StringComparison.OrdinalIgnoreCase))
                        champAliases.Add(cleanChamp.Replace("shark", "_shark"));
                    if (cleanChamp == "xinzhao")
                    {
                        champAliases.Add("xenzhao");
                        champAliases.Add("xinzhaorework");
                    }
                    else if (cleanChamp == "orianna")
                    {
                        champAliases.Add("oriana");
                    }

                    foreach (string ca in champAliases)
                    {
                        candidateStems.Add(ca);
                        candidateStems.Add($"{ca}_tx");
                    }
                }

                string skinName = null;
                if (obj.Properties.TryGetValue(0x2d78c328, out BinTreeProperty nameProp) &&
                    nameProp is BinTreeString nameStr)
                {
                    skinName = nameStr.Value;
                }

                if (!string.IsNullOrEmpty(skinName))
                {
                    string cleanName = skinName.ToLowerInvariant();
                    candidateStems.Add(cleanName);
                    if (isThemeMesh)
                    {
                        string stem = Regex.Replace(cleanName, @"_tier[0-9]+$", "", RegexOptions.CultureInvariant);
                        candidateStems.Add(stem);
                        themeTextureStems.Add(stem);
                    }
                    candidateStems.Add(cleanName.Replace(' ', '_'));
                    candidateStems.Add(cleanName.Replace(" ", ""));
                    string[] parts = cleanName.Split(new[] { '_', ' ', '-' }, StringSplitOptions.RemoveEmptyEntries);
                    var meaningfulTokens = parts.Where(p => p != "jade" && p != "classic" && p.Length >= 3).ToList();
                    foreach (string t in meaningfulTokens)
                    {
                        candidateStems.Add(t);
                        foreach (string ca in champAliases)
                        {
                            candidateStems.Add($"{ca}_{t}");
                            candidateStems.Add($"jade_{ca}_{t}");
                            candidateStems.Add($"{t}_{ca}");
                            candidateStems.Add($"{t}_jade_{ca}");
                        }
                    }
                    if (parts.Length >= 2)
                    {
                        candidateStems.Add($"{parts[0]}_{parts[^1]}");
                        candidateStems.Add($"{parts[^1]}_{parts[0]}");
                    }
                }

                string[] stemParts = fileStem.Split('_', StringSplitOptions.RemoveEmptyEntries);
                var stemTokens = stemParts.Where(p => p != "jade" && p != "classic" && p != "rg" && p != cleanChamp && p.Length >= 3).ToList();
                foreach (string st in stemTokens)
                {
                    candidateStems.Add(st);
                    foreach (string ca in champAliases)
                    {
                        candidateStems.Add($"{ca}_{st}");
                        candidateStems.Add($"jade_{ca}_{st}");
                        candidateStems.Add($"{st}_{ca}");
                        candidateStems.Add($"{st}_jade_{ca}");
                    }
                }

                string sKey = champMatch.Success ? champMatch.Groups["skin"].Value.ToLowerInvariant() : "";
                if (!string.IsNullOrEmpty(sKey) && submeshesBySkin.TryGetValue(sKey, out var smList))
                {
                    foreach (string sm in smList)
                    {
                        candidateStems.Add(sm);
                        candidateStems.Add($"{fileStem}_{sm}");
                        foreach (string stem in themeTextureStems) candidateStems.Add($"{stem}_{sm}");
                        if (!string.IsNullOrEmpty(cleanChamp))
                            candidateStems.Add($"{cleanChamp}_{sm}");
                    }
                }

                var allStems = candidateStems.ToList();
                foreach (string s in allStems)
                {
                    candidateStems.Add(s + "_base");
                    candidateStems.Add(s + "_tx");
                    candidateStems.Add(s + "_base_tx");
                    candidateStems.Add("2x_" + s);
                    candidateStems.Add("4x_" + s);
                    if (s.Contains("royalguard", StringComparison.OrdinalIgnoreCase) || s.Contains("royal_guard", StringComparison.OrdinalIgnoreCase))
                    {
                        candidateStems.Add("fiora_musketeer");
                        candidateStems.Add("fiora_musketeer_tx");
                    }
                    if (s.Contains("nightraven", StringComparison.OrdinalIgnoreCase) || s.Contains("night_raven", StringComparison.OrdinalIgnoreCase))
                    {
                        candidateStems.Add("fiora_zorro");
                        candidateStems.Add("fiora_zorro_tx");
                    }
                    if (s.Contains("hextech", StringComparison.OrdinalIgnoreCase))
                    {
                        candidateStems.Add("galio_hextech");
                        candidateStems.Add("galio_hextech_tx");
                    }
                    if (s.Contains("lumberjack", StringComparison.OrdinalIgnoreCase))
                    {
                        candidateStems.Add("sion_lumberjack");
                        candidateStems.Add("sion_lumberjack_tx");
                    }
                }

                foreach (string cDir in candidateDirs)
                {
                    if (targetTexHashes.Count == 0 || engine.RemainingUnknownCount == 0) break;
                    foreach (string cStem in candidateStems)
                    {
                        if (targetTexHashes.Count == 0 || engine.RemainingUnknownCount == 0) break;
                        foreach (string suf in TextureMapSuffixes)
                        {
                            string candidatePath = cDir + cStem + suf;
                            ulong hash = XxHash64Ext.Hash(candidatePath);
                            if (targetTexHashes.Remove(hash) && engine.UnknownHashes.Contains(hash))
                            {
                                Check(engine, candidatePath, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                                if (targetTexHashes.Count == 0) break;
                            }
                        }
                    }
                }
            }
        }

        private static readonly Regex SkinBinFileRegex = new(
            @"^(?:assets|data)/characters/(?<champ>[^/]+)/(?<folder>skins|themes)/(?<skin>(?:skin|theme)\d+|base)\.bin$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly uint SkinPropertiesClassHash = Fnv1a.HashLower("SkinCharacterDataProperties");
        private static readonly uint StaticMaterialClassHash = Fnv1a.HashLower("StaticMaterialDef");
        private static readonly uint SkinMeshPropertiesHash = Fnv1a.HashLower("skinMeshProperties");
        private static readonly uint MaterialOverrideHash = Fnv1a.HashLower("materialOverride");
        private static readonly uint DirectTextureHash = Fnv1a.HashLower("texture");
        private static readonly uint SubmeshNameHash = Fnv1a.HashLower("submesh");
        private static readonly uint SamplerValuesHash = Fnv1a.HashLower("samplerValues");
        private static readonly uint TexturePathHash = Fnv1a.HashLower("texturePath");

        // One material owns this matcher; only the fixed suffix vocabulary is shared globally.
        private sealed class MaterialTexturePathMatcher
        {
            private static readonly byte[][] Suffixes = TextureMapSuffixes.Select(Encoding.UTF8.GetBytes).ToArray();
            private readonly byte[][] _prefixes;
            private readonly Dictionary<string, byte[]> _descriptors = new(StringComparer.Ordinal);

            internal MaterialTexturePathMatcher(params string[] prefixes) =>
                _prefixes = prefixes.Select(Encoding.UTF8.GetBytes).ToArray();

            internal bool TryMatch(string stem, IEnumerable<string> descriptors, ulong target, out string match)
            {
                int length = Encoding.UTF8.GetByteCount(stem);
                byte[] rented = null;
                Span<byte> encodedStem = length <= 512 ? stackalloc byte[length]
                    : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
                try
                {
                    Encoding.UTF8.GetBytes(stem, encodedStem);
                    foreach (string descriptor in descriptors)
                    {
                        if (!_descriptors.TryGetValue(descriptor, out byte[] encodedDescriptor))
                            _descriptors[descriptor] = encodedDescriptor = Encoding.UTF8.GetBytes(descriptor);
                        foreach (byte[] suffix in Suffixes)
                        foreach (byte[] prefix in _prefixes)
                            if (TryMatchEncoded(prefix, encodedStem, encodedDescriptor, suffix, target, out match)) return true;
                    }
                    match = null;
                    return false;
                }
                finally { if (rented != null) ArrayPool<byte>.Shared.Return(rented); }
            }

            private static bool TryMatchEncoded(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> stem,
                ReadOnlySpan<byte> descriptor, ReadOnlySpan<byte> suffix, ulong target, out string match)
            {
                int length = prefix.Length + stem.Length + descriptor.Length + suffix.Length;
                byte[] rented = null;
                Span<byte> candidate = length <= 1024 ? stackalloc byte[length]
                    : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
                try
                {
                    prefix.CopyTo(candidate);
                    stem.CopyTo(candidate[prefix.Length..]);
                    descriptor.CopyTo(candidate[(prefix.Length + stem.Length)..]);
                    suffix.CopyTo(candidate[(prefix.Length + stem.Length + descriptor.Length)..]);
                    if (XxHash64.HashToUInt64(candidate) == target)
                    {
                        match = Encoding.UTF8.GetString(candidate);
                        return true;
                    }
                    match = null;
                    return false;
                }
                finally { if (rented != null) ArrayPool<byte>.Shared.Return(rented); }
            }
        }

        /// <summary>
        /// Resolves skin textures from their material context. When a skin BIN links a
        /// material sampler at an unknown hash, the owning submesh names the texture
        /// role, so stem + submesh + _tx_cm candidates are checked. Only fires on skin
        /// BINs with unknown sampler targets, keeping the cost to a few candidates each.
        /// </summary>
        internal void GuessSkinRoleTextures(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken,
            Func<BinTree> binTreeFactory = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Array is null || data.Count == 0 || engine.RemainingUnknownCount == 0) return;
            Match skin = SkinBinFileRegex.Match(NormalizePath(sourcePath));
            if (!skin.Success) return;

            string champ = skin.Groups["champ"].Value.ToLowerInvariant();
            string folder = skin.Groups["folder"].Value.ToLowerInvariant();
            string skinFile = skin.Groups["skin"].Value.ToLowerInvariant();
            string assetFolder = $"assets/characters/{champ}/{folder}/{skinFile}";

            BinTree tree;
            try
            {
                tree = binTreeFactory != null
                    ? binTreeFactory()
                    : new BinTree(new MemoryStream(data.Array, data.Offset, data.Count, writable: false));
                if (tree == null) return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService?.LogDebug($"GAME skin role scan skipped '{sourcePath}': {exception.Message}");
                return;
            }

            var materialTargets = new Dictionary<uint, List<ulong>>();
            foreach (BinTreeObject material in tree.Objects.Values)
            {
                if (material.ClassHash != StaticMaterialClassHash ||
                    !material.Properties.TryGetValue(SamplerValuesHash, out BinTreeProperty samplersProperty) ||
                    samplersProperty is not BinTreeContainer samplers)
                    continue;

                foreach (BinTreeStruct sampler in samplers.Elements.OfType<BinTreeStruct>())
                {
                    if (sampler.Properties.TryGetValue(TexturePathHash, out BinTreeProperty pathProperty) &&
                        pathProperty is BinTreeWadChunkLink link &&
                        link.Value != 0 && engine.UnknownHashes.Contains(link.Value))
                    {
                        if (!materialTargets.TryGetValue(material.PathHash, out List<ulong> targets))
                            materialTargets[material.PathHash] = targets = new List<ulong>();
                        targets.Add(link.Value);
                    }
                }
            }

            var directTargets = new HashSet<ulong>();
            var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (BinTreeObject obj in tree.Objects.Values)
            {
                if (obj.ClassHash != SkinPropertiesClassHash ||
                    !obj.Properties.TryGetValue(SkinMeshPropertiesHash, out BinTreeProperty meshProperty) ||
                    meshProperty is not BinTreeStruct mesh ||
                    !mesh.Properties.TryGetValue(MaterialOverrideHash, out BinTreeProperty overrideProperty) ||
                    overrideProperty is not BinTreeContainer overrides)
                    continue;

                foreach (BinTreeStruct entry in overrides.Elements.OfType<BinTreeStruct>())
                {
                    if (!entry.Properties.TryGetValue(SubmeshNameHash, out BinTreeProperty submeshProperty) ||
                        submeshProperty is not BinTreeString submesh ||
                        string.IsNullOrWhiteSpace(submesh.Value))
                        continue;

                    if (entry.Properties.TryGetValue(DirectTextureHash, out BinTreeProperty textureProperty) &&
                        textureProperty is BinTreeWadChunkLink directLink &&
                        directLink.Value != 0 && engine.UnknownHashes.Contains(directLink.Value))
                    {
                        directTargets.Add(directLink.Value);
                    }

                    HarvestSubmeshRoleTokens(submesh.Value, roles);
                }
            }

            if (materialTargets.Count == 0 && directTargets.Count == 0) return;

            roles.RemoveWhere(string.IsNullOrWhiteSpace);
            if (roles.Count == 0) return;

            var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"{champ}_{skinFile}" };
            if (SkinFolderStems.TryGetValue(assetFolder, out HashSet<string> folderStems))
                stems.UnionWith(folderStems);

            var candidates = new List<HashGuessCandidate>();
            foreach (string stem in stems)
            {
                candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_tx_cm.tex", HashGuessStrategy.CharacterTemplate));
                candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_tx_gm.tex", HashGuessStrategy.CharacterTemplate));
                candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_mask_tx_cm.tex", HashGuessStrategy.CharacterTemplate));
                candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_goldfresnelmasks.tex", HashGuessStrategy.CharacterTemplate));
                foreach (string role in roles)
                {
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_{role}_tx_cm.tex", HashGuessStrategy.CharacterTemplate));
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_{role}_tx_cm.dds", HashGuessStrategy.CharacterTemplate));
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_{role}_tx_gm.tex", HashGuessStrategy.CharacterTemplate));
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_{role}_tx.tex", HashGuessStrategy.CharacterTemplate));
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_{role}.dds", HashGuessStrategy.CharacterTemplate));
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_{role}_mask.tex", HashGuessStrategy.CharacterTemplate));
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_{role}_mask_tx_cm.tex", HashGuessStrategy.CharacterTemplate));
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_{role}_goldfresnelmasks.tex", HashGuessStrategy.CharacterTemplate));
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/2x_{stem}_{role}_tx_cm.tex", HashGuessStrategy.CharacterTemplate));
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/4x_{stem}_{role}_tx_cm.tex", HashGuessStrategy.CharacterTemplate));

                    if (role.Contains('_'))
                    {
                        int lastSep = role.LastIndexOf('_');
                        string rolePrefix = role[..lastSep];
                        string roleSuffix = role[(lastSep + 1)..];
                        candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_{rolePrefix}_tx_cm_{roleSuffix}.tex", HashGuessStrategy.CharacterTemplate));
                        candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_{rolePrefix}_tx_gm_{roleSuffix}.tex", HashGuessStrategy.CharacterTemplate));
                    }
                }
            }

            CheckIter(engine, candidates, sourceWadPath, cancellationToken, sourceChunkHash: sourceChunkHash);
        }

        private IReadOnlyDictionary<string, HashSet<string>> SkinFolderStems =>
            Corpus.GetOrCreate("skin-folder-txcm-stems", BuildSkinFolderStems);

        private static Dictionary<string, HashSet<string>> BuildSkinFolderStems(IReadOnlyList<string> knownPaths)
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in knownPaths)
            {
                if (!path.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase)) continue;
                string[] parts = path.Replace('\\', '/').Split('/');
                if (parts.Length < 6 || (!parts[3].Equals("skins", StringComparison.OrdinalIgnoreCase) && !parts[3].Equals("themes", StringComparison.OrdinalIgnoreCase))) continue;
                string file = parts[^1];
                string baseName = file.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
                                  file.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                    ? file[..file.LastIndexOf('.')]
                    : file;
                if (!baseName.EndsWith("_tx_cm", StringComparison.OrdinalIgnoreCase)) continue;

                string folder = string.Join('/', parts[..5]);
                if (!result.TryGetValue(folder, out HashSet<string> stems))
                    result[folder] = stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                stems.Add(baseName[..^"_tx_cm".Length]);
                int roleSeparator = baseName.LastIndexOf('_', baseName.Length - "_tx_cm".Length - 1);
                if (roleSeparator > 0)
                    stems.Add(baseName[..roleSeparator]);
            }

            return result;
        }

        private static string CleanRoleName(string submesh)
        {
            var builder = new StringBuilder(submesh.Length);
            foreach (char c in submesh.TrimEnd('\0'))
                if (char.IsLetterOrDigit(c) || c == '_')
                    builder.Append(char.ToLowerInvariant(c));
            return builder.ToString().Trim('_');
        }

        private void GuessChampionSpecialBins(
            HashGuessEngine engine,
            string sourceWadPath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(sourceWadPath) ||
                !sourceWadPath.Contains("champions", StringComparison.OrdinalIgnoreCase))
                return;

            string fileName = Path.GetFileName(sourceWadPath);
            string[] parts = fileName.Split('.');
            if (parts.Length != 3 || !parts[1].Equals("wad", StringComparison.OrdinalIgnoreCase))
                return;

            string champName = parts[0];
            if (string.IsNullOrEmpty(champName))
                return;

            ConcurrentDictionary<string, byte> scannedCharacters = _scannedWadCharacters.GetValue(
                engine,
                _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
            if (!scannedCharacters.TryAdd(champName, 0)) return;

            string champ = champName.ToLowerInvariant();
            string[] aliases = champ.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ||
                               champ.StartsWith("pet", StringComparison.OrdinalIgnoreCase)
                ? new[] { champ }
                : new[] { champ, $"jade_{champ}" };

            foreach (string alias in aliases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (engine.RemainingUnknownCount == 0) break;

                CheckSpecialBin($"data/characters/{alias}/{alias}.bin");
                CheckSpecialBin($"data/characters/{alias}/skins/root.bin");
                CheckSpecialBin($"gameplay.hol{alias}ncvc.bin");
                CheckSpecialBin($"gameplay.{alias}comps.bin");

                string consonantStem = new string(alias.Where(c => !"aeiou_".Contains(c)).ToArray());
                if (!string.IsNullOrEmpty(consonantStem) && !consonantStem.Equals(alias, StringComparison.OrdinalIgnoreCase))
                {
                    CheckSpecialBin($"gameplay.{consonantStem}comps.bin");
                }

                for (int s = 0; s <= 350; s++)
                {
                    if (engine.RemainingUnknownCount == 0) break;
                    CheckSpecialBin($"gameplay.{alias}skin{s}viewcontroller.bin");
                }

                var dynamicSkins = GetChampionSkinNames(alias, cancellationToken);
                foreach (string skin in dynamicSkins)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (engine.RemainingUnknownCount == 0) break;

                    CheckSpecialBin($"data/characters/{alias}/skins/{skin}.bin");
                    CheckSpecialBin($"data/characters/{alias}/animations/{skin}.bin");
                }
            }

            void CheckSpecialBin(string path)
            {
                ulong hash = XxHash64Ext.Hash(path);
                if (engine.UnknownHashes.Contains(hash))
                {
                    Check(engine, path, HashGuessStrategy.BinEntry, sourceWadPath);
                }
            }
        }

        internal int GrepFile(
            HashGuessEngine engine,
            string path = null,
            byte[] data = null,
            string source = "GAME grep file")
        {
            if (!string.IsNullOrWhiteSpace(path)) data = File.ReadAllBytes(path);
            else if (data == null) throw new ArgumentException("Either path or data must be provided.");
            int checkedCandidates = 0;
            foreach (HashGuessCandidate candidate in GrepFile(data, CancellationToken.None))
            {
                Check(engine, candidate.Path, candidate.Strategy, string.IsNullOrWhiteSpace(path) ? source : path);
                checkedCandidates++;
                if (engine.RemainingUnknownCount == 0) break;
            }
            return checkedCandidates;
        }

        private void GuessNamespacedPreloadScripts(HashGuessEngine engine, string name, string sourceWadPath,
            ulong sourceChunkHash, CancellationToken cancellationToken)
        {
            if (name.Contains('/') || name.Contains('\\')) return;
            var owners = Corpus.GetOrCreate("preload-script-owners", paths =>
            {
                const string prefix = "data/characters/";
                var result = new HashSet<string>(StringComparer.Ordinal);
                foreach (string path in paths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    int end = path.IndexOf('/', prefix.Length);
                    if (end <= prefix.Length) continue;
                    ReadOnlySpan<char> relative = path.AsSpan(end + 1);
                    bool script = relative.StartsWith("spells/", StringComparison.Ordinal) &&
                        (relative.EndsWith(".luabin64", StringComparison.Ordinal) || relative.EndsWith(".preload", StringComparison.Ordinal));
                    bool rootBin = relative.IndexOf('/') < 0 && relative.EndsWith(".bin", StringComparison.Ordinal);
                    if (script || rootBin) result.Add(path[prefix.Length..end]);
                }
                return result;
            });
            int dot = name.IndexOf('.');
            if (dot > 0 && dot == name.LastIndexOf('.') && owners.Contains(name[..dot]))
            {
                string owner = name[..dot], script = name[(dot + 1)..];
                if (!script.StartsWith(owner + "_", StringComparison.Ordinal)) script = owner + "_" + script;
                Check(owner, script);
                return;
            }
            if (dot >= 0) return;
            for (int separator = name.LastIndexOf('_'); separator > 0; separator = name.LastIndexOf('_', separator - 1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (owners.Contains(name[..separator])) Check(name[..separator], name);
            }

            void Check(string owner, string script)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string prefix = $"data/characters/{owner}/spells/";
                engine.CheckNormalizedParts(prefix, script, ".luabin64", HashGuessStrategy.PreloadReference, sourceWadPath, sourceChunkHash);
                engine.CheckNormalizedParts(prefix, script, ".preload", HashGuessStrategy.PreloadReference, sourceWadPath, sourceChunkHash);
            }
        }

        private static IEnumerable<HashGuessCandidate> ExtractLuaManifestCandidates(
            ArraySegment<byte> data,
            CancellationToken cancellationToken)
        {
            string[] luaExtensions = { "luabin64", "preload" };
            string[] luaCharacterPrefixes = { "", "spells/", "scripts/", "npcscripts", "npcscripts/" };
            string[] sharedScriptDirectories =
            {
                "data/spells", "data/spells/modules", "data/scripts", "data/shared/scripts",
                "data/shared/scripts/aicomponents", "data/shared/spells", "data/shared/npcscripts",
                "data/shared/tft/common", "data/shared/tft/items", "data/shared/tft/traits",
                "data/shared/spells/practicetool", "data/items", "data/items/spells",
                "data/items/spells/modules", "data/buildingblocks", "data/shared/gamemodes"
            };
            string[] luaCommonPaths =
            {
                "data/spells", "data/spells/modules", "data/scripts", "data/shared/scripts",
                "data/shared/scripts/aicomponents", "data/shared/spells", "data/shared/npcscripts",
                "data/shared/tft/common", "data/shared/tft/items", "data/shared/tft/traits",
                "data/shared/spells/practicetool", "data/items", "data/items/spells",
                "data/items/spells/modules", "data/buildingblocks", "data/shared/gamemodes",
                "data/shared/spells/cheat"
            };

            using var stream = new MemoryStream(data.Array, data.Offset, data.Count, false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, true), true);
            if (stream.Length < 8) yield break;

            reader.ReadBytes(4);
            uint characterCount = ReadManifestCount(reader);
            for (uint characterIndex = 0; characterIndex < characterCount; characterIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string character = ReadManifestString(reader).ToLowerInvariant();
                uint childCount = ReadManifestCount(reader);
                for (uint childIndex = 0; childIndex < childCount; childIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = ReadManifestString(reader).ToLowerInvariant();
                    foreach (string prefix in luaCharacterPrefixes)
                    foreach (string extension in luaExtensions)
                        yield return new HashGuessCandidate(
                            $"data/characters/{character}/{prefix}{name}.{extension}",
                            HashGuessStrategy.LuaManifest);
                }
            }

            uint sharedCount = ReadManifestCount(reader);
            var sharedNames = new List<string>((int)Math.Min(sharedCount, 100_000));
            for (uint sharedIndex = 0; sharedIndex < sharedCount; sharedIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sharedNames.Add(ReadManifestString(reader).ToLowerInvariant());
            }

            uint hashCount = ReadManifestCount(reader);
            long hashBytes = checked((long)hashCount * sizeof(ulong));
            if (hashBytes > stream.Length - stream.Position)
                throw new InvalidDataException("Lua manifest hash table exceeds the available data.");

            var hashMap = new Dictionary<ulong, uint>((int)Math.Min(hashCount, 100_000));
            for (uint i = 0; i < hashCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ulong entry = reader.ReadUInt64();
                uint dirIndex = (uint)(entry & 0x1F);
                ulong xxh3Truncated = entry >> 5;
                hashMap[xxh3Truncated] = dirIndex;
            }

            foreach (string name in sharedNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] nameBytes = Encoding.UTF8.GetBytes(name);
                ulong nameHashTruncated = XxHash3.HashToUInt64(nameBytes) >> 5;
                if (hashMap.TryGetValue(nameHashTruncated, out uint dirIndex) && dirIndex < (uint)sharedScriptDirectories.Length)
                {
                    string dir = sharedScriptDirectories[dirIndex];
                    foreach (string extension in luaExtensions)
                    {
                        yield return new HashGuessCandidate($"{dir}/{name}.{extension}", HashGuessStrategy.LuaManifest);
                    }
                }
                else
                {
                    // Fallback for stripped scripts (Cheat* or Map scripts)
                    if (name.StartsWith("cheat", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string extension in luaExtensions)
                            yield return new HashGuessCandidate($"data/shared/spells/cheat/{name}.{extension}", HashGuessStrategy.LuaManifest);
                    }
                    else
                    {
                        foreach (string prefix in luaCommonPaths)
                        foreach (string extension in luaExtensions)
                            yield return new HashGuessCandidate($"{prefix}/{name}.{extension}", HashGuessStrategy.LuaManifest);

                        for (int map = 0; map < 1500; map++)
                        foreach (string prefix in new[] { string.Empty, "mutators/" })
                            yield return new HashGuessCandidate(
                                $"levels/map{map}/scripts/{prefix}{name}.luabin64",
                                HashGuessStrategy.LuaManifest);
                    }
                }
            }
        }

        private static uint ReadManifestCount(BinaryReader reader)
        {
            const uint maximumCount = 1_000_000;
            if (reader.BaseStream.Length - reader.BaseStream.Position < sizeof(uint))
                throw new EndOfStreamException("Unexpected end of Lua manifest count.");
            uint count = reader.ReadUInt32();
            if (count > maximumCount)
                throw new InvalidDataException($"Lua manifest count {count} exceeds the safety limit.");
            return count;
        }

        private static string ReadManifestString(BinaryReader reader)
        {
            const uint maximumLength = 16_384;
            uint length = ReadManifestCount(reader);
            if (length > maximumLength || length > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException($"Lua manifest string length {length} is invalid.");
            byte[] bytes = reader.ReadBytes((int)length);
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        internal static IEnumerable<string> EnumerateArenaPropLabelTextures(string meshPath, string skinLabel)
        {
            string path = NormalizePath(meshPath);
            string[] parts = path.Split('/');
            if (parts.Length < 6 || parts[0] != "assets" || parts[1] != "characters" || parts[2].Length <= 10 ||
                !parts[2].StartsWith("tft_", StringComparison.Ordinal) || !parts[2].EndsWith("_props", StringComparison.Ordinal)) yield break;
            string owner = parts[2][4..^6];
            if (owner.Length == 0 || string.IsNullOrWhiteSpace(skinLabel) ||
                !skinLabel.StartsWith(owner + "_", StringComparison.OrdinalIgnoreCase) ||
                skinLabel.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_')) yield break;
            string prefix = path[..(path.LastIndexOf('/') + 1)] + "tft_arenaskin_" + skinLabel.ToLowerInvariant();
            foreach (string suffix in new[] { "_tx", "_tx_cm" })
            foreach (string extension in TextureMapExtensions)
                yield return prefix + suffix + extension;
        }

        internal static IEnumerable<HashGuessCandidate> ExtractMapGeometryTextureCandidates(
            string text, CancellationToken cancellationToken)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            int search = 0;
            while (search < text.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int start = text.IndexOf("maps/kitpieces/", search, StringComparison.OrdinalIgnoreCase);
                if (start < 0) yield break;
                search = start + "maps/kitpieces/".Length;
                if (start >= 7 && text.AsSpan(start - 7, 7).Equals("assets/", StringComparison.OrdinalIgnoreCase)) start -= 7;
                if (start < 4) continue;
                int length = text[start - 4] | text[start - 3] << 8 |
                             text[start - 2] << 16 | text[start - 1] << 24;
                if (length is <= 0 or > 512 || length > text.Length - start) continue;
                string material = text.Substring(start, length).ToLowerInvariant();
                if (material.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '/' or '.' or '-'))) continue;
                if (material.StartsWith("assets/", StringComparison.Ordinal)) material = material[7..];
                int separator = material.IndexOf("/materials/", StringComparison.Ordinal);
                if (separator < 0 || !material.EndsWith("_mat", StringComparison.Ordinal) ||
                    material.Contains("/../", StringComparison.Ordinal) || material.Contains("//", StringComparison.Ordinal)) continue;
                string stem = material[(material.LastIndexOf('/') + 1)..^4];
                if (stem.Length == 0) continue;
                string texture = "assets/" + material[..separator] + "/textures/" + stem + "_tx";
                if (!seen.Add(texture)) continue;
                yield return new(texture + ".tex", HashGuessStrategy.BinLengthPath);
                yield return new(texture + ".dds", HashGuessStrategy.BinLengthPath);
            }
        }

        private static IEnumerable<HashGuessCandidate> GrepFile(
            ArraySegment<byte> data,
            CancellationToken cancellationToken)
        {
            if (data.Array is null || data.Count == 0) yield break;

            string text = Encoding.Latin1.GetString(data.Array, data.Offset, data.Count);
            var paths = new HashSet<string>(StringComparer.Ordinal);

            if (text.StartsWith("OEGM", StringComparison.Ordinal))
                foreach (var candidate in ExtractMapGeometryTextureCandidates(text, cancellationToken))
                    yield return candidate;

            foreach (Match match in Regex.Matches(text, @"(?:ASSETS|Common|DATA|DATA_SOON|DATA_Soon|Gameplay|Global|LEVELS|Loadouts|UX|UIAutoAtlas)/[0-9a-zA-Z_. /-]+"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string rawPath = match.Value;
                string path = rawPath.ToLowerInvariant().Replace("data_soon/", "data/", StringComparison.Ordinal);
                paths.Add(path);

                int pos = match.Index;
                if (pos >= 2)
                {
                    int n = ByteAt(data, pos - 2) | (ByteAt(data, pos - 1) << 8);
                    if (n == 0 && pos >= 4)
                    {
                        n = ByteAt(data, pos - 4) | (ByteAt(data, pos - 3) << 8) |
                            (ByteAt(data, pos - 2) << 16) | (ByteAt(data, pos - 1) << 24);
                    }

                    if (n > 0 && n < rawPath.Length)
                    {
                        string shortened = rawPath[..n].ToLowerInvariant().Replace("data_soon/", "data/", StringComparison.Ordinal);
                        paths.Add(shortened);
                    }
                }
            }

            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (HashGuessCandidate candidate in ExpandGrepFilePath(path, HashGuessStrategy.EmbeddedPathGrep))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (emitted.Add(candidate.Path)) yield return candidate;
                }
            }
        }

        private static IEnumerable<HashGuessCandidate> ExpandGrepFilePath(string path, HashGuessStrategy strategy)
        {
            if (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
            {
                string prefix = path[..^4];
                yield return new HashGuessCandidate(path, strategy);
                yield return new HashGuessCandidate(prefix + ".luabin", HashGuessStrategy.LuaVariant);
                yield return new HashGuessCandidate(prefix + ".luabin64", HashGuessStrategy.LuaVariant);
                yield return new HashGuessCandidate(prefix + ".preload", HashGuessStrategy.LuaVariant);
                yield break;
            }

            yield return new HashGuessCandidate(path, strategy);
        }

        private static bool IsAscii(string value)
        {
            foreach (char character in value)
                if (character > 0x7F) return false;
            return true;
        }

        private static byte ByteAt(ArraySegment<byte> data, int index) => data.Array[data.Offset + index];

        private IReadOnlyDictionary<string, IReadOnlyList<string>> GetChampionSkinMap(CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate("all-champion-skin-maps", knownPaths =>
            {
                var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

                for (int pathIndex = 0; pathIndex < knownPaths.Count; pathIndex++)
                {
                    if ((pathIndex & 0x3fff) == 0) cancellationToken.ThrowIfCancellationRequested();
                    string path = knownPaths[pathIndex];
                    if (!path.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase) &&
                        !path.StartsWith("data/characters/", StringComparison.OrdinalIgnoreCase))
                        continue;

                    int skinsIdx = path.IndexOf("/skins/", StringComparison.OrdinalIgnoreCase);
                    if (skinsIdx <= 0) continue;

                    string champ = path.Substring(path.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase) ? 18 : 16, skinsIdx - (path.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase) ? 18 : 16)).ToLowerInvariant();
                    string rel = path.Substring(skinsIdx + 7);
                    int slash = rel.IndexOf('/');
                    string skin = slash > 0 ? rel[..slash] : rel;
                    if (skin.Length is >= 3 and <= 35 && !skin.Contains('.'))
                    {
                        if (!map.TryGetValue(champ, out var set))
                        {
                            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base" };
                            map[champ] = set;
                        }
                        set.Add(skin);
                    }
                }

                return (IReadOnlyDictionary<string, IReadOnlyList<string>>)map.ToDictionary(
                    kvp => kvp.Key,
                    kvp => (IReadOnlyList<string>)kvp.Value.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList(),
                    StringComparer.OrdinalIgnoreCase);
            });
        }

        private IReadOnlyList<string> GetChampionSkinNames(string character, CancellationToken cancellationToken)
        {
            var map = GetChampionSkinMap(cancellationToken);
            string baseChar = character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase) ? character[5..] : character;
            
            var skins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base" };
            if (map.TryGetValue(character, out var directSkins))
            {
                for (int i = 0; i < directSkins.Count; i++) skins.Add(directSkins[i]);
            }
            if (baseChar != character && map.TryGetValue(baseChar, out var baseSkins))
            {
                for (int i = 0; i < baseSkins.Count; i++) skins.Add(baseSkins[i]);
            }

            // Add attested skin numbers + padding
            int maxAttested = 0;
            foreach (var s in skins)
            {
                if (s.StartsWith("skin", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(s[4..], out int num))
                {
                    if (num > maxAttested) maxAttested = num;
                }
            }

            int limit = Math.Max(maxAttested + 15, 85);
            if (character.Equals("sightward", StringComparison.OrdinalIgnoreCase)) limit = 500;
            for (int i = 0; i <= limit; i++)
            {
                skins.Add($"skin{i}");
                if (i <= 9) skins.Add($"skin{i:D2}");
            }

            for (int i = 300; i <= 350; i++) skins.Add($"skin{i}");

            return skins.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static IEnumerable<ulong> EnumerateChunkLinks(BinTree tree)
        {
            var roots = tree.Objects.Values.SelectMany(obj => obj.Properties.Values)
                .Concat(tree.DataOverrides.Select(ovr => ovr.Property));
            foreach (BinTreeProperty root in roots)
            {
                foreach (BinTreeProperty prop in EnumerateAllProperties(root))
                {
                    if (prop is BinTreeWadChunkLink link && link.Value != 0)
                        yield return link.Value;
                }
            }
        }

        private static IEnumerable<BinTreeProperty> EnumerateAllProperties(BinTreeProperty property)
        {
            if (property == null) yield break;
            yield return property;

            IEnumerable<BinTreeProperty> children = property switch
            {
                BinTreeStruct structure => structure.Properties.Values,
                BinTreeOptional optional when optional.Value != null => new[] { optional.Value },
                BinTreeContainer container => container.Elements,
                BinTreeMap map => map.SelectMany(pair => new[] { pair.Key, pair.Value }),
                _ => Array.Empty<BinTreeProperty>()
            };
            foreach (BinTreeProperty child in children)
            foreach (BinTreeProperty descendant in EnumerateAllProperties(child))
                yield return descendant;
        }

        private void GuessImageAutoAtlasPaths(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (data.Array is null || data.Count == 0) return;
            if (!ImageAutoAtlas.IsAtlas(data.AsSpan()) || !ImageAutoAtlas.TryRead(data.Array[data.Offset..(data.Offset + data.Count)], out ImageAutoAtlas atlas))
                return;

            IReadOnlyDictionary<ulong, string> knownDict = HashFile.Load();

            string atlasPath = knownDict.GetValueOrDefault(sourceChunkHash, sourcePath);
            if (atlasPath.StartsWith("clientstates/", StringComparison.OrdinalIgnoreCase) ||
                atlasPath.StartsWith("ux/", StringComparison.OrdinalIgnoreCase))
            {
                for (int index = 0; index < atlas.TextureHashes.Count; index++)
                    Check(engine, $"uiautoatlas/{atlasPath}/atlas_{index}.tex",
                        HashGuessStrategy.AtlasReference, sourceWadPath, sourceChunkHash);
            }

            // Ensure any sprite hash not in HashFile is marked unknown in engine
            var unresolvedSprites = new HashSet<ulong>();
            foreach (var sprite in atlas.Sprites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (engine.UnknownHashes.Contains(sprite.SpriteHash) || !knownDict.ContainsKey(sprite.SpriteHash))
                {
                    engine.EnsureUnknown(sprite.SpriteHash);
                    unresolvedSprites.Add(sprite.SpriteHash);
                }
            }
            if (unresolvedSprites.Count == 0) return;

            var candidateDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(sourcePath) && !sourcePath.Equals(".bin", StringComparison.OrdinalIgnoreCase))
            {
                string dir = Path.GetDirectoryName(PathUtils.NormalizePath(sourcePath));
                if (!string.IsNullOrEmpty(dir))
                    candidateDirs.Add(PathUtils.NormalizeSeparators(dir));
            }

            if (candidateDirs.Count == 0 && atlas.TextureHashes.Count > 0)
            {
                var texDirIndex = GetTextureHashToDirectoryIndex();
                foreach (ulong texHash in atlas.TextureHashes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (texDirIndex.TryGetValue(texHash, out string dir))
                        candidateDirs.Add(dir);
                }
            }

            if (candidateDirs.Count == 0)
            {
                foreach (string dir in GetAllKnownAtlasDirectories())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    candidateDirs.Add(dir);
                }
            }

            foreach (var sprite in atlas.Sprites)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!knownDict.TryGetValue(sprite.SpriteHash, out string spritePath)) continue;
                string directory = PathUtils.NormalizeSeparators(Path.GetDirectoryName(spritePath));
                if (!string.IsNullOrEmpty(directory)) candidateDirs.Add(directory);
            }

            IReadOnlyList<string> candidatePatterns = GetAutoAtlasCandidatePatterns();
            foreach (string baseDir in candidateDirs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalizedDirectory = PathUtils.NormalizePath(baseDir);
                foreach (string pattern in candidatePatterns)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    bool matched = engine.CheckNormalizedParts(normalizedDirectory, "/", pattern,
                        HashGuessStrategy.AtlasReference, sourceWadPath, sourceChunkHash);
                    // Misses cannot change the outstanding sprite set; rescan only after a hit.
                    if (matched) unresolvedSprites.RemoveWhere(hash => !engine.UnknownHashes.Contains(hash));
                    if (unresolvedSprites.Count == 0 || engine.RemainingUnknownCount == 0) break;
                }
                if (unresolvedSprites.Count == 0 || engine.RemainingUnknownCount == 0) break;
            }
        }

        private IReadOnlyDictionary<ulong, string> GetTextureHashToDirectoryIndex()
        {
            return Corpus.GetOrCreate("texture-hash-to-dir-index", knownPaths =>
            {
                var dict = new Dictionary<ulong, string>();
                foreach (string path in knownPaths)
                {
                    if (path.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                    {
                        string norm = PathUtils.NormalizePath(path);
                        string dir = PathUtils.NormalizeSeparators(Path.GetDirectoryName(norm));
                        if (!string.IsNullOrEmpty(dir))
                            dict[XxHash64Ext.Hash(norm)] = dir;
                    }
                }
                return dict;
            });
        }

        private IReadOnlyList<string> GetAllKnownAtlasDirectories()
        {
            return Corpus.GetOrCreate("all-known-atlas-directories", knownPaths =>
            {
                var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in knownPaths)
                {
                    if (path.EndsWith("atlas_info.bin", StringComparison.OrdinalIgnoreCase))
                    {
                        string dir = Path.GetDirectoryName(PathUtils.NormalizePath(path));
                        if (!string.IsNullOrEmpty(dir))
                            dirs.Add(PathUtils.NormalizeSeparators(dir));
                    }
                }
                return dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private IReadOnlyList<string> GetAutoAtlasCandidatePatterns()
        {
            return Corpus.GetOrCreate("autoatlas-candidate-patterns-v5", knownPaths =>
            {
                var patterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string path in knownPaths)
                {
                    string norm = PathUtils.NormalizePath(path);

                    // Extract relative filenames from any known autoatlas folders
                    int autoAtlasIdx = norm.IndexOf("/autoatlas/", StringComparison.OrdinalIgnoreCase);
                    if (autoAtlasIdx >= 0)
                    {
                        string sub = norm[(autoAtlasIdx + "/autoatlas/".Length)..];
                        int firstSlash = sub.IndexOf('/');
                        if (firstSlash >= 0 && firstSlash < sub.Length - 1)
                        {
                            string relFile = sub[(firstSlash + 1)..];
                            if (!string.IsNullOrWhiteSpace(relFile) && !relFile.StartsWith("atlas_", StringComparison.OrdinalIgnoreCase))
                                patterns.Add(relFile);
                        }
                        else
                        {
                            string fileName = Path.GetFileName(norm);
                            if (!string.IsNullOrWhiteSpace(fileName) && !fileName.StartsWith("atlas_", StringComparison.OrdinalIgnoreCase))
                                patterns.Add(fileName);
                        }
                    }
                    else if (norm.Contains("/icons2d/", StringComparison.OrdinalIgnoreCase) ||
                             (norm.Contains("/hud/", StringComparison.OrdinalIgnoreCase) &&
                              norm.Contains("_circle", StringComparison.OrdinalIgnoreCase)) ||
                             norm.StartsWith("ux/", StringComparison.OrdinalIgnoreCase) ||
                             norm.StartsWith("clientstates/", StringComparison.OrdinalIgnoreCase))
                    {
                        string fileName = Path.GetFileName(norm);
                        if (!string.IsNullOrWhiteSpace(fileName) && !fileName.StartsWith("atlas_", StringComparison.OrdinalIgnoreCase))
                        {
                            patterns.Add(fileName);
                            string stem = Path.GetFileNameWithoutExtension(fileName);
                            if (!string.IsNullOrWhiteSpace(stem) && stem.Length <= 100)
                            {
                                patterns.Add($"{stem}.png");
                                patterns.Add($"{stem}.dds");
                                patterns.Add($"{stem}.tex");
                            }
                        }
                    }
                }

                return patterns.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private static string GetBasename(string path)
        {
            int separator = path.LastIndexOf('/');
            return separator >= 0 ? path[(separator + 1)..] : path;
        }

    }
}
