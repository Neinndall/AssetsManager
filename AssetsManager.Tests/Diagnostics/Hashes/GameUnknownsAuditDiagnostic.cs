using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class GameUnknownsAuditDiagnostic
    {
        private static readonly Regex PathLikeTokenRegex = new(
            @"(?:data|assets|maps|plugins|patches|gameplay|characters|shared|levels|shaders|ux)/[a-z0-9_\-\.#/]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static void Run(string[] args)
        {
            string pbeRoot = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))
                ?? @"C:\Riot Games\League of Legends (PBE)";

            string unknownsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AssetsManager", "hash_lab", "unknowns.game.txt");
            if (!File.Exists(unknownsPath))
            {
                Console.WriteLine($"Unknowns file not found at: {unknownsPath}");
                return;
            }

            var unknownHashes = new HashSet<ulong>();
            foreach (string line in File.ReadLines(unknownsPath))
            {
                if (ulong.TryParse(line.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    unknownHashes.Add(hash);
            }
            if (unknownHashes.Count == 0)
            {
                Console.WriteLine("No unknown hashes loaded.");
                return;
            }

            string gameDir = Directory.Exists(Path.Combine(pbeRoot, "Game"))
                ? Path.Combine(pbeRoot, "Game")
                : pbeRoot;
            var wads = Directory.EnumerateFiles(gameDir, "*.wad.client", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Console.WriteLine("==================================================");
            Console.WriteLine($"    GAME UNKNOWNS FORENSIC AUDIT ({unknownHashes.Count} hashes)");
            Console.WriteLine("==================================================");
            Console.WriteLine($"Root: {pbeRoot}");
            Console.WriteLine($"Scanning {wads.Count} game WADs...");

            var findings = new Dictionary<ulong, Finding>();
            var solvedPaths = new Dictionary<ulong, string>();
            var stopwatch = Stopwatch.StartNew();

            foreach (string wadPath in wads)
            {
                string relWad = Path.GetRelativePath(gameDir, wadPath).Replace('\\', '/');
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (!unknownHashes.Contains(pair.Key) || findings.ContainsKey(pair.Key))
                            continue;

                        var finding = new Finding(pair.Key, relWad, pair.Value);
                        try
                        {
                            using var owner = wad.LoadChunkDecompressed(pair.Value);
                            ArraySegment<byte> seg = owner.DangerousGetArray();
                            byte[] data = seg.Array[seg.Offset..(seg.Offset + seg.Count)];
                            finding.FileType = DetectGameFileType(data, out string sample);
                            finding.Sample = sample;
                            finding.Strings = ExtractPathTokens(data).ToList();

                            foreach (string token in finding.Strings)
                                TryCrack(token, unknownHashes, solvedPaths);
                        }
                        catch (Exception ex)
                        {
                            finding.FileType = $"ERR({ex.GetType().Name})";
                            finding.Sample = ex.Message;
                        }
                        findings[pair.Key] = finding;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [warn] failed to open {relWad}: {ex.Message}");
                }
            }
            stopwatch.Stop();

            Console.WriteLine($"\nLocated {findings.Count}/{unknownHashes.Count} unknowns across WADs in {stopwatch.Elapsed:hh\\:mm\\:ss}.");

            Console.WriteLine("\n[1] FILE TYPE BREAKDOWN:");
            Console.WriteLine("--------------------------------------------------");
            foreach (var group in findings.Values.GroupBy(finding => finding.FileType).OrderByDescending(group => group.Count()))
            {
                Console.WriteLine($"  {group.Key,-24} x{group.Count()}");
            }

            Console.WriteLine("\n[2] COMPRESSION BREAKDOWN:");
            Console.WriteLine("--------------------------------------------------");
            foreach (var group in findings.Values.GroupBy(finding => finding.Chunk.Compression.ToString()).OrderByDescending(group => group.Count()))
            {
                Console.WriteLine($"  {group.Key,-24} x{group.Count()}");
            }

            Console.WriteLine("\n[3] SIZE DISTRIBUTION:");
            Console.WriteLine("--------------------------------------------------");
            foreach (var bucket in findings.Values
                .GroupBy(finding => SizeBucket(finding.Chunk.UncompressedSize))
                .OrderBy(group => group.Min(finding => ParseBucketFloor(group.Key))))
            {
                Console.WriteLine($"  {bucket.Key,-16} x{bucket.Count()}");
            }

            Console.WriteLine("\n[4] PER-HASH DETAILS:");
            Console.WriteLine("--------------------------------------------------");
            foreach (Finding finding in findings.Values.OrderBy(finding => finding.Hash))
            {
                string stringsPreview = finding.Strings.Count > 0
                    ? string.Join(" | ", finding.Strings.Take(3))
                    : string.Empty;
                Console.WriteLine($"  {finding.Hash:x16}  {finding.FileType,-20} {FormatSize(finding.Chunk.UncompressedSize),10}  {finding.Wad}");
                if (!string.IsNullOrEmpty(finding.Sample))
                    Console.WriteLine($"      sample: {finding.Sample}");
                if (stringsPreview.Length > 0)
                    Console.WriteLine($"      refs:   {stringsPreview}");
            }

            Console.WriteLine("\n[5] NOT LOCATED IN LOCAL WADS:");
            Console.WriteLine("--------------------------------------------------");
            var missing = unknownHashes.Where(hash => !findings.ContainsKey(hash)).OrderBy(hash => hash).ToList();
            foreach (ulong hash in missing)
                Console.WriteLine($"  {hash:x16}");

            Console.WriteLine("\n[6] INSTANT CRACKS FROM EMBEDDED PATHS:");
            Console.WriteLine("--------------------------------------------------");
            if (solvedPaths.Count == 0)
            {
                Console.WriteLine("  (none)");
            }
            else
            {
                foreach (var pair in solvedPaths.OrderBy(item => item.Key))
                    Console.WriteLine($"  [CRACKED] {pair.Key:x16} = {pair.Value}");
            }
        }

        private static void TryCrack(string token, HashSet<ulong> unknownHashes, Dictionary<ulong, string> solvedPaths)
        {
            string normalized = token.Trim().ToLowerInvariant();
            if (normalized.Length == 0 || !unknownHashes.Contains(XxHash64Ext.Hash(normalized)))
                return;
            solvedPaths.TryAdd(XxHash64Ext.Hash(normalized), normalized);
        }

        private static IEnumerable<string> ExtractPathTokens(byte[] data)
        {
            var results = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in PathLikeTokenRegex.Matches(Encoding.ASCII.GetString(data)))
            {
                string value = match.Value.TrimEnd('.', '/', '#');
                if (value.Length >= 8 && value.Split('/').Length >= 2)
                    results.Add(value);
            }
            return results;
        }

        private static string DetectGameFileType(byte[] data, out string sample)
        {
            sample = string.Empty;
            if (data == null || data.Length == 0) return "EMPTY";

            string asciiMagic = Encoding.ASCII.GetString(data, 0, Math.Min(8, data.Length));
            if (asciiMagic.StartsWith("r3d2", StringComparison.Ordinal))
            {
                if (asciiMagic.Length >= 8 && asciiMagic.StartsWith("r3d2anmd")) return "ANIMATION_ANMD";
                if (asciiMagic.Length >= 8 && asciiMagic.StartsWith("r3d2canmd")) return "ANIMATION_CANMD";
                if (asciiMagic.StartsWith("r3d2sklt")) return "SKELETON_SKLT";
                if (asciiMagic.StartsWith("r3d2cskt")) return "SKELETON_CSKT";
                if (asciiMagic.StartsWith("r3d2smpl")) return "MESH_SAMPLER";
                return $"BIN({ReadUintLe(data, 4)})";
            }

            if (asciiMagic.StartsWith("BKHD", StringComparison.Ordinal)) return "WWISE_BANK";
            if (asciiMagic.StartsWith("RIFF", StringComparison.Ordinal)) return "WEM_AUDIO";
            if (asciiMagic.StartsWith("OggS", StringComparison.Ordinal)) return "OGG";
            if (data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return "PNG";
            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return "JPG";
            if (data[0] == 0x1B && data[1] == 0x4C && data[2] == 0x75 && data[3] == 0x61) return "LUA_BYTECODE";

            if (IsLikelyRiotTex(data))
            {
                sample = $"w={ReadUshortLe(data, 12)} h={ReadUshortLe(data, 14)}";
                return "TEX";
            }

            bool printableRatio = data.Take(Math.Min(data.Length, 512)).Count(b => b == 0x09 || b == 0x0A || b == 0x0D || (b >= 0x20 && b <= 0x7E)) >
                Math.Min(data.Length, 512) * 0.85;
            if (printableRatio)
            {
                string head = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 120)).TrimStart();
                sample = head.Replace("\r", "").Replace("\n", " ");
                if (head.StartsWith("{") || head.StartsWith("[")) return "JSON";
                return "TEXT";
            }

            return $"RAW({Convert.ToHexString(data, 0, Math.Min(8, data.Length))})";
        }

        private static bool IsLikelyRiotTex(byte[] data)
        {
            if (data.Length < 16) return false;
            uint flags = ReadUintLe(data, 0);
            if (flags != 5 && flags != 6) return false;
            byte[] ddsHeader = Encoding.ASCII.GetBytes("DDS ");
            bool containsDds = data.AsSpan().IndexOf(ddsHeader) >= 0;
            return containsDds || ReadUintLe(data, 4) <= 32;
        }

        private static string SizeBucket(long size) =>
            size switch
            {
                < 1024 => "< 1KB",
                < 16 * 1024 => "1KB - 16KB",
                < 64 * 1024 => "16KB - 64KB",
                < 256 * 1024 => "64KB - 256KB",
                < 1024 * 1024 => "256KB - 1MB",
                _ => "> 1MB"
            };

        private static long ParseBucketFloor(string bucket) =>
            bucket switch
            {
                "< 1KB" => 0,
                "1KB - 16KB" => 1024,
                "16KB - 64KB" => 16 * 1024,
                "64KB - 256KB" => 64 * 1024,
                "256KB - 1MB" => 256 * 1024,
                _ => 1024 * 1024
            };

        private static string FormatSize(long size) =>
            size >= 1024 * 1024 ? $"{size / (1024d * 1024):F1}MB"
                : size >= 1024 ? $"{size / 1024d:F1}KB"
                : $"{size}B";

        private static uint ReadUintLe(byte[] data, int offset) =>
            data.Length >= offset + 4
                ? (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24))
                : 0;

        private static ushort ReadUshortLe(byte[] data, int offset) =>
            data.Length >= offset + 2 ? (ushort)(data[offset] | (data[offset + 1] << 8)) : (ushort)0;

        private sealed class Finding
        {
            public Finding(ulong hash, string wad, WadChunk chunk)
            {
                Hash = hash;
                Wad = wad;
                Chunk = chunk;
            }

            public ulong Hash { get; }
            public string Wad { get; }
            public WadChunk Chunk { get; }
            public string FileType { get; set; } = "UNKNOWN";
            public string Sample { get; set; } = string.Empty;
            public List<string> Strings { get; set; } = new();
        }
    }
}
