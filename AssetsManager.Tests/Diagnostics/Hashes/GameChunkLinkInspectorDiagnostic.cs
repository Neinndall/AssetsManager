using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class GameChunkLinkInspectorDiagnostic
    {
        public static void Run(string[] args)
        {
            string pbeRoot = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                ?? @"C:\Riot Games\League of Legends (PBE)\Game";

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string unknownsPath = Path.Combine(localAppData, "AssetsManager", "hash_lab", "unknowns.game.txt");

            if (!File.Exists(unknownsPath))
            {
                Console.WriteLine("Missing unknowns.game.txt");
                return;
            }

            var unknownHashes = new HashSet<ulong>();
            foreach (string line in File.ReadLines(unknownsPath))
                if (ulong.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong h))
                    unknownHashes.Add(h);

            Console.WriteLine("==================================================");
            Console.WriteLine($"   INSPECTOR DE PROPIEDADES DE CHUNK LINKS ({unknownHashes.Count:N0} unknowns)");
            Console.WriteLine("==================================================");

            var wads = Directory.EnumerateFiles(pbeRoot, "*.wad.client", SearchOption.AllDirectories).ToList();
            int matchedLinks = 0;

            foreach (string wadPath in wads)
            {
                string relWad = Path.GetRelativePath(pbeRoot, wadPath).Replace('\\', '/');
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

                            foreach (var obj in tree.Objects.Values)
                            {
                                foreach (var prop in obj.Properties.Values)
                                {
                                    InspectProperty(obj, prop, unknownHashes, relWad, pair.Key, ref matchedLinks);
                                }
                            }
                        }
                        catch {}
                    }
                }
                catch {}
            }

            Console.WriteLine($"\nTotal Chunk Links Desconocidos encontrados en .BINs: {matchedLinks}");
        }

        private static void InspectProperty(BinTreeObject obj, BinTreeProperty prop, HashSet<ulong> unknowns, string relWad, ulong chunkHash, ref int matchedLinks)
        {
            if (prop == null) return;

            if (prop is BinTreeWadChunkLink link && unknowns.Contains(link.Value))
            {
                matchedLinks++;
                Console.WriteLine($"[LINK MATCH #{matchedLinks}]");
                Console.WriteLine($"  WAD:         {relWad}");
                Console.WriteLine($"  Chunk BIN:   {chunkHash:x16}");
                Console.WriteLine($"  Object Hash: {obj.PathHash:x8} (Class: {obj.ClassHash:x8})");
                Console.WriteLine($"  Prop Hash:   {link.NameHash:x8}");
                Console.WriteLine($"  Target Hash: {link.Value:x16}");
            }

            switch (prop)
            {
                case BinTreeStruct str:
                    foreach (var child in str.Properties.Values)
                        InspectProperty(obj, child, unknowns, relWad, chunkHash, ref matchedLinks);
                    break;
                case BinTreeContainer cnt:
                    foreach (var child in cnt.Elements)
                        InspectProperty(obj, child, unknowns, relWad, chunkHash, ref matchedLinks);
                    break;
                case BinTreeMap map:
                    foreach (var pair in map)
                    {
                        InspectProperty(obj, pair.Key, unknowns, relWad, chunkHash, ref matchedLinks);
                        InspectProperty(obj, pair.Value, unknowns, relWad, chunkHash, ref matchedLinks);
                    }
                    break;
                case BinTreeOptional opt when opt.Value != null:
                    InspectProperty(obj, opt.Value, unknowns, relWad, chunkHash, ref matchedLinks);
                    break;
            }
        }
    }
}
