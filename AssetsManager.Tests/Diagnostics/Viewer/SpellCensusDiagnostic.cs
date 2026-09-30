using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `spell-census`: every SpellObject in the installed champion WADs, with the viewer's spell availability, what each
    /// spell authors (animation, hit effect, missile) and the class of its missile movement, to explain why spells show
    /// as unsupported.
    /// </summary>
    internal static class SpellCensusDiagnostic
    {
        public static void Run(string[] args)
        {
            string hashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            var binPaths = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.game.txt")))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && line.EndsWith(".bin", StringComparison.Ordinal) && line.Contains("data/characters/", StringComparison.Ordinal) &&
                    ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    binPaths[hash] = line[(space + 1)..];
            }
            var typeNames = new Dictionary<uint, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.bintypes.txt")))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && uint.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                    typeNames.TryAdd(hash, line[(space + 1)..]);
            }

            uint spellObject = Fnv1a.HashLower("SpellObject");
            uint spellField = Fnv1a.HashLower("mSpell");
            uint missileSpec = Fnv1a.HashLower("mMissileSpec");
            uint movement = Fnv1a.HashLower("movementComponent");
            var seen = new HashSet<uint>();
            var availability = new Dictionary<string, int>();
            var authored = new Dictionary<string, int>();
            var movements = new Dictionary<string, int>();
            var unsupportedMovements = new Dictionary<string, int>();
            var reasons = new Dictionary<string, int>();
            uint buffField = Fnv1a.HashLower("mBuff");
            string final = Path.Combine(InstalledSkins.FindInstall(), @"Game\DATA\FINAL\Champions");
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client")
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                using var wad = new WadFile(wadPath);
                foreach (ulong chunk in wad.Chunks.Keys.Where(binPaths.ContainsKey))
                {
                    BinTree tree;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                        tree = new BinTree(stream);
                    }
                    catch { continue; }

                    foreach (BinTreeObject spell in tree.Objects.Values.Where(item => item.ClassHash == spellObject))
                    {
                        if (!seen.Add(spell.PathHash)) continue;

                        VfxSpellPreview preview = VfxSpellPreviewReader.Read(spell);
                        VfxSpellAvailability state = VfxSpellPreviewReader.AvailabilityOf(preview);
                        Tally(availability, state.ToString());

                        bool animation = !string.IsNullOrWhiteSpace(preview?.AnimationName);
                        bool hit = preview?.HaveHitEffect != false && (preview?.HitEffectKey != 0u || !string.IsNullOrWhiteSpace(preview?.HitEffectName));
                        string movementClass = "none";
                        if (spell.Properties.TryGetValue(spellField, out var data) && data is BinTreeStruct spellData &&
                            spellData.Properties.TryGetValue(missileSpec, out var spec) && spec is BinTreeStruct specStruct)
                        {
                            movementClass = specStruct.Properties.TryGetValue(movement, out var move) && move is BinTreeStruct moveStruct
                                ? (typeNames.TryGetValue(moveStruct.ClassHash, out string name) ? name : $"0x{moveStruct.ClassHash:x8}")
                                : "missileWithoutMovement";
                        }
                        Tally(authored, $"animation={animation} hit={hit} missile={movementClass != "none"}");
                        Tally(movements, movementClass);
                        if (state != VfxSpellAvailability.Supported)
                        {
                            Tally(unsupportedMovements, movementClass);
                            bool hasSpell = spell.Properties.ContainsKey(spellField), hasBuff = spell.Properties.ContainsKey(buffField);
                            Tally(reasons, hasSpell ? (movementClass != "none" ? "spellWithUnplayableMissile" : "spellWithoutVisualData") : hasBuff ? "buffOnly" : "scriptOnly");
                        }
                    }
                }
            }

            Console.WriteLine($"[Spells] total={seen.Count}");
            foreach (var pair in availability.OrderByDescending(pair => pair.Value)) Console.WriteLine($"[Spells] availability {pair.Key} = {pair.Value}");
            foreach (var pair in authored.OrderByDescending(pair => pair.Value)) Console.WriteLine($"[Spells] authored {pair.Key} = {pair.Value}");
            foreach (var pair in movements.OrderByDescending(pair => pair.Value)) Console.WriteLine($"[Spells] movement {pair.Key} = {pair.Value}");
            foreach (var pair in reasons.OrderByDescending(pair => pair.Value)) Console.WriteLine($"[Spells] notPlayable reason {pair.Key} = {pair.Value}");
            foreach (var pair in unsupportedMovements.OrderByDescending(pair => pair.Value)) Console.WriteLine($"[Spells] notPlayable movement {pair.Key} = {pair.Value}");
        }

        private static void Tally(Dictionary<string, int> table, string key) =>
            table[key] = table.TryGetValue(key, out int count) ? count + 1 : 1;
    }
}
