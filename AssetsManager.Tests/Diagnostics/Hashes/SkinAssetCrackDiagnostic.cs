using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    /// <summary>
    /// Read-only crack probe for skin textures referenced by known skin BINs.
    /// For every unknown texture reached through a skin material override it builds
    /// submesh-role candidates (stem + submesh + _tx_cm) and reports hits without
    /// persisting anything. Used to validate patterns before production changes.
    /// </summary>
    internal static class SkinAssetCrackDiagnostic
    {
        private static readonly uint SkinPropertiesClass = Fnv1a.HashLower("SkinCharacterDataProperties");
        private static readonly uint StaticMaterialClass = Fnv1a.HashLower("StaticMaterialDef");
        private static readonly uint SkinMeshProperties = Fnv1a.HashLower("skinMeshProperties");
        private static readonly uint MaterialOverride = Fnv1a.HashLower("materialOverride");
        private static readonly uint Material = Fnv1a.HashLower("Material");
        private static readonly uint TextureOverride = Fnv1a.HashLower("texture");
        private static readonly uint Submesh = Fnv1a.HashLower("submesh");

        private static void AddRef(
            Dictionary<ulong, List<OverrideRef>> textureRefs,
            ulong target,
            string skinBin,
            string submesh)
        {
            if (!textureRefs.TryGetValue(target, out List<OverrideRef> sources))
                textureRefs[target] = sources = new List<OverrideRef>();
            if (sources.Count < 8)
                sources.Add(new OverrideRef(skinBin, submesh));
        }
        private static readonly uint SamplerValues = Fnv1a.HashLower("samplerValues");
        private static readonly uint TextureName = Fnv1a.HashLower("textureName");
        private static readonly uint TexturePath = Fnv1a.HashLower("texturePath");

        private sealed record OverrideRef(string SkinBin, string Submesh);

        private static readonly uint AnimationFileHash = Fnv1a.HashLower("mAnimationFilePath");
        private static readonly Regex SkinNumberRegex = new(@"skin(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private sealed record AnimBin(string Bin, List<string> KnownAnims, List<ulong> UnknownAnims);

        public static void Run(string[] args)
        {
            string pbeRoot = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                ?? @"C:\Riot Games\League of Legends (PBE)";
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string hashesDirectory = Path.Combine(localAppData, "AssetsManager", "hashes");
            string unknownsPath = Path.Combine(localAppData, "AssetsManager", "hash_lab", "unknowns.game.txt");
            string gameHashesPath = Path.Combine(hashesDirectory, "hashes.game.txt");
            if (!File.Exists(gameHashesPath) || !File.Exists(unknownsPath))
            {
                Console.WriteLine("Missing hashes.game.txt or unknowns.game.txt.");
                return;
            }

            var gameHashFile = new HashFile(HashGuessDomain.Game, gameHashesPath);
            IReadOnlyDictionary<ulong, string> gamePaths = gameHashFile.Load();
            var unknowns = new HashSet<ulong>();
            foreach (string line in File.ReadLines(unknownsPath))
                if (ulong.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash)
                    && !gamePaths.ContainsKey(hash))
                    unknowns.Add(hash);

            var guesser = new GameHashGuesser(gameHashFile, null, _ => string.Empty);
            string[] wadPaths = guesser.FindWads(pbeRoot);
            string gameDirectory = Directory.Exists(Path.Combine(pbeRoot, "Game"))
                ? Path.Combine(pbeRoot, "Game")
                : pbeRoot;

            // Index needed chunks only: unknown textures + skin BINs parsed on demand per WAD.
            var wadIndex = new Dictionary<string, WadFile>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Phase 1: collect (unknown texture -> skin BINs + submeshes) via material overrides.
                var textureRefs = new Dictionary<ulong, List<OverrideRef>>();
                var skinBins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var animBins = new List<AnimBin>();
                foreach (string wadPath in wadPaths)
                {
                    WadFile wad;
                    try
                    {
                        wad = new WadFile(wadPath);
                    }
                    catch
                    {
                        continue;
                    }

                    using (wad)
                    {
                        foreach (var pair in wad.Chunks)
                        {
                            if (!gamePaths.TryGetValue(pair.Key, out string logical) ||
                                !logical.StartsWith("data/characters/", StringComparison.OrdinalIgnoreCase) ||
                                !logical.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                                continue;

                            ArraySegment<byte> seg;
                            try
                            {
                                using var owner = wad.LoadChunkDecompressed(pair.Value);
                                seg = owner.DangerousGetArray();
                            }
                            catch
                            {
                                continue;
                            }

                            if (!IsPropertyBin(seg))
                                continue;

                            BinTree tree;
                            try
                            {
                                using var stream = new MemoryStream(seg.Array, seg.Offset, seg.Count, writable: false);
                                tree = new BinTree(stream);
                            }
                            catch
                            {
                                continue;
                            }

                            if (logical.Contains("/animations/", StringComparison.OrdinalIgnoreCase))
                                CollectAnimLinks(logical, tree, gamePaths, unknowns, animBins);

                            var materials = new Dictionary<uint, List<ulong>>();
                            foreach (BinTreeObject material in tree.Objects.Values)
                            {
                                if (material.ClassHash != StaticMaterialClass ||
                                    !material.Properties.TryGetValue(SamplerValues, out BinTreeProperty samplersProperty) ||
                                    samplersProperty is not BinTreeContainer samplers)
                                    continue;

                                foreach (BinTreeStruct sampler in samplers.Elements.OfType<BinTreeStruct>())
                                {
                                    if (sampler.Properties.TryGetValue(TexturePath, out BinTreeProperty pathProperty) &&
                                        pathProperty is BinTreeWadChunkLink link &&
                                        unknowns.Contains(link.Value))
                                    {
                                        if (!materials.TryGetValue(material.PathHash, out List<ulong> targets))
                                            materials[material.PathHash] = targets = new List<ulong>();
                                        targets.Add(link.Value);
                                    }
                                }
                            }

                            if (materials.Count == 0)
                                continue;

                            foreach (BinTreeObject obj in tree.Objects.Values)
                            {
                                if (obj.ClassHash != SkinPropertiesClass ||
                                    !obj.Properties.TryGetValue(SkinMeshProperties, out BinTreeProperty meshProperty) ||
                                    meshProperty is not BinTreeStruct mesh ||
                                    !mesh.Properties.TryGetValue(MaterialOverride, out BinTreeProperty overrideProperty) ||
                                    overrideProperty is not BinTreeContainer overrides)
                                    continue;

                                foreach (BinTreeStruct entry in overrides.Elements.OfType<BinTreeStruct>())
                                {
                                    if (!entry.Properties.TryGetValue(Submesh, out BinTreeProperty submeshProperty) ||
                                        submeshProperty is not BinTreeString submesh ||
                                        string.IsNullOrWhiteSpace(submesh.Value))
                                        continue;

                                    skinBins.Add(logical);
                                    if (entry.Properties.TryGetValue(Material, out BinTreeProperty materialProperty) &&
                                        materialProperty is BinTreeObjectLink materialLink &&
                                        materials.TryGetValue(materialLink.Value, out List<ulong> targets))
                                    {
                                        foreach (ulong target in targets)
                                            AddRef(textureRefs, target, logical, submesh.Value);
                                    }

                                    // Direct override texture links (no material involved).
                                    if (entry.Properties.TryGetValue(TextureOverride, out BinTreeProperty textureProperty) &&
                                        textureProperty is BinTreeWadChunkLink directLink &&
                                        unknowns.Contains(directLink.Value))
                                        AddRef(textureRefs, directLink.Value, logical, submesh.Value);
                                }
                            }
                        }
                    }
                }

                Console.WriteLine($"SKIN ASSET CRACK PROBE ({unknowns.Count} unknowns)");
                Console.WriteLine($"  skin BINs with overrides: {skinBins.Count}");
                Console.WriteLine($"  unknown textures referenced: {textureRefs.Count}");
                Console.WriteLine($"  animation BINs with siblings: {animBins.Count}");

                // Phase 2: learn file stems per skin folder from known textures, then try candidates.
                var stems = LearnStems(gamePaths);
                int hits = 0;
                int tried = 0;
                var hitSamples = new List<string>();
                foreach (var pair in textureRefs.OrderBy(p => p.Key))
                {
                    foreach (OverrideRef reference in pair.Value)
                    {
                        if (!TryParseSkinBin(reference.SkinBin, out string folder, out string skinToken))
                            continue;
                        if (!stems.TryGetValue(folder + "|" + skinToken, out List<string> folderStems))
                            folderStems = new List<string> { DefaultStem(folder, skinToken) };
                        foreach (string stem in folderStems.Distinct(StringComparer.OrdinalIgnoreCase))
                        foreach (string candidate in BuildCandidates(folder, stem, reference.Submesh))
                        {
                            tried++;
                            if (XxHash64Ext.Hash(candidate) == pair.Key)
                            {
                                hits++;
                                if (hitSamples.Count < 40)
                                    hitSamples.Add($"{pair.Key:x16} = {candidate}");
                                break;
                            }
                        }

                        if (hits > 0 && hitSamples.Count >= 40)
                            break;
                    }

                    if (hitSamples.Count >= 40)
                        break;
                }

                Console.WriteLine($"  candidates tried: {tried}");
                Console.WriteLine($"  HITS: {hits}");
                foreach (string sample in hitSamples)
                    Console.WriteLine($"    {sample}");

                // Phase 2b: sibling skin-swap for unknown animations.
                int animHits = 0;
                int animTried = 0;
                var animSamples = new List<string>();
                foreach (AnimBin animBin in animBins)
                {
                    string file = Path.GetFileNameWithoutExtension(animBin.Bin);
                    Match skinMatch = SkinNumberRegex.Match(file);
                    string skinNum = skinMatch.Success ? skinMatch.Groups[1].Value : null;
                    foreach (ulong target in animBin.UnknownAnims)
                    {
                        foreach (string sibling in animBin.KnownAnims)
                        {
                            foreach (string candidate in BuildAnimCandidates(sibling, skinNum))
                            {
                                animTried++;
                                if (XxHash64Ext.Hash(candidate) == target)
                                {
                                    animHits++;
                                    if (animSamples.Count < 40)
                                        animSamples.Add($"{target:x16} = {candidate}");
                                    break;
                                }
                            }
                        }
                    }
                }

                Console.WriteLine($"  anim candidates tried: {animTried}");
                Console.WriteLine($"  ANIM HITS: {animHits}");
                foreach (string sample in animSamples)
                    Console.WriteLine($"    {sample}");

                // Phase 2c: sibling action transfer for unknown animations.
                int actionHits = 0;
                int actionTried = 0;
                var actionSamples = new List<string>();
                var actionIndex = BuildActionIndex(animBins, gamePaths);
                foreach (AnimBin animBin in animBins)
                {
                    foreach (ulong target in animBin.UnknownAnims)
                    {
                        foreach (string candidate in BuildActionCandidates(animBin, target, actionIndex))
                        {
                            actionTried++;
                            if (XxHash64Ext.Hash(candidate) == target)
                            {
                                actionHits++;
                                if (actionSamples.Count < 40)
                                    actionSamples.Add($"{target:x16} = {candidate}");
                                break;
                            }
                        }
                    }
                }

                Console.WriteLine($"  action candidates tried: {actionTried}");
                Console.WriteLine($"  ACTION HITS: {actionHits}");
                foreach (string sample in actionSamples)
                    Console.WriteLine($"    {sample}");
            }
            finally
            {
                foreach (WadFile wad in wadIndex.Values)
                    wad.Dispose();
            }
        }

        private static void CollectAnimLinks(
            string logical,
            BinTree tree,
            IReadOnlyDictionary<ulong, string> gamePaths,
            HashSet<ulong> unknowns,
            List<AnimBin> animBins)
        {
            var known = new List<string>();
            var unknown = new List<ulong>();
            foreach (BinTreeObject obj in tree.Objects.Values)
            foreach (BinTreeProperty property in Enumerate(obj.Properties.Values))
            {
                if (property.NameHash != AnimationFileHash ||
                    property is not BinTreeWadChunkLink link ||
                    link.Value == 0)
                    continue;
                if (unknowns.Contains(link.Value))
                {
                    if (!unknown.Contains(link.Value))
                        unknown.Add(link.Value);
                }
                else if (gamePaths.TryGetValue(link.Value, out string animPath) &&
                         animPath.EndsWith(".anm", StringComparison.OrdinalIgnoreCase) &&
                         !known.Contains(animPath, StringComparer.OrdinalIgnoreCase))
                    known.Add(animPath);
            }

            if (unknown.Count > 0 && known.Count > 0)
                animBins.Add(new AnimBin(logical, known, unknown));
        }

        private static IEnumerable<string> BuildAnimCandidates(string sibling, string skinNum)
        {
            if (skinNum == null || !SkinNumberRegex.IsMatch(sibling))
                yield break;
            if (int.TryParse(skinNum, out int number))
            {
                string plain = SkinNumberRegex.Replace(sibling, "skin" + number);
                if (!plain.Equals(sibling, StringComparison.OrdinalIgnoreCase))
                    yield return plain;
                string padded = SkinNumberRegex.Replace(sibling, "skin" + number.ToString("D2", CultureInfo.InvariantCulture));
                if (!padded.Equals(sibling, StringComparison.OrdinalIgnoreCase) &&
                    !padded.Equals(plain, StringComparison.OrdinalIgnoreCase))
                    yield return padded;
            }
        }

        private static IEnumerable<BinTreeProperty> Enumerate(IEnumerable<BinTreeProperty> properties)
        {
            foreach (BinTreeProperty property in properties)
            {
                if (property is null)
                    continue;
                yield return property;
                IEnumerable<BinTreeProperty> children = property switch
                {
                    BinTreeStruct structure => structure.Properties.Values,
                    BinTreeOptional optional when optional.Value is not null => new[] { optional.Value },
                    BinTreeContainer container => container.Elements,
                    BinTreeMap map => map.SelectMany(pair => new[] { pair.Key, pair.Value }),
                    _ => Array.Empty<BinTreeProperty>()
                };
                foreach (BinTreeProperty child in children)
                foreach (BinTreeProperty nested in Enumerate(new[] { child }))
                    yield return nested;
            }
        }

        private sealed record ActionIndex(
            Dictionary<string, HashSet<string>> DirStems,
            Dictionary<string, HashSet<string>> ChampActions);

        private static ActionIndex BuildActionIndex(
            List<AnimBin> animBins,
            IReadOnlyDictionary<ulong, string> gamePaths)
        {
            var dirStems = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var champActions = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var seenDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AnimBin animBin in animBins)
            foreach (string sibling in animBin.KnownAnims)
            {
                string dir = sibling[..(sibling.LastIndexOf('/') + 1)];
                string baseName = sibling[(sibling.LastIndexOf('/') + 1)..^".anm".Length];
                if (!seenDirs.Add(dir))
                    continue;
                if (!dirStems.TryGetValue(dir, out HashSet<string> stems))
                    dirStems[dir] = stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int prefixCut = baseName.LastIndexOf('_');
                if (prefixCut > 0)
                    stems.Add(baseName[..prefixCut]);
            }

            foreach (string path in gamePaths.Values)
            {
                if (!path.EndsWith(".anm", StringComparison.OrdinalIgnoreCase))
                    continue;
                string[] parts = path.Replace('\\', '/').Split('/');
                int charIndex = Array.FindIndex(parts, p => p.Equals("characters", StringComparison.OrdinalIgnoreCase));
                if (charIndex < 0 || charIndex + 1 >= parts.Length)
                    continue;
                string champ = parts[charIndex + 1].ToLowerInvariant();
                string baseName = parts[^1][..^".anm".Length];
                if (!champActions.TryGetValue(champ, out HashSet<string> actions))
                    champActions[champ] = actions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                actions.Add(baseName);
                string stripped = StripChampSkinPrefix(baseName, champ);
                if (!stripped.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                    actions.Add(stripped);
            }

            return new ActionIndex(dirStems, champActions);
        }

        private static string StripChampSkinPrefix(string baseName, string champ)
        {
            string work = baseName;
            if (work.StartsWith(champ + "_", StringComparison.OrdinalIgnoreCase))
                work = work[(champ.Length + 1)..];
            work = SkinNumberRegex.Replace(work, "").Trim('_');
            return work.Length == 0 ? baseName : work;
        }

        private static IEnumerable<string> BuildActionCandidates(
            AnimBin animBin,
            ulong target,
            ActionIndex index)
        {
            string[] parts = animBin.Bin.Replace('\\', '/').Split('/');
            int charIndex = Array.FindIndex(parts, p => p.Equals("characters", StringComparison.OrdinalIgnoreCase));
            string champ = charIndex >= 0 && charIndex + 1 < parts.Length
                ? parts[charIndex + 1].ToLowerInvariant()
                : null;
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string sibling in animBin.KnownAnims)
                dirs.Add(sibling[..(sibling.LastIndexOf('/') + 1)]);
            if (!index.ChampActions.TryGetValue(champ ?? string.Empty, out HashSet<string> actions))
                actions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string dir in dirs)
            {
                index.DirStems.TryGetValue(dir, out HashSet<string> stems);
                foreach (string action in actions)
                {
                    yield return $"{dir}{action}.anm";
                    if (stems == null)
                        continue;
                    foreach (string stem in stems)
                        yield return $"{dir}{stem}_{action}.anm";
                }
            }
        }

        private static Dictionary<string, List<string>> LearnStems(IReadOnlyDictionary<ulong, string> gamePaths)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in gamePaths.Values)
            {
                if (!path.StartsWith("assets/characters/", StringComparison.OrdinalIgnoreCase))
                    continue;
                string[] parts = path.Replace('\\', '/').Split('/');
                // assets/characters/<folder>/skins/<skinDir>/<file>
                if (parts.Length < 6 || !parts[3].Equals("skins", StringComparison.OrdinalIgnoreCase))
                    continue;
                string file = parts[^1];
                string stem = file;
                foreach (string suffix in new[] { "_tx_cm.tex", "_tx_cm.dds", ".tex", ".dds" })
                {
                    if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        stem = stem[..^suffix.Length];
                        break;
                    }
                }

                if (stem.Contains("_tx_cm", StringComparison.OrdinalIgnoreCase))
                    stem = stem[..stem.LastIndexOf("_tx_cm", StringComparison.OrdinalIgnoreCase)];
                string key = $"assets/characters/{parts[2]}/skins/{parts[4]}|{parts[4]}";
                if (!result.TryGetValue(key, out List<string> stems))
                    result[key] = stems = new List<string>();
                if (!stems.Contains(stem, StringComparer.OrdinalIgnoreCase))
                    stems.Add(stem);
            }

            return result;
        }

        private static bool TryParseSkinBin(string skinBin, out string folder, out string skinToken)
        {
            folder = null;
            skinToken = null;
            string[] parts = skinBin.Replace('\\', '/').Split('/');
            // data/characters/<folder>/skins/<skinFile>.bin
            if (parts.Length < 5 || !parts[3].Equals("skins", StringComparison.OrdinalIgnoreCase))
                return false;
            folder = $"assets/characters/{parts[2]}/skins/{Path.GetFileNameWithoutExtension(parts[4])}";
            skinToken = Path.GetFileNameWithoutExtension(parts[4]);
            return true;
        }

        private static string DefaultStem(string folder, string skinToken)
        {
            // assets/characters/janna/skins/skin70 -> janna_skin70
            string[] parts = folder.Split('/');
            string character = parts.Length >= 3 ? parts[2] : "unknown";
            if (character.StartsWith("jade_", StringComparison.OrdinalIgnoreCase))
                return $"{character}_{skinToken}";
            return $"{character}_{skinToken}";
        }

        private static IEnumerable<string> BuildCandidates(string folder, string stem, string submesh)
        {
            string clean = new string(submesh.TrimEnd('\0').Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (!string.IsNullOrEmpty(clean))
            {
                yield return $"{folder}/{stem}_{clean}_tx_cm.tex";
                yield return $"{folder}/{stem}_{clean}_tx_cm.dds";
                yield return $"{folder}/2x_{stem}_{clean}_tx_cm.tex";
                yield return $"{folder}/4x_{stem}_{clean}_tx_cm.tex";
            }

            yield return $"{folder}/{stem}_tx_cm.tex";
        }

        private static bool IsPropertyBin(ArraySegment<byte> seg) =>
            seg.Count >= 4 &&
            ((seg.Array[seg.Offset] == 0x50 && seg.Array[seg.Offset + 1] == 0x52 && seg.Array[seg.Offset + 2] == 0x4F && seg.Array[seg.Offset + 3] == 0x50) ||
             (seg.Array[seg.Offset] == 0x50 && seg.Array[seg.Offset + 1] == 0x54 && seg.Array[seg.Offset + 2] == 0x43 && seg.Array[seg.Offset + 3] == 0x48));
    }
}
