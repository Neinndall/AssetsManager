using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Hashes.Guessers;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class FullUnknownsCoverageDiagnostic
    {
        public static void Run(string[] args)
        {
            string pbeRoot = @"C:\Riot Games\League of Legends (PBE)\Game";
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string unknownsPath = Path.Combine(localAppData, "AssetsManager", "hash_lab", "unknowns.game.txt");
            string hashesPath = Path.Combine(localAppData, "AssetsManager", "hashes", "hashes.game.txt");

            if (!File.Exists(unknownsPath) || !File.Exists(hashesPath))
            {
                Console.WriteLine("Missing unknowns.game.txt or hashes.game.txt");
                return;
            }

            var unknownHashes = new HashSet<ulong>();
            foreach (string line in File.ReadLines(unknownsPath))
                if (ulong.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong h))
                    unknownHashes.Add(h);

            Console.WriteLine("==================================================");
            Console.WriteLine($"   SIMULACIÓN COMPLETA DE GREPWAD EN TIEMPO REAL");
            Console.WriteLine($"   Hashes desconocidos iniciales: {unknownHashes.Count:N0}");
            Console.WriteLine("==================================================");

            var hashFile = new HashFile(HashGuessDomain.Game, hashesPath);
            var guesser = new GameHashGuesser(hashFile);
            var engine = new HashGuessEngine(HashGuessDomain.Game, unknownHashes);

            var wads = Directory.EnumerateFiles(pbeRoot, "*.wad.client", SearchOption.AllDirectories)
                .Where(w => {
                    string name = Path.GetFileName(w);
                    return name.Contains("xinzhao", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("tristana", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("poppy", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("fiora", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("orianna", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("galio", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("shyvana", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("companions", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("map22", StringComparison.OrdinalIgnoreCase) ||
                           name.Contains("global", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
            var sw = Stopwatch.StartNew();

            int totalProcessedChunks = 0;

            foreach (string wadPath in wads)
            {
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach ((ulong hash, WadChunk chunk) in wad.Chunks)
                    {
                        if (chunk.Compression == WadChunkCompression.Satellite || chunk.CompressedSize > 1_000_000) continue;

                        using var owner = wad.LoadChunkDecompressed(chunk);
                        ArraySegment<byte> data = owner.DangerousGetArray();
                        if (data.Count < 4) continue;

                        // Only process .bin and properties (PROP = 0x504F5250, PTCH = 0x48435450)
                        uint magic = BitConverter.ToUInt32(data.Array, data.Offset);
                        if (magic != 0x504F5250 && magic != 0x48435450) continue;

                        totalProcessedChunks++;
                        string relWad = Path.GetRelativePath(pbeRoot, wadPath).Replace('\\', '/');
                        guesser.GrepWad(engine, data, $"{hash:x16}.bin", relWad, hash);
                    }
                }
                catch {}
            }

            sw.Stop();

            Console.WriteLine($"\n==================================================");
            Console.WriteLine($"   RESULTADOS FINALES DE GREPWAD ({sw.Elapsed.TotalSeconds:F1} s)");
            Console.WriteLine($"   Total Chunks procesados: {totalProcessedChunks:N0}");
            Console.WriteLine($"   Hashes resueltos: {engine.Matches.Count:N0} de {unknownHashes.Count:N0}");
            Console.WriteLine($"   Hashes restantes: {engine.RemainingUnknownCount:N0}");
            Console.WriteLine("==================================================\n");

            var texMatches = engine.Matches.Values.Where(m => m.Path.EndsWith(".tex") || m.Path.EndsWith(".dds")).ToList();
            var binMatches = engine.Matches.Values.Where(m => m.Path.EndsWith(".bin")).ToList();
            var anmMatches = engine.Matches.Values.Where(m => m.Path.EndsWith(".anm")).ToList();

            Console.WriteLine($"Desglose de aciertos:");
            Console.WriteLine($"  - Texturas (.tex / .dds): {texMatches.Count:N0}");
            Console.WriteLine($"  - Binarios (.bin):        {binMatches.Count:N0}");
            Console.WriteLine($"  - Animaciones (.anm):     {anmMatches.Count:N0}");
            Console.WriteLine($"  - Otros:                  {engine.Matches.Count - texMatches.Count - binMatches.Count - anmMatches.Count:N0}");

            Console.WriteLine("\n--- MUESTRA DE TEXTURAS RESUELTAS ---");
            foreach (var m in texMatches.Take(25))
                Console.WriteLine($"  [TEX] {m.Hash:x16} = {m.Path}");

            Console.WriteLine("\n--- MUESTRA DE BINS RESUELTOS ---");
            foreach (var m in binMatches.Take(25))
                Console.WriteLine($"  [BIN] {m.Hash:x16} = {m.Path}");
        }
    }
}
