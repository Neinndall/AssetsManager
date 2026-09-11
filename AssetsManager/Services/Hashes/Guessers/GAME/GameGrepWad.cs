using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

namespace AssetsManager.Services.Hashes.Guessers
{
    internal sealed partial class GameHashGuesser
    {

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
            if (!ShouldGrepExtension(extension))
            {
                return; // don't grep filetypes known to not contain full paths
            }

            bool isBin = extension is "bin" or "inibin";
            if (isBin)
            {
                string text = Encoding.Latin1.GetString(data.Array, data.Offset, data.Count);
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match match in Regex.Matches(text, @"(?:ASSETS|DATA|Characters|Shaders|Maps|Gameplay|ClientStates|Patching|Loadouts)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
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

                    if (path.StartsWith("characters/", StringComparison.OrdinalIgnoreCase))
                    {
                        CheckGame(path);
                        CheckGame($"assets/{path}");
                        CheckGame($"data/{path}");
                    }
                    else if (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
                    {
                        string prefix = path[..^4];
                        CheckGame(path);
                        CheckGame(prefix + ".luabin", HashGuessStrategy.LuaVariant);
                        CheckGame(prefix + ".luabin64", HashGuessStrategy.LuaVariant);
                        CheckGame(prefix + ".preload", HashGuessStrategy.LuaVariant);
                    }
                    else if (path.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("assets/shaders/", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("data/shaders/", StringComparison.OrdinalIgnoreCase))
                    {
                        var candidateBases = path.StartsWith("assets/", StringComparison.OrdinalIgnoreCase) ||
                                             path.StartsWith("data/", StringComparison.OrdinalIgnoreCase)
                            ? new[] { path }
                            : new[] { $"assets/{path}", $"data/{path}", $"assets/shaders/generated/{path}" };
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
                                        Enumerable.Range(0, 32).Select(index =>
                                            $"{candidateBase}{extensionName}{variant}_{index}"))));
                            if (engine.RemainingUnknownCount == 0) break;
                        }
                    }
                    else if (path.StartsWith("maps/mapgeometry/", StringComparison.OrdinalIgnoreCase))
                    {
                        CheckGame($"data/{path}.mapgeo");
                        CheckGame($"data/{path}.materials.bin");
                    }
                    else if (path.StartsWith("clientstates/", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("patching/", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("loadouts/", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
                    {
                        CheckGame(path);
                        int separator = path.LastIndexOf('/');
                        if (separator > 0)
                        {
                            string parent = path[..separator];
                            CheckGame(parent);
                            int parentSeparator = parent.LastIndexOf('/');
                            if (parentSeparator > 0) CheckGame(parent[..parentSeparator]);
                        }
                    }
                    else
                    {
                        CheckGame(path);
                        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                            CheckGame(path[..^4] + ".dds", HashGuessStrategy.ImageExtensionVariant);
                    }
                }

                GuessDottedBinPaths(engine, data, sourceWadPath, sourceChunkHash, cancellationToken);
                if (data.Array is not null && data.Count >= sizeof(int) && FileTypeDetector.IsPropertyBin(data.AsSpan()))
                {
                    BinTree cachedTree = null;
                    BinTree GetCachedBinTree()
                    {
                        if (cachedTree == null)
                        {
                            using var stream = new MemoryStream(data.Array, data.Offset, data.Count, writable: false);
                            cachedTree = new BinTree(stream);
                        }
                        return cachedTree;
                    }

                    GuessAnimationBinPaths(engine, data, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken, GetCachedBinTree);
                    GuessRegaliaBinChunkLinks(engine, data, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken, GetCachedBinTree);
                    GuessSkinCharacterBinChunkLinks(engine, data, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken, GetCachedBinTree);
                    GuessSkinRoleTextures(engine, data, sourcePath, sourceWadPath, sourceChunkHash, cancellationToken, GetCachedBinTree);
                }

                GuessChampionSpecialBins(engine, sourceWadPath, cancellationToken);
                return;
            }

            if (extension == "preload")
            {
                string text = Encoding.Latin1.GetString(data.Array, data.Offset, data.Count);
                string directory = PathUtils.NormalizeSeparators(Path.GetDirectoryName(sourcePath));

                foreach (Match match in Regex.Matches(text, @"Name=""([^""]+)"""))
                {
                    if (!IsAscii(match.Groups[1].Value)) continue;
                    string path = NormalizePath(match.Groups[1].Value);
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
                        CheckGame(directory + "/" + path + ".preload", HashGuessStrategy.PreloadReference);
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
                    if (!IsAscii(match.Groups[1].Value)) continue;
                    CheckGame(
                        NormalizePath(PathUtils.NormalizeVirtualPath($"{directory}/{match.Groups[1].Value}")),
                        HashGuessStrategy.ShaderInclude);
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
                    if (!IsAscii(line)) continue;
                    CheckGame(
                        NormalizePath(Path.Combine(directory, line.Trim())),
                        HashGuessStrategy.AtlasReference);
                }
                return;
            }

            CheckGameCandidates(GrepFile(data, cancellationToken));
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

        private void GuessAnimationBinPaths(
            HashGuessEngine engine,
            ArraySegment<byte> data,
            string sourcePath,
            string sourceWadPath,
            ulong sourceChunkHash,
            CancellationToken cancellationToken,
            Func<BinTree> binTreeFactory = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Match context = AnimationBinPathRegex.Match(PathUtils.NormalizePath(sourcePath));
            if (!context.Success || data.Array is null || data.Count == 0) return;

            string character = context.Groups["character"].Value.ToLowerInvariant();
            string sourceSkin = context.Groups["skin"].Value.ToLowerInvariant();

            var links = new HashSet<AnimationFileLink>();
            try
            {
                BinTree tree = binTreeFactory != null
                    ? binTreeFactory()
                    : new BinTree(new MemoryStream(data.Array, data.Offset, data.Count, writable: false));
                if (tree == null) return;
                cancellationToken.ThrowIfCancellationRequested();
                foreach (AnimationFileLink link in EnumerateAnimationFileLinks(tree))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    links.Add(link);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logService?.LogDebug($"GAME animation BIN link scan skipped '{sourcePath}': {exception.Message}");
                return;
            }

            foreach (AnimationFileLink link in links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.IsNullOrWhiteSpace(link.Path))
                    Check(engine, link.Path, HashGuessStrategy.AnimationBinLink, sourceWadPath, sourceChunkHash);
            }

            var unresolved = links
                .Where(link => link.PathHash != 0 && engine.UnknownHashes.Contains(link.PathHash))
                .ToList();
            cancellationToken.ThrowIfCancellationRequested();
            if (unresolved.Count == 0) return;

            CheckIter(
                engine,
                EnumerateAnimationPaths(
                    character,
                    sourceSkin,
                    IsThemeAnimationContext(character, sourceSkin),
                    unresolved,
                    engine.UnknownHashes,
                    cancellationToken),
                HashGuessStrategy.AnimationBinLink,
                sourceWadPath,
                cancellationToken,
                sourceChunkHash);
            var remainingHashes = unresolved
                .Where(link => engine.UnknownHashes.Contains(link.PathHash))
                .Select(link => link.PathHash)
                .ToHashSet();
            if (remainingHashes.Count == 0 || engine.RemainingUnknownCount == 0) return;

            var characters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddAnimationCharacter(character);
            if (character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase))
                AddAnimationCharacter(character[5..]);
            else if (!character.StartsWith("pet", StringComparison.OrdinalIgnoreCase))
                AddAnimationCharacter($"jade_{character}");

            var emittedCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int checkedCandidates = 0;

            var namedActions = EnumerateAnimationNameCandidates(
                character,
                unresolved,
                remainingHashes,
                cancellationToken)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (namedActions.Count > 0)
            {
                foreach (string animationCharacter in characters)
                foreach (string skin in EnumerateAnimationSkins(animationCharacter))
                {
                    if (remainingHashes.Count == 0 || engine.RemainingUnknownCount == 0) return;
                    bool isTheme = IsThemeAnimationContext(animationCharacter, skin);
                    foreach (string action in namedActions)
                    foreach (string candidate in EnumerateAnimationCandidates(
                                 animationCharacter,
                                 skin,
                                 action,
                                 isTheme))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!emittedCandidates.Add(candidate)) continue;
                        ulong hash = XxHash64Ext.Hash(candidate);
                        if (remainingHashes.Remove(hash) && engine.UnknownHashes.Contains(hash))
                            Check(engine, candidate, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                    }
                }
            }

            foreach (string animationCharacter in characters)
            foreach (string skin in EnumerateAnimationSkins(animationCharacter))
            {
                if (remainingHashes.Count == 0 || engine.RemainingUnknownCount == 0) return;
                bool isTheme = IsThemeAnimationContext(animationCharacter, skin);
                foreach (string action in EnumerateActionsForSkin(animationCharacter, skin))
                foreach (string candidate in EnumerateAnimationCandidates(
                             animationCharacter,
                             skin,
                             action,
                             isTheme))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!emittedCandidates.Add(candidate)) continue;
                    if (checkedCandidates++ >= AnimationBinFallbackCandidateBudget ||
                        remainingHashes.Count == 0 || engine.RemainingUnknownCount == 0) return;

                    ulong hash = XxHash64Ext.Hash(candidate);
                    if (remainingHashes.Remove(hash) && engine.UnknownHashes.Contains(hash))
                        Check(engine, candidate, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                }
            }

            void AddAnimationCharacter(string value)
            {
                if (!string.IsNullOrWhiteSpace(value)) characters.Add(value.ToLowerInvariant());
            }

            IEnumerable<string> EnumerateAnimationSkins(string animationCharacter)
            {
                IReadOnlyList<string> knownSkins = GetChampionSkinNames(animationCharacter, cancellationToken);
                bool hasSource = !string.IsNullOrEmpty(sourceSkin) &&
                    !sourceSkin.Equals("root", StringComparison.OrdinalIgnoreCase) &&
                    !sourceSkin.Equals("shared", StringComparison.OrdinalIgnoreCase);
                // Shared base container first: highest hit density per candidate, so it
                // survives the fallback budget even when the source skin overflows it.
                if (knownSkins.Contains("base", StringComparer.OrdinalIgnoreCase) &&
                    (!hasSource || !sourceSkin.Equals("base", StringComparison.OrdinalIgnoreCase)))
                    yield return "base";
                if (hasSource)
                    yield return sourceSkin;

                foreach (string candidate in OrderAnimationContainers(sourceSkin, knownSkins))
                    if (!candidate.Equals(sourceSkin, StringComparison.OrdinalIgnoreCase) &&
                        !candidate.Equals("base", StringComparison.OrdinalIgnoreCase)) yield return candidate;
            }

            IEnumerable<string> EnumerateActionsForSkin(string animChar, string sk)
            {
                bool isTargetSkin = !string.IsNullOrEmpty(sourceSkin) &&
                                    sk.Equals(sourceSkin, StringComparison.OrdinalIgnoreCase);
                // The shared base container only gets thin per-character actions, so pooled
                // actions never reach it before the budget dies; give it the full fallback.
                if (isTargetSkin || string.IsNullOrEmpty(sourceSkin) ||
                    sk.Equals("base", StringComparison.OrdinalIgnoreCase))
                {
                    return EnumerateFallbackActions(animChar);
                }
                return GetCharacterAnimationActions(animChar);
            }

            IReadOnlyList<string> EnumerateFallbackActions(string animationCharacter) =>
                Corpus.GetOrCreate($"fallback-animation-actions/{animationCharacter.ToLowerInvariant()}", _ =>
                    GetCharacterAnimationActions(animationCharacter)
                        .Concat(GetGlobalAnimationActions(cancellationToken))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList());

            static IEnumerable<string> EnumerateAnimationCandidates(
                string animationCharacter,
                string skin,
                string action,
                bool includeThemeLayout)
            {
                foreach (string candidate in EnumerateAnimationNameVariants(
                             animationCharacter,
                             skin,
                             action,
                             includeThemeLayout: includeThemeLayout))
                    yield return candidate;

                if (!action.Equals("recall", StringComparison.OrdinalIgnoreCase)) yield break;
                foreach (string root in AnimationRootPrefixes)
                {
                    yield return $"{root}/characters/{animationCharacter}/skins/{skin}/animations/recall.skins_{animationCharacter}_{skin}.anm";
                    if (animationCharacter.StartsWith("jade_", StringComparison.OrdinalIgnoreCase))
                        yield return $"{root}/characters/{animationCharacter}/skins/{skin}/animations/recall.skins_{animationCharacter[5..]}_{skin}.anm";
                }
            }
        }

        private IReadOnlyList<string> GetDynamicLoadoutRegaliaPaths(CancellationToken cancellationToken)
        {
            return Corpus.GetOrCreate("dynamic-loadout-regalia-paths", knownPaths =>
            {
                var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                    string p = knownPaths[i];
                    if (p.StartsWith("assets/loadouts/summoneremotes/", StringComparison.OrdinalIgnoreCase))
                        candidates.Add(Regex.Replace(p, @"_(?:inventory|glow)\.(?:tex|dds)$", "_selector.tex", RegexOptions.IgnoreCase));
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

                        candidates.Add($"{dir}{file}.tex");
                        candidates.Add($"{dir}{file}.dds");
                        candidates.Add($"{dir}{file}.png");
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

                        candidates.Add(clean);
                        candidates.Add($"data/{clean}");
                        candidates.Add($"assets/{clean}");
                    }
                }

                var cutscenePets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var petThemeTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "base", "tier1" };
                for (int i = 0; i < knownPaths.Count; i++)
                {
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
                        candidates.Add($"loadouts/companions/{pet}_{theme}_{theme}_tier{tier}.cutscene.bin");
                        candidates.Add($"loadouts/companions/{pet}_{theme}_tier{tier}.cutscene.bin");
                        candidates.Add($"data/loadouts/companions/{pet}_{theme}_{theme}_tier{tier}.cutscene.bin");
                        candidates.Add($"assets/loadouts/companions/{pet}_{theme}_{theme}_tier{tier}.cutscene.bin");
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
                    candidates.Add(candidate);
                }

                return candidates.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            });
        }

        private static readonly Regex SkinPathRegex = new(
            @"characters/(?<champ>[^/]+)/skins/(?<skin>base|skin0*(?<num>\d+))",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MaterialPathRegex = new(
            @"characters/(?<champ>[^/]+)/skins/(?<skin>[^/]+)/materials/(?<mat>[^/]+)",
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

            var candidates = GetDynamicLoadoutRegaliaPaths(cancellationToken);
            var candidateIndex = Corpus.GetOrCreate("dynamic-loadout-regalia-hashes", _ =>
            {
                var index = new Dictionary<ulong, int>();
                for (int i = 0; i < candidates.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Preserve the first path and candidate order, including hash collisions.
                    index.TryAdd(XxHash64Ext.Hash(candidates[i]), i);
                }
                return index;
            });
            foreach (int index in unresolved.Where(candidateIndex.ContainsKey).Select(hash => candidateIndex[hash]).Order())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (engine.RemainingUnknownCount == 0) return;
                Check(engine, candidates[index], HashGuessStrategy.BannerVariant, sourceWadPath, sourceChunkHash);
            }
        }

        private static void HarvestSubmeshTokens(string text, HashSet<string> smSet)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (string sm in text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string smLow = sm.ToLowerInvariant();
                if (smLow.Length >= 3)
                {
                    smSet.Add(smLow);
                    string[] parts = smLow.Split('_', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1)
                    {
                        for (int i = 1; i < parts.Length; i++)
                            smSet.Add(string.Join('_', parts.Take(i)));
                        foreach (string part in parts)
                        {
                            if (part.Length >= 3 && part != "top" && part != "low" && part != "bot")
                                smSet.Add(part);
                        }
                    }
                }
            }
        }

        private static void HarvestRoleTokens(string text, HashSet<string> descriptors)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Contains('/')) return;
            string low = text.ToLowerInvariant().Replace("_texture", "").Replace("_tex", "");
            if (low.Length >= 3)
            {
                descriptors.Add("_" + low);
                string[] parts = low.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 1)
                {
                    descriptors.Add("_" + string.Join("", parts));
                    foreach (string part in parts)
                    {
                        if (part.Length >= 3 && part != "texture")
                        {
                            descriptors.Add("_" + part);
                            if (part.EndsWith("scroll", StringComparison.Ordinal))
                                descriptors.Add("_scrollmask");
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
            "_diffuse", "_mult", "_base_cm_tx", "_base_tx", ""
        };
        private static readonly string[] TextureMapVariants = { "", ".project_jade" };
        private static readonly string[] TextureMapExtensions = { ".tex", ".dds" };
        private static readonly string[] TextureMapSuffixes = BuildTextureMapSuffixes();

        private static string[] BuildTextureMapSuffixes() =>
            (from kind in TextureMapKinds
             from variant in TextureMapVariants
             from extension in TextureMapExtensions
             select kind + variant + extension).ToArray();

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
                var knownPaths = Corpus.GetOrCreate("known-hashes-dict", _ => HashFile.Load());
                return knownPaths.TryGetValue(link.Value, out string path) ? path : null;
            }

            var submeshesBySkin = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var skinObjects = new List<(BinTreeObject Obj, string SimpleSkin, string Skeleton, HashSet<ulong> Links)>();
            var materialObjects = new List<(string Champ, string Skin, string RawMat, HashSet<ulong> Links, HashSet<string> Descriptors)>();
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

                    if (targetTexHashes.Count > 0)
                    {
                        skinObjects.Add((obj, simpleSkin, skeleton, targetTexHashes));
                        totalUnresolved += targetTexHashes.Count;
                    }
                }
                else if (obj.ClassHash == 0xff9d3409) // StaticMaterialDef
                {
                    if (obj.Properties.TryGetValue(0x8d39bde6, out BinTreeProperty pMat) &&
                        pMat is BinTreeString sMat &&
                        !string.IsNullOrEmpty(sMat.Value))
                    {
                        Match matMatch = MaterialPathRegex.Match(sMat.Value.Replace('\\', '/'));
                        if (matMatch.Success)
                        {
                            var matLinks = new HashSet<ulong>();
                            foreach (BinTreeProperty p in obj.Properties.Values)
                                CollectChunkLinks(p, matLinks);

                            if (matLinks.Count > 0)
                            {
                                string mChamp = matMatch.Groups["champ"].Value.ToLowerInvariant();
                                string mSkin = matMatch.Groups["skin"].Value.ToLowerInvariant();
                                string rawMat = matMatch.Groups["mat"].Value.ToLowerInvariant();
                                if (rawMat.EndsWith("_inst", StringComparison.OrdinalIgnoreCase)) rawMat = rawMat[..^5];
                                if (rawMat.EndsWith("_mat", StringComparison.OrdinalIgnoreCase)) rawMat = rawMat[..^4];
                                if (rawMat.StartsWith(mChamp + "_" + mSkin + "_", StringComparison.OrdinalIgnoreCase))
                                    rawMat = rawMat[(mChamp.Length + mSkin.Length + 2)..];
                                else if (rawMat.StartsWith(mChamp + "_", StringComparison.OrdinalIgnoreCase))
                                    rawMat = rawMat[(mChamp.Length + 1)..];

                                var descriptors = new HashSet<string>(CanonicalMaterialRoles, StringComparer.OrdinalIgnoreCase);
                                foreach (BinTreeProperty p in obj.Properties.Values)
                                {
                                    if (p is BinTreeContainer container)
                                    {
                                        foreach (BinTreeProperty elem in container.Elements)
                                        {
                                            if (elem is BinTreeStruct samplerStruct)
                                            {
                                                foreach (BinTreeProperty sp in samplerStruct.Properties.Values)
                                                {
                                                    if (sp is BinTreeString sVal)
                                                        HarvestRoleTokens(sVal.Value, descriptors);
                                                }
                                            }
                                        }
                                    }
                                }

                                materialObjects.Add((mChamp, mSkin, rawMat, matLinks, descriptors));
                                totalUnresolved += matLinks.Count;
                            }
                        }
                    }
                }
            }

            if (totalUnresolved == 0 || engine.RemainingUnknownCount == 0) return;

            // Phase 2: Resolve Modern Skin Materials (StaticMaterialDef)
            foreach (var (mChamp, mSkin, rawMat, matLinks, matDescriptors) in materialObjects)
            {
                if (engine.RemainingUnknownCount == 0) break;

                var baseStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rawMat };
                if (rawMat.Contains('_'))
                {
                    foreach (string tok in rawMat.Split('_', StringSplitOptions.RemoveEmptyEntries))
                        if (tok.Length >= 3 && tok != "matcap" && tok != "inst" && tok != "mat")
                            baseStems.Add(tok);
                }

                if (submeshesBySkin.TryGetValue(mSkin, out var smList))
                    baseStems.UnionWith(smList);

                var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string baseSt in baseStems)
                {
                    string root = baseSt.EndsWith("_f1", StringComparison.OrdinalIgnoreCase) ? baseSt[..^3] : baseSt;
                    stems.Add(root);
                    stems.Add(root + "_f1");
                    stems.Add(root + "_f2");
                    stems.Add(root + "_f3");
                    stems.Add(root + "_f4");
                    stems.Add(root + "_f1_f2");
                    stems.Add(root + "_f2_f3");
                    stems.Add(root + "_empowered");
                    stems.Add(root + "_reset");
                    stems.Add("empowered_" + root);
                }

                string baseDir = $"assets/characters/{mChamp}/skins/{mSkin}/";
                foreach (ulong unk in matLinks)
                {
                    if (engine.RemainingUnknownCount == 0) break;
                    if (!engine.UnknownHashes.Contains(unk)) continue;

                    Check(engine, $"{baseDir}{mChamp}_{mSkin}_tx_cm.tex", HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                    Check(engine, $"{baseDir}{mChamp}_{mSkin}_mask_tx_cm.tex", HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                    Check(engine, $"{baseDir}{mChamp}_{mSkin}_matcap.tex", HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);

                    foreach (string st in stems)
                    {
                        if (!engine.UnknownHashes.Contains(unk)) break;
                        foreach (string desc in matDescriptors)
                        {
                            if (!engine.UnknownHashes.Contains(unk)) break;
                            foreach (string suf in TextureMapSuffixes)
                            {
                                string c1 = $"{baseDir}{mChamp}_{mSkin}_{st}{desc}{suf}";
                                if (XxHash64Ext.Hash(c1) == unk)
                                {
                                    Check(engine, c1, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                                    break;
                                }
                                string c2 = $"{baseDir}{mChamp}_{st}{desc}{suf}";
                                if (XxHash64Ext.Hash(c2) == unk)
                                {
                                    Check(engine, c2, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                                    break;
                                }
                                string c3 = $"{baseDir}{st}{desc}{suf}";
                                if (XxHash64Ext.Hash(c3) == unk)
                                {
                                    Check(engine, c3, HashGuessStrategy.BinEntry, sourceWadPath, sourceChunkHash);
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            // Phase 3: Resolve Skin Mesh Links (SkinCharacterDataProperties)
            foreach (var (obj, simpleSkin, skeleton, targetTexHashes) in skinObjects)
            {
                if (engine.RemainingUnknownCount == 0 || targetTexHashes.Count == 0) break;

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
                if (fileStem.StartsWith("jade_", StringComparison.OrdinalIgnoreCase))
                    candidateStems.Add(fileStem[5..]);
                else
                    candidateStems.Add("jade_" + fileStem);

                var champAliases = new List<string>();
                if (!string.IsNullOrEmpty(cleanChamp))
                {
                    champAliases.Add(cleanChamp);
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
                    foreach (string sm in smList) candidateStems.Add(sm);
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
            @"^(?:assets|data)/characters/(?<champ>[^/]+)/skins/(?<skin>skin\d+|base)\.bin$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly uint SkinPropertiesClassHash = Fnv1a.HashLower("SkinCharacterDataProperties");
        private static readonly uint StaticMaterialClassHash = Fnv1a.HashLower("StaticMaterialDef");
        private static readonly uint SkinMeshPropertiesHash = Fnv1a.HashLower("skinMeshProperties");
        private static readonly uint MaterialOverrideHash = Fnv1a.HashLower("materialOverride");
        private static readonly uint MaterialLinkHash = Fnv1a.HashLower("Material");
        private static readonly uint DirectTextureHash = Fnv1a.HashLower("texture");
        private static readonly uint SubmeshNameHash = Fnv1a.HashLower("submesh");
        private static readonly uint SamplerValuesHash = Fnv1a.HashLower("samplerValues");
        private static readonly uint TexturePathHash = Fnv1a.HashLower("texturePath");

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
            string skinFile = skin.Groups["skin"].Value.ToLowerInvariant();
            string assetFolder = $"assets/characters/{champ}/skins/{skinFile}";

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

            if (materialTargets.Count == 0) return;

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

                    bool hitsUnknown = false;
                    if (entry.Properties.TryGetValue(MaterialLinkHash, out BinTreeProperty materialProperty) &&
                        materialProperty is BinTreeObjectLink materialLink &&
                        materialTargets.ContainsKey(materialLink.Value))
                        hitsUnknown = true;
                    else if (entry.Properties.TryGetValue(DirectTextureHash, out BinTreeProperty textureProperty) &&
                             textureProperty is BinTreeWadChunkLink directLink &&
                             directLink.Value != 0 && engine.UnknownHashes.Contains(directLink.Value))
                        hitsUnknown = true;

                    if (hitsUnknown)
                        roles.Add(CleanRoleName(submesh.Value));
                }
            }

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
                foreach (string role in roles)
                {
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_{role}_tx_cm.tex", HashGuessStrategy.CharacterTemplate));
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_{role}_tx_cm.dds", HashGuessStrategy.CharacterTemplate));
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_{role}_tx_gm.tex", HashGuessStrategy.CharacterTemplate));
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_{role}_tx.tex", HashGuessStrategy.CharacterTemplate));
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/{stem}_{role}.dds", HashGuessStrategy.CharacterTemplate));
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/2x_{stem}_{role}_tx_cm.tex", HashGuessStrategy.CharacterTemplate));
                    candidates.Add(new HashGuessCandidate($"{assetFolder}/4x_{stem}_{role}_tx_cm.tex", HashGuessStrategy.CharacterTemplate));
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
                if (parts.Length < 6 || !parts[3].Equals("skins", StringComparison.OrdinalIgnoreCase)) continue;
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
    }
}
