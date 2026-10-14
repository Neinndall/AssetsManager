using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Services.Hashes;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class LcuBinaryHashScanProbe
    {
        private static readonly (uint Hash, string Name)[] Targets =
        {
            (0x7ec8e5ed, "UiBehavior"),
            (0xc7a79d2b, "GameScreenContainerBase"),
            (0x65c802be, "IGameScreenNode"),
            (0xe75836d4, "GameEntityTemplateLocatorPreview"),
            (0xde5dac9e, "GameEntityTemplateProxyLink"),
            (0x174c7096, "UiComponent"),
        };

        public static void Run(string root)
        {
            Console.WriteLine("=== LCU BINARY HASH SCAN (binarios del cliente) ===");
            string gameDir = Path.Combine(root, "Game");
            var files = new List<string>();
            files.AddRange(Directory.EnumerateFiles(gameDir, "*.*", SearchOption.AllDirectories)
                .Where(f => IsBinary(f)));
            if (Directory.Exists(root))
                files.AddRange(Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => IsBinary(f)));
            string[] scanFiles = files.Distinct().OrderBy(f => new FileInfo(f).Length).ToArray();
            Console.WriteLine($"Binarios a escanear: {scanFiles.Length} (Game + raiz del PBE)");

            foreach (string file in scanFiles)
            {
                var fileInfo = new FileInfo(file);
                byte[] data = File.ReadAllBytes(file);
                var found = new List<string>();
                foreach (var target in Targets)
                {
                    if (ContainsU32(data, target.Hash))
                        found.Add(target.Name);
                }
                if (found.Count > 0)
                    Console.WriteLine($"[HIT] {Path.GetFileName(file)} ({fileInfo.Length:N0} bytes): {string.Join(", ", found)}");
                else
                    Console.WriteLine($"  --  {Path.GetFileName(file)} ({fileInfo.Length:N0} bytes): nada");
            }

            string exePath = Path.Combine(gameDir, "League of Legends.exe");
            if (File.Exists(exePath))
            {
                Console.WriteLine();
                Console.WriteLine("=== Diagnostico: offsets de los 6 hashes en el exe ===");
                byte[] exe = File.ReadAllBytes(exePath);
                foreach (var target in Targets)
                {
                    var offsets = FindAllOffsets(exe, target.Hash);
                    Console.WriteLine($"  {target.Name} ({target.Hash:x8}): {offsets.Count} apariciones" +
                        (offsets.Count > 0 ? $" - primera LE@{offsets[0].Item1} (aligned={offsets[0].Item1 % 4 == 0}) BE@{offsets[0].Item2}" : ""));
                    string name = target.Name;
                    int nameIndex = IndexOfAscii(exe, name);
                    Console.WriteLine($"     string '{name}': {(nameIndex >= 0 ? $"SI @{nameIndex}" : "NO como ASCII")}");
                }
            }
            Console.WriteLine("Scan complete.");
        }

        private static bool IsBinary(string path)
        {
            string name = Path.GetFileName(path).ToLowerInvariant();
            if (name.EndsWith(".exe") || name.EndsWith(".dll")) return true;
            return false;
        }

        private static List<(int, int)> FindAllOffsets(byte[] data, uint value)
        {
            var result = new List<(int, int)>();
            byte b0 = (byte)value, b1 = (byte)(value >> 8), b2 = (byte)(value >> 16), b3 = (byte)(value >> 24);
            for (int i = 0; i <= data.Length - 4; i++)
            {
                bool le = data[i] == b0 && data[i + 1] == b1 && data[i + 2] == b2 && data[i + 3] == b3;
                bool be = data[i] == b3 && data[i + 1] == b2 && data[i + 2] == b1 && data[i + 3] == b0;
                if (le || be)
                    result.Add((i, le && be ? -1 : (be ? i : -1)));
                if (result.Count >= 8) break;
            }
            return result;
        }

        private static int IndexOfAscii(byte[] data, string value)
        {
            for (int i = 0; i <= data.Length - value.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < value.Length; j++)
                {
                    if (data[i + j] != (byte)value[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private static bool ContainsU32(byte[] data, uint value)
        {
            byte b0 = (byte)value, b1 = (byte)(value >> 8), b2 = (byte)(value >> 16), b3 = (byte)(value >> 24);
            for (int i = 0; i <= data.Length - 4; i++)
            {
                if (data[i] == b0 && data[i + 1] == b1 && data[i + 2] == b2 && data[i + 3] == b3)
                    return true;
                if (data[i] == b3 && data[i + 1] == b2 && data[i + 2] == b1 && data[i + 3] == b0)
                    return true;
            }
            return false;
        }
    }
}
