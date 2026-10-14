using System;
using System.Linq;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class QuickHashCheck
    {
        public static void Run(string[] args)
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string unknownsPath = System.IO.Path.Combine(localAppData, "AssetsManager", "hash_lab", "unknowns.game.txt");
            var unknownHashes = new System.Collections.Generic.HashSet<ulong>();
            foreach (string line in System.IO.File.ReadLines(unknownsPath))
                if (ulong.TryParse(line.Trim(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out ulong h))
                    unknownHashes.Add(h);

            Console.WriteLine($"Cargados {unknownHashes.Count} unknowns.");

            string[] champs = {
                "aatrox", "ahri", "akali", "alistar", "ambessa", "amumu", "anivia", "annie", "aphelios", "ashe", "aurelionsol", "aurora", "azir",
                "bard", "belveth", "blitzcrank", "brand", "braum", "briar", "caitlyn", "camille", "cassiopeia", "chogath", "corki",
                "darius", "diana", "drmundo", "draven", "ekko", "elise", "evelynn", "ezreal", "fiddlesticks", "fiora", "fizz",
                "galio", "gangplank", "garen", "gnar", "gragas", "graves", "gwen", "hecarim", "heimerdinger", "hwei", "illaoi",
                "irelia", "ivern", "janna", "jarvaniv", "jax", "jayce", "jhin", "jinx", "kaisa", "kalista", "karma", "karthus",
                "kassadin", "katarina", "kayle", "kayn", "kennen", "khazix", "kindred", "kled", "kogmaw", "ksante", "leblanc",
                "leesin", "leona", "lillia", "lissandra", "lucian", "lulu", "lux", "malphite", "malzahar", "maokai", "masteryi",
                "milio", "missfortune", "mordekaiser", "morgana", "naafiri", "nami", "nasus", "nautilus", "neeko", "nidalee", "nilah",
                "nocturne", "nunu", "olaf", "orianna", "ornn", "pantheon", "poppy", "pyke", "qiyana", "quinn", "rakan", "rammus",
                "reksai", "rell", "renataglasc", "renekton", "rengar", "riven", "rumble", "ryze", "samira", "sejuani", "senna",
                "seraphine", "sett", "shaco", "shen", "shyvana", "singed", "sion", "sivir", "skarner", "smolder", "sona", "soraka",
                "swain", "sylas", "syndra", "tahmkench", "taliyah", "talon", "taric", "teemo", "thresh", "tristana", "trundle",
                "tryndamere", "twistedfate", "twitch", "udyr", "urgot", "varus", "vayne", "veigar", "velkoz", "vex", "vi", "viego",
                "viktor", "vladimir", "volibear", "warwick", "wukong", "xayah", "xerath", "xinzhao", "yasuo", "yone", "yorick",
                "yuumi", "zac", "zed", "zeri", "ziggs", "zilean", "zoe", "zyra"
            };

            string[] pets = {
                "petchibijinx", "petstyletwoleblanc", "pethammerhead", "petchibiahri", "petchibiyasuo", "petchibiyone", "petchibized",
                "petchibikaisa", "petchibileesin", "petchibigwen", "petchibisona", "petchibiannie", "petchibilux", "petchibiteemo",
                "petchibimalphite", "petchibitristana", "petchibiakali", "petchibimorgana", "petchibivi", "petchibiekko", "petchibiashe"
            };

            int matches = 0;

            void Check(string path)
            {
                string norm = path.ToLowerInvariant().Replace('\\', '/');
                ulong hash = XxHash64Ext.Hash(norm);
                if (unknownHashes.Contains(hash))
                {
                    matches++;
                    Console.WriteLine($"[CRACKED #{matches}] {hash:x16} = {norm}");
                }
            }

            // 1. Check all characters and Jade variants
            foreach (string ch in champs)
            {
                string[] aliases = { ch, $"jade_{ch}" };
                foreach (string alias in aliases)
                {
                    Check($"data/characters/{alias}/{alias}.bin");
                    Check($"data/characters/{alias}/skins/root.bin");
                    Check($"data/characters/{alias}/tiers/root.bin");

                    for (int s = 0; s <= 40; s++)
                    {
                        Check($"data/characters/{alias}/skins/skin{s}.bin");
                        Check($"data/characters/{alias}/animations/skin{s}.bin");
                        Check($"assets/characters/{alias}/skins/skin{s}/{alias}_skin{s}.skn");
                        Check($"assets/characters/{alias}/skins/skin{s}/{alias}_skin{s}.skl");
                        Check($"assets/characters/{alias}/skins/skin{s}/{alias}_skin{s}_tx_cm.tex");
                        Check($"assets/characters/{alias}/skins/skin{s}/{alias}_skin{s}_tx_cm.dds");
                        Check($"assets/characters/{alias}/skins/skin{s}/2x_{alias}_skin{s}_tx_cm.tex");
                        Check($"assets/characters/{alias}/skins/skin{s}/2x_{alias}_skin{s}_tx_cm.dds");
                    }

                    for (int s = 300; s <= 310; s++)
                    {
                        Check($"data/characters/{alias}/skins/skin{s}.bin");
                        Check($"data/characters/{alias}/animations/skin{s}.bin");
                        Check($"assets/characters/{alias}/skins/skin{s}/{alias}_skin{s}.skn");
                        Check($"assets/characters/{alias}/skins/skin{s}/{alias}_skin{s}.skl");
                        Check($"assets/characters/{alias}/skins/skin{s}/{alias}_skin{s}_tx_cm.tex");
                        Check($"assets/characters/{alias}/skins/skin{s}/{alias}_skin{s}_tx_cm.dds");
                        Check($"assets/characters/{alias}/skins/skin{s}/2x_{alias}_skin{s}_tx_cm.tex");
                        Check($"assets/characters/{alias}/skins/skin{s}/2x_{alias}_skin{s}_tx_cm.dds");
                    }
                }
            }

            // Now let's inspect the 24 cracked .bins directly inside the WADs
            string pbeRoot = @"C:\Riot Games\League of Legends (PBE)\Game";
            var wads = System.IO.Directory.EnumerateFiles(pbeRoot, "*.wad.client", System.IO.SearchOption.AllDirectories).ToList();

            var crackedBins = new System.Collections.Generic.HashSet<ulong> {
                0x3078b99d227ac2c3, 0xf8dbbe28c64aecca, 0xfd33f38dfea3cc8b, 0xc2514f010cdc83f4, 0x0840b6f2d6a41844,
                0x88d15dcf5630f5b6, 0x6b56a961418b1628, 0xdc37c4cfd743caf7, 0x6bfe9b8eeaf4fdb4, 0x8a5653e9a7820598,
                0x211fd5e1606483ca, 0x5be1a47be3fcfc81, 0x7c52fed541e0a99f, 0xf81801eb1f1d8ff4, 0xdad2f43e7deda2c2,
                0xf2fa400d2c047689, 0x165dae3e8077c9a3, 0x4ef9acbecf5f9ec4, 0xe3edad6cbbff4507, 0x5c12f53676ece5d4,
                0xef177a39ab532b71, 0x3a0ca5cda680cfa9, 0x52ffe084ea321793, 0x028b5488d9259258
            };

            Console.WriteLine("\n--- EXTRAYENDO ENLACES Y TEXTURAS DE LOS 24 BINS CRACKEADOS ---");
            int texturesFound = 0;

            foreach (string wadPath in wads)
            {
                try
                {
                    using var wad = new LeagueToolkit.Core.Wad.WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (!crackedBins.Contains(pair.Key)) continue;

                        using var owner = wad.LoadChunkDecompressed(pair.Value);
                        var seg = owner.DangerousGetArray();
                        using var ms = new System.IO.MemoryStream(seg.Array, seg.Offset, seg.Count, false);
                        var tree = new LeagueToolkit.Core.Meta.BinTree(ms);

                        // Look at all objects and strings
                        foreach (var obj in tree.Objects.Values)
                        {
                            foreach (var prop in obj.Properties.Values)
                            {
                                foreach (var subProp in EnumerateAll(prop))
                                {
                                    if (subProp is LeagueToolkit.Core.Meta.Properties.BinTreeWadChunkLink link && link.Value != 0)
                                    {
                                        if (unknownHashes.Contains(link.Value))
                                        {
                                            texturesFound++;
                                            Console.WriteLine($"  [LINK ENCONTRADO EN BIN {pair.Key:x16}] Prop: {link.NameHash:x8} -> Target Chunk: {link.Value:x16}");
                                        }
                                    }
                                    else if (subProp is LeagueToolkit.Core.Meta.Properties.BinTreeString str && !string.IsNullOrEmpty(str.Value))
                                    {
                                        Check(str.Value);
                                        Check($"assets/{str.Value}");
                                        Check($"data/{str.Value}");
                                    }
                                }
                            }
                        }
                    }
                }
                catch {}
            }

            uint[] propHashes = { 0x3c6468f4, 0xb35135fa, 0x089aff69, 0xa24d4513 };
            var words = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string hashesPath = System.IO.Path.Combine(localAppData, "AssetsManager", "hashes", "hashes.game.txt");
            foreach (var line in System.IO.File.ReadLines(hashesPath))
            {
                int sp = line.IndexOf(' ');
                if (sp < 0) continue;
                string p = line[(sp + 1)..];
                foreach (var token in p.Split(new[] { '/', '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (token.Length >= 2 && token.Length <= 32)
                        words.Add(token);
                }
            }

            Console.WriteLine($"Vocabulario cargado: {words.Count} palabras.");

            foreach (var ph in propHashes)
            {
                foreach (var w in words)
                {
                    if (LeagueToolkit.Hashing.Fnv1a.HashLower(w) == ph)
                        Console.WriteLine($"[PROP DESCIFRADA] 0x{ph:x8} = {w}");
                }
            }
        }

        private static System.Collections.Generic.IEnumerable<LeagueToolkit.Core.Meta.BinTreeProperty> EnumerateAll(LeagueToolkit.Core.Meta.BinTreeProperty prop)
        {
            if (prop == null) yield break;
            yield return prop;
            if (prop is LeagueToolkit.Core.Meta.Properties.BinTreeStruct st)
                foreach (var c in st.Properties.Values)
                foreach (var d in EnumerateAll(c)) yield return d;
            else if (prop is LeagueToolkit.Core.Meta.Properties.BinTreeContainer cnt)
                foreach (var c in cnt.Elements)
                foreach (var d in EnumerateAll(c)) yield return d;
            else if (prop is LeagueToolkit.Core.Meta.Properties.BinTreeMap map)
                foreach (var p in map)
                {
                    foreach (var d in EnumerateAll(p.Key)) yield return d;
                    foreach (var d in EnumerateAll(p.Value)) yield return d;
                }
            else if (prop is LeagueToolkit.Core.Meta.Properties.BinTreeOptional opt && opt.Value != null)
                foreach (var d in EnumerateAll(opt.Value)) yield return d;
        }
    }
}
