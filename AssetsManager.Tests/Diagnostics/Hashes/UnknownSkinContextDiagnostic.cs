using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Services.Hashes.Guessers.Game;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    /// <summary>
    /// Read-only context dump for GAME unknown hashes. For every unknown it prints
    /// the hosting WAD, payload kind and, for skin BINs, the referenced model and
    /// submeshes. It also inverts BIN references so each unknown shows which known
    /// skin BIN (champion/skin) and field points at it. Never persists anything.
    /// </summary>
    internal static class UnknownSkinContextDiagnostic
    {
        private static readonly uint SkinPropertiesClass = Fnv1a.HashLower("SkinCharacterDataProperties");
        private static readonly uint SkinMeshProperties = Fnv1a.HashLower("skinMeshProperties");
        private static readonly uint SimpleSkin = Fnv1a.HashLower("simpleSkin");
        private static readonly uint MaterialOverride = Fnv1a.HashLower("materialOverride");
        private static readonly uint Submesh = Fnv1a.HashLower("submesh");

        private sealed record Referrer(string Source, string Object, string Field);

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
            IReadOnlyDictionary<uint, string> binNames = LoadBinNames(hashesDirectory);
            var unknowns = new HashSet<ulong>();
            foreach (string line in File.ReadLines(unknownsPath))
                if (ulong.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash)
                    && !gamePaths.ContainsKey(hash))
                    unknowns.Add(hash);
            if (unknowns.Count == 0)
            {
                Console.WriteLine("No unresolved GAME hashes.");
                return;
            }

            var guesser = new GameHashGuesser(gameHashFile, null, _ => string.Empty);
            string[] wadPaths;
            try
            {
                wadPaths = guesser.FindWads(pbeRoot);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Could not enumerate GAME WADs: {exception.Message}");
                return;
            }

            string gameDirectory = Directory.Exists(Path.Combine(pbeRoot, "Game"))
                ? Path.Combine(pbeRoot, "Game")
                : pbeRoot;
            var locations = new Dictionary<ulong, (string Wad, long Size, string Payload)>();
            var skinIdentity = new Dictionary<ulong, List<string>>();
            var submeshes = new Dictionary<ulong, HashSet<string>>();
            var referrers = new Dictionary<ulong, List<Referrer>>();
            var stopwatch = Stopwatch.StartNew();

            foreach (string wadPath in wadPaths)
            {
                string relWad = Path.GetRelativePath(gameDirectory, wadPath).Replace('\\', '/');
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
                        bool isTarget = unknowns.Contains(pair.Key);
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

                        byte[] data = seg.Array[seg.Offset..(seg.Offset + seg.Count)];
                        if (isTarget && !locations.ContainsKey(pair.Key))
                            locations[pair.Key] = (relWad, pair.Value.UncompressedSize, Classify(data));

                        if (!IsPropertyBin(data))
                            continue;

                        BinTree tree;
                        try
                        {
                            using var stream = new MemoryStream(data, writable: false);
                            tree = new BinTree(stream);
                        }
                        catch
                        {
                            continue;
                        }

                        if (isTarget)
                            ExtractSkinIdentity(pair.Key, tree, binNames, skinIdentity, submeshes);
                        CollectReferrers(pair.Key, relWad, tree, binNames, gamePaths, unknowns, referrers);
                    }
                }
            }

            stopwatch.Stop();
            Console.WriteLine($"UNKNOWN SKIN CONTEXT ({unknowns.Count} unknowns, located {locations.Count}, {stopwatch.Elapsed:hh\\:mm\\:ss})");
            foreach (ulong hash in unknowns.OrderBy(h => h))
            {
                if (!locations.TryGetValue(hash, out var location))
                {
                    Console.WriteLine($"{hash:x16} NOT-LOCATED");
                    continue;
                }

                Console.WriteLine($"{hash:x16} {location.Payload} {location.Size} {location.Wad}");
                if (skinIdentity.TryGetValue(hash, out List<string> models))
                    foreach (string model in models.Distinct(StringComparer.OrdinalIgnoreCase).Take(5))
                        Console.WriteLine($"    model={model}");
                if (submeshes.TryGetValue(hash, out HashSet<string> meshes))
                    Console.WriteLine($"    submeshes={string.Join(",", meshes.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).Take(12))}");
                if (referrers.TryGetValue(hash, out List<Referrer> sources))
                {
                    Console.WriteLine($"    referred={sources.Count}");
                    foreach (Referrer referrer in sources.Take(6))
                        Console.WriteLine($"    from={referrer.Source} obj={referrer.Object} field={referrer.Field}");
                }
            }
        }

        private static string Classify(byte[] data)
        {
            if (data.Length == 0)
                return "empty";
            string guessed = FileTypeDetector.GuessExtension(data.AsSpan());
            if (!string.IsNullOrEmpty(guessed))
                return guessed;
            return "raw-" + Convert.ToHexString(data[..Math.Min(4, data.Length)]).ToLowerInvariant();
        }

        private static bool IsPropertyBin(byte[] data) =>
            data.Length >= 4 &&
            ((data[0] == 0x50 && data[1] == 0x52 && data[2] == 0x4F && data[3] == 0x50) ||
             (data[0] == 0x50 && data[1] == 0x54 && data[2] == 0x43 && data[3] == 0x48));

        private static void ExtractSkinIdentity(
            ulong hash,
            BinTree tree,
            IReadOnlyDictionary<uint, string> binNames,
            Dictionary<ulong, List<string>> skinIdentity,
            Dictionary<ulong, HashSet<string>> submeshes)
        {
            foreach (BinTreeObject obj in tree.Objects.Values)
            {
                if (obj.ClassHash != SkinPropertiesClass ||
                    !obj.Properties.TryGetValue(SkinMeshProperties, out BinTreeProperty meshProperty) ||
                    meshProperty is not BinTreeStruct mesh)
                    continue;

                if (mesh.Properties.TryGetValue(SimpleSkin, out BinTreeProperty skinProperty) &&
                    skinProperty is BinTreeString skin &&
                    !string.IsNullOrWhiteSpace(skin.Value))
                {
                    if (!skinIdentity.TryGetValue(hash, out List<string> models))
                        skinIdentity[hash] = models = new List<string>();
                    models.Add(skin.Value);
                }

                if (mesh.Properties.TryGetValue(MaterialOverride, out BinTreeProperty overrideProperty) &&
                    overrideProperty is BinTreeContainer overrides)
                {
                    if (!submeshes.TryGetValue(hash, out HashSet<string> meshes))
                        submeshes[hash] = meshes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (BinTreeStruct entry in overrides.Elements.OfType<BinTreeStruct>())
                        if (entry.Properties.TryGetValue(Submesh, out BinTreeProperty submeshProperty) &&
                            submeshProperty is BinTreeString submesh &&
                            !string.IsNullOrWhiteSpace(submesh.Value))
                            meshes.Add(submesh.Value);
                }

                if (!binNames.TryGetValue(obj.PathHash, out _))
                {
                    if (!skinIdentity.TryGetValue(hash, out List<string> entries))
                        skinIdentity[hash] = entries = new List<string>();
                    entries.Add($"entry:0x{obj.PathHash:x8}");
                }
            }
        }

        private static void CollectReferrers(
            ulong sourceHash,
            string relWad,
            BinTree tree,
            IReadOnlyDictionary<uint, string> binNames,
            IReadOnlyDictionary<ulong, string> gamePaths,
            HashSet<ulong> unknowns,
            Dictionary<ulong, List<Referrer>> referrers)
        {
            string source = gamePaths.TryGetValue(sourceHash, out string path) ? path : $"0x{sourceHash:x16}";
            foreach (BinTreeObject obj in tree.Objects.Values)
            {
                string objectName = binNames.TryGetValue(obj.PathHash, out string entry)
                    ? entry
                    : $"0x{obj.PathHash:x8}";
                foreach (BinTreeProperty property in Enumerate(obj.Properties.Values))
                {
                    if (property is BinTreeWadChunkLink link && unknowns.Contains(link.Value))
                    {
                        string field = binNames.TryGetValue(property.NameHash, out string fieldName)
                            ? fieldName
                            : $"0x{property.NameHash:x8}";
                        if (!referrers.TryGetValue(link.Value, out List<Referrer> sources))
                            referrers[link.Value] = sources = new List<Referrer>();
                        sources.Add(new Referrer($"{relWad}::{source}", objectName, field));
                    }
                }
            }

            foreach (var dataOverride in tree.DataOverrides)
            foreach (BinTreeProperty property in Enumerate(new[] { dataOverride.Property }))
            {
                if (property is BinTreeWadChunkLink link && unknowns.Contains(link.Value))
                {
                    string field = binNames.TryGetValue(property.NameHash, out string fieldName)
                        ? fieldName
                        : $"0x{property.NameHash:x8}";
                    if (!referrers.TryGetValue(link.Value, out List<Referrer> sources))
                        referrers[link.Value] = sources = new List<Referrer>();
                    sources.Add(new Referrer($"{relWad}::{source}", "override", field));
                }
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

        private static IReadOnlyDictionary<uint, string> LoadBinNames(string hashesDirectory)
        {
            var result = new Dictionary<uint, string>();
            foreach (string file in new[] { "hashes.binhashes.txt", "hashes.binentries.txt", "hashes.binfields.txt", "hashes.bintypes.txt" })
            {
                string path = Path.Combine(hashesDirectory, file);
                if (!File.Exists(path))
                    continue;
                foreach (string line in File.ReadLines(path))
                {
                    if (line.Length <= 9 || line[8] != ' ')
                        continue;
                    if (uint.TryParse(line.AsSpan(0, 8), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                        result.TryAdd(hash, line[9..].Trim());
                }
            }

            return result;
        }
    }
}
