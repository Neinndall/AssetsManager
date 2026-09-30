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
            // `spell-census bones <Champion>`: bind-pose position of the champion's hand joints, to read which side is its right.
            if (args.Length >= 2 && args[0] == "bones")
            {
                string champion = args[1];
                string root = Path.Combine(InstalledSkins.FindInstall(), @"Game\DATA\FINAL\Champions");
                using var championWad = new WadFile(Path.Combine(root, $"{champion}.wad.client"));
                string skeletonPath = $"assets/characters/{champion.ToLowerInvariant()}/skins/base/{champion.ToLowerInvariant()}.skl";
                using var rigData = championWad.LoadChunkDecompressed(XxHash64Ext.Hash(skeletonPath));
                using var rigStream = new MemoryStream(rigData.Span.ToArray(), writable: false);
                var rig = new LeagueToolkit.Core.Animation.RigResource(rigStream);
                foreach (var joint in rig.Joints.Where(joint => joint.Name.Contains("hand", StringComparison.OrdinalIgnoreCase) || joint.Name.Equals("Root", StringComparison.OrdinalIgnoreCase)))
                {
                    System.Numerics.Matrix4x4.Invert(joint.InverseBindTransform, out var bind);
                    Console.WriteLine($"[Bone] {joint.Name} {bind.Translation} zAxis=<{bind.M31:0.##}, {bind.M32:0.##}, {bind.M33:0.##}>");
                }
                return;
            }

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

            var entryNames = new Dictionary<uint, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.binentries.txt")))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && uint.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                    entryNames.TryAdd(hash, line[(space + 1)..]);
            }

            var fieldNames = new Dictionary<uint, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.binfields.txt")))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && uint.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                    fieldNames.TryAdd(hash, line[(space + 1)..]);
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
            var ignored = new Dictionary<string, int>();
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
                            if (Environment.GetEnvironmentVariable("SPELL_CENSUS_MOVES") == "1" && move is BinTreeStruct dumped &&
                                VfxSpellPreviewReader.UnplayableReasonOf(preview) == VfxSpellUnplayableReason.UnsupportedMissile)
                            {
                                string entry = entryNames.TryGetValue(spell.PathHash, out string known) ? known : $"0x{spell.PathHash:x8}";
                                Console.WriteLine($"[Move] {entry} {movementClass} {FormatStruct(dumped, typeNames, fieldNames)}");
                            }
                        }
                        string spellEntry = entryNames.TryGetValue(spell.PathHash, out string entryName) ? entryName : $"0x{spell.PathHash:x8}";
                        string nameFilter = Environment.GetEnvironmentVariable("SPELL_CENSUS_NAME");
                        if (!string.IsNullOrEmpty(nameFilter) && spellEntry.Contains(nameFilter, StringComparison.OrdinalIgnoreCase))
                            Console.WriteLine($"[Dump] {spellEntry} {string.Join(" ", spell.Properties.Keys.Select(key => fieldNames.TryGetValue(key, out string f) ? f : $"0x{key:x8}"))} :: " +
                                              (spell.Properties.TryGetValue(spellField, out var dumpData) && dumpData is BinTreeStruct dumpStruct ? FormatStruct(dumpStruct, typeNames, fieldNames) : ""));
                        if (state == VfxSpellAvailability.Supported && spell.Properties.TryGetValue(spellField, out var playable) && playable is BinTreeStruct playableSpell)
                        {
                            foreach (string field in new[] { "mParticleStartOffset", "mMissileEffectEnemyKey", "mMissileEffectEnemyName", "mResourceResolvers" })
                                if (playableSpell.Properties.ContainsKey(Fnv1a.HashLower(field))) Tally(ignored, $"mSpell.{field}");
                            if (playableSpell.Properties.TryGetValue(missileSpec, out var playableSpec) && playableSpec is BinTreeStruct playableSpecStruct)
                                foreach (string field in new[] { "heightSolver", "verticalFacing", "behaviors", "missileGroupSpawners", "visibilityComponent" })
                                    if (playableSpecStruct.Properties.TryGetValue(Fnv1a.HashLower(field), out var value))
                                    {
                                        Tally(ignored, $"mMissileSpec.{field}" + (value is BinTreeStruct typed && typeNames.TryGetValue(typed.ClassHash, out string type) ? $" ({type})" : ""));
                                        if (field == "heightSolver" && Environment.GetEnvironmentVariable("SPELL_CENSUS_HEIGHT") == "1" && value is BinTreeStruct solver)
                                            Console.WriteLine($"[Height] {(typeNames.TryGetValue(solver.ClassHash, out string solverType) ? solverType : "?")} {spellEntry} {FormatStruct(solver, typeNames, fieldNames)}");
                                    }
                        }
                        Tally(authored, $"animation={animation} hit={hit} missile={movementClass != "none"}");
                        Tally(movements, movementClass);
                        if (state != VfxSpellAvailability.Supported)
                        {
                            Tally(unsupportedMovements, movementClass);
                            Tally(reasons, VfxSpellPreviewReader.UnplayableReasonOf(preview).ToString());
                            if (Environment.GetEnvironmentVariable("SPELL_CENSUS_DUMP") == "1")
                                Console.WriteLine($"[Spells] unplayable {VfxSpellPreviewReader.UnplayableReasonOf(preview)} {(entryNames.TryGetValue(spell.PathHash, out string entry) ? entry : $"0x{spell.PathHash:x8}")}");
                        }
                    }
                }
            }

            Console.WriteLine($"[Spells] total={seen.Count}");
            foreach (var pair in availability.OrderByDescending(pair => pair.Value)) Console.WriteLine($"[Spells] availability {pair.Key} = {pair.Value}");
            foreach (var pair in authored.OrderByDescending(pair => pair.Value)) Console.WriteLine($"[Spells] authored {pair.Key} = {pair.Value}");
            foreach (var pair in movements.OrderByDescending(pair => pair.Value)) Console.WriteLine($"[Spells] movement {pair.Key} = {pair.Value}");
            foreach (var pair in reasons.OrderByDescending(pair => pair.Value)) Console.WriteLine($"[Spells] notPlayable reason {pair.Key} = {pair.Value}");
            foreach (var pair in ignored.OrderByDescending(pair => pair.Value)) Console.WriteLine($"[Spells] playable ignores {pair.Key} = {pair.Value}");
            foreach (var pair in unsupportedMovements.OrderByDescending(pair => pair.Value)) Console.WriteLine($"[Spells] notPlayable movement {pair.Key} = {pair.Value}");
        }

        private static string FormatStruct(BinTreeStruct value, Dictionary<uint, string> types, Dictionary<uint, string> fields)
        {
            var parts = new List<string>();
            foreach (var pair in value.Properties)
            {
                string name = fields.TryGetValue(pair.Key, out string known) ? known : $"0x{pair.Key:x8}";
                BinTreeProperty property = pair.Value is BinTreeOptional optional ? optional.Value : pair.Value;
                string text = property switch
                {
                    BinTreeF32 f => f.Value.ToString(CultureInfo.InvariantCulture),
                    BinTreeBool b => b.Value.ToString(),
                    BinTreeString str => str.Value,
                    BinTreeVector3 v => $"({v.Value.X.ToString(CultureInfo.InvariantCulture)},{v.Value.Y.ToString(CultureInfo.InvariantCulture)},{v.Value.Z.ToString(CultureInfo.InvariantCulture)})",
                    BinTreeStruct nested => $"{(types.TryGetValue(nested.ClassHash, out string type) ? type : $"0x{nested.ClassHash:x8}")}{{{FormatStruct(nested, types, fields)}}}",
                    BinTreeContainer list => $"[{list.Elements.Count}]",
                    null => "null",
                    _ => property.GetType().Name
                };
                parts.Add($"{name}={text}");
            }
            return string.Join(" ", parts);
        }

        private static void Tally(Dictionary<string, int> table, string key) =>
            table[key] = table.TryGetValue(key, out int count) ? count + 1 : 1;
    }
}
