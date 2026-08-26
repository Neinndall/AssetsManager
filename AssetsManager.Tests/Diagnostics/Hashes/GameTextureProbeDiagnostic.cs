using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class GameTextureProbeDiagnostic
    {
        private static readonly Regex CharacterRegex = new(
            @"^(?:assets|data)/characters/(?<character>[^/]+)/skins/(?<skin>[^/]+)/(?<file>.+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] CanonicalSamplers =
        {
            "_tx_cm", "_tx_mask", "_tx_em", "_tx_normal", "_tx_spec", "_tx_ao",
            "_tx_dist", "_tx_alpha", "_tx_noise", "_tx_cube", "_tx_env", "_tx_flow",
            "_base_tx_cm", "_base_tx_mask", "_base_tx_em", "_base_tx_normal"
        };

        private static readonly string[] CanonicalSubmeshes =
        {
            "", "_weapon", "_weapons", "_body", "_wings", "_wing", "_recall", "_props", "_prop",
            "_flower", "_sword", "_swords", "_gun", "_guns", "_mask", "_hair", "_dragon",
            "_familiar", "_book", "_cape", "_shadow", "_blades", "_pet"
        };

        private static readonly string[] SkinsRange = {
            "base", "skin0", "skin1", "skin2", "skin3", "skin4", "skin5", "skin6", "skin7", "skin8", "skin9",
            "skin10", "skin11", "skin12", "skin13", "skin14", "skin15", "skin16", "skin17", "skin18", "skin19",
            "skin20", "skin21", "skin22", "skin23", "skin24", "skin25", "skin26", "skin27", "skin28", "skin30", "skin40",
            "skin301", "skin302", "skin303", "skin304", "skin305"
        };

        public static void Run(string[] args)
        {
            string pbeRoot = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                ?? @"C:\Riot Games\League of Legends (PBE)\Game";

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string unknownsPath = Path.Combine(localAppData, "AssetsManager", "hash_lab", "unknowns.game.txt");
            string hashesPath = Path.Combine(localAppData, "AssetsManager", "hashes", "hashes.game.txt");

            if (!File.Exists(unknownsPath) || !File.Exists(hashesPath))
            {
                Console.WriteLine("Missing unknowns.game.txt or hashes.game.txt.");
                return;
            }

            var unknownHashes = new HashSet<ulong>();
            foreach (string line in File.ReadLines(unknownsPath))
                if (ulong.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong h))
                    unknownHashes.Add(h);

            Console.WriteLine("==================================================");
            Console.WriteLine($"   TEXTURE & BIN LINK PROBE ({unknownHashes.Count:N0} unknowns)");
            Console.WriteLine("==================================================");

            // Index champion templates from known catalog
            Console.WriteLine("Indexing champion DNA templates from hashes.game.txt...");
            var templatesByChar = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadLines(hashesPath))
            {
                int space = line.IndexOf(' ');
                if (space < 0) continue;
                string path = line[(space + 1)..].Trim().ToLowerInvariant();

                Match m = CharacterRegex.Match(path);
                if (!m.Success) continue;

                string character = m.Groups["character"].Value;
                string skin = m.Groups["skin"].Value;
                string file = m.Groups["file"].Value;

                if (!templatesByChar.TryGetValue(character, out var set))
                    templatesByChar[character] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                string templated = file.Replace(skin, "{skin}", StringComparison.OrdinalIgnoreCase);
                if (templated.Length > 0 && templated.Length < 250)
                    set.Add(templated);
            }

            Console.WriteLine($"Indexed {templatesByChar.Count:N0} characters.");

            var wads = Directory.EnumerateFiles(pbeRoot, "*.wad.client", SearchOption.AllDirectories).ToList();
            Console.WriteLine($"Scanning {wads.Count:N0} WADs for .bin chunk links...");

            var solved = new Dictionary<ulong, string>();
            var stopwatch = Stopwatch.StartNew();

            foreach (string wadPath in wads)
            {
                string wadName = Path.GetFileNameWithoutExtension(wadPath);
                if (wadName.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
                    wadName = Path.GetFileNameWithoutExtension(wadName);

                string champ = wadName.ToLowerInvariant();
                var champVariants = new List<string> { champ };
                if (!champ.StartsWith("jade_")) champVariants.Add($"jade_{champ}");
                if (!champ.StartsWith("pet")) champVariants.Add($"pet{champ}");

                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        using var owner = wad.LoadChunkDecompressed(pair.Value);
                        ArraySegment<byte> seg = owner.DangerousGetArray();
                        if (seg.Count < 4) continue;

                        uint magic = BitConverter.ToUInt32(seg.Array, seg.Offset);
                        if (magic != 0x50524F50 && magic != 0x50544348) continue;

                        try
                        {
                            using var ms = new MemoryStream(seg.Array, seg.Offset, seg.Count, false);
                            var tree = new BinTree(ms);
                            var links = EnumerateChunkLinks(tree).Where(l => unknownHashes.Contains(l)).ToHashSet();
                            if (links.Count == 0) continue;

                            foreach (string character in champVariants)
                            {
                                templatesByChar.TryGetValue(character, out var templates);
                                if (templates == null && character.StartsWith("jade_"))
                                    templatesByChar.TryGetValue(character[5..], out templates);

                                foreach (string skin in SkinsRange)
                                {
                                    if (links.Count == 0) break;

                                    string skinDir = $"assets/characters/{character}/skins/{skin}";
                                    string dataSkinDir = $"data/characters/{character}/skins/{skin}";
                                    string baseName = $"{character}_{skin}";

                                    // 1. Check templates
                                    if (templates != null)
                                    {
                                        foreach (string template in templates)
                                        {
                                            if (links.Count == 0) break;
                                            string resolved = template.Replace("{skin}", skin, StringComparison.OrdinalIgnoreCase);
                                            TestCandidate($"{skinDir}/{resolved}", links, solved, unknownHashes);
                                            TestCandidate($"{dataSkinDir}/{resolved}", links, solved, unknownHashes);

                                            if (resolved.EndsWith(".tex", StringComparison.OrdinalIgnoreCase))
                                            {
                                                TestCandidate($"{skinDir}/{resolved[..^4]}.dds", links, solved, unknownHashes);
                                                TestCandidate($"{dataSkinDir}/{resolved[..^4]}.dds", links, solved, unknownHashes);
                                                TestCandidate($"{skinDir}/2x_{resolved}", links, solved, unknownHashes);
                                                TestCandidate($"{skinDir}/4x_{resolved}", links, solved, unknownHashes);
                                            }
                                            else if (resolved.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
                                            {
                                                TestCandidate($"{skinDir}/{resolved[..^4]}.tex", links, solved, unknownHashes);
                                                TestCandidate($"{dataSkinDir}/{resolved[..^4]}.tex", links, solved, unknownHashes);
                                                TestCandidate($"{skinDir}/2x_{resolved}", links, solved, unknownHashes);
                                                TestCandidate($"{skinDir}/4x_{resolved}", links, solved, unknownHashes);
                                            }
                                        }
                                    }

                                    // 2. Canonical Submeshes & Samplers
                                    foreach (string sub in CanonicalSubmeshes)
                                    {
                                        if (links.Count == 0) break;
                                        string prefix = $"{baseName}{sub}";

                                        foreach (string samp in CanonicalSamplers)
                                        {
                                            if (links.Count == 0) break;
                                            TestCandidate($"{skinDir}/{prefix}{samp}.tex", links, solved, unknownHashes);
                                            TestCandidate($"{skinDir}/{prefix}{samp}.dds", links, solved, unknownHashes);
                                            TestCandidate($"{dataSkinDir}/{prefix}{samp}.tex", links, solved, unknownHashes);
                                            TestCandidate($"{dataSkinDir}/{prefix}{samp}.dds", links, solved, unknownHashes);

                                            TestCandidate($"{skinDir}/2x_{prefix}{samp}.tex", links, solved, unknownHashes);
                                            TestCandidate($"{skinDir}/2x_{prefix}{samp}.dds", links, solved, unknownHashes);
                                            TestCandidate($"{skinDir}/4x_{prefix}{samp}.tex", links, solved, unknownHashes);
                                            TestCandidate($"{skinDir}/4x_{prefix}{samp}.dds", links, solved, unknownHashes);

                                            TestCandidate($"{skinDir}/{prefix}{samp}.skins_{character}_{skin}.tex", links, solved, unknownHashes);
                                            TestCandidate($"{skinDir}/{prefix}{samp}.skins_{character}_{skin}.dds", links, solved, unknownHashes);
                                        }

                                        TestCandidate($"{skinDir}/{prefix}.tex", links, solved, unknownHashes);
                                        TestCandidate($"{skinDir}/{prefix}.dds", links, solved, unknownHashes);
                                        TestCandidate($"{skinDir}/{prefix}.skn", links, solved, unknownHashes);
                                        TestCandidate($"{skinDir}/{prefix}.skl", links, solved, unknownHashes);
                                        TestCandidate($"{dataSkinDir}/{prefix}.skn", links, solved, unknownHashes);
                                        TestCandidate($"{dataSkinDir}/{prefix}.skl", links, solved, unknownHashes);
                                    }
                                }
                            }
                        }
                        catch {}
                    }
                }
                catch {}
            }

            stopwatch.Stop();

            Console.WriteLine($"\n==================================================");
            Console.WriteLine($"   RESULTADOS DEL PROBE ({stopwatch.ElapsedMilliseconds} ms)");
            Console.WriteLine($"   Hashes Desconocidos Resueltos: {solved.Count:N0} / {unknownHashes.Count:N0}");
            Console.WriteLine($"==================================================\n");

            foreach (var kv in solved.OrderBy(x => x.Value))
            {
                Console.WriteLine($"  [CRACKED] {kv.Key:x16} => {kv.Value}");
            }
        }

        private static void TestCandidate(string path, HashSet<ulong> links, Dictionary<ulong, string> solved, HashSet<ulong> unknowns)
        {
            string norm = path.ToLowerInvariant().Replace('\\', '/');
            ulong hash = XxHash64Ext.Hash(norm);
            if (links.Remove(hash) || unknowns.Contains(hash))
            {
                solved.TryAdd(hash, norm);
            }
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
    }
}
