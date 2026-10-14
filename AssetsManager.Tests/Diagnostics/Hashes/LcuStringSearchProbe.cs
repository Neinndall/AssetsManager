using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class LcuStringSearchProbe
    {
        public static void Run(string pbeRoot, string needlesArg)
        {
            string[] needles = (needlesArg ?? string.Empty)
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim().ToLowerInvariant())
                .Where(value => value.Length > 0)
                .ToArray();
            if (needles.Length == 0)
            {
                Console.WriteLine("Usage: lcu-string-search <pbe-root> <needle1,needle2,...>");
                return;
            }

            Console.WriteLine($"Searching WAD text chunks for: {string.Join(", ", needles)}");
            string[] wads = Directory.EnumerateFiles(pbeRoot, "*.wad", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".wad", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Console.WriteLine($"LCU WADs: {wads.Length}");

            ulong[] needleHashes = needles.Select(value =>
                ulong.TryParse(value.Split('.')[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash) ? hash : 0UL)
                .ToArray();

            foreach (string wadPath in wads)
            {
                try
                {
                    using var wad = new LeagueToolkit.Core.Wad.WadFile(wadPath);
                    foreach (ulong needleHash in needleHashes)
                    {
                        if (needleHash != 0UL && wad.Chunks.ContainsKey(needleHash))
                            Console.WriteLine($"[KEY-HIT] {Path.GetFileName(wadPath)} chunk key 0x{needleHash:x16}");
                    }
                    foreach (var pair in wad.Chunks)
                    {
                        string sig = GetChunkSignature(wad, pair.Value);
                        if (IsBinarySignature(sig)) continue;
                        try
                        {
                            using var data = wad.LoadChunkDecompressed(pair.Value);
                            string text = Encoding.UTF8.GetString(data.Memory.Span.ToArray());
                            foreach (string needle in needles)
                            {
                                if (text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    int index = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                                    int start = Math.Max(0, index - 60);
                                    int length = Math.Min(160, text.Length - start);
                                    string context = text.Substring(start, length).Replace("\n", "\\n").Replace("\r", "");
                                    Console.WriteLine($"[HIT] {Path.GetFileName(wadPath)} {pair.Key:x16} ({sig}) needle={needle}");
                                    Console.WriteLine($"      ...{context}...");
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ! {Path.GetFileName(wadPath)}: {ex.Message}");
                }
            }
            Console.WriteLine("Search complete.");
        }

        private static readonly HashSet<string> BinarySignatures = new(StringComparer.Ordinal)
        {
            ".PNG", "OggS", ".E..", "..xm", "OTTO", ".dds", "true", "fLaC", "id3 ", "ID3 ",
        };

        private static bool IsBinarySignature(string sig)
        {
            if (BinarySignatures.Contains(sig)) return true;
            if (sig.Length < 3) return false;
            return sig.Any(ch => ch < 0x20 && ch != '\t' && ch != '\r' && ch != '\n');
        }

        private static string GetChunkSignature(LeagueToolkit.Core.Wad.WadFile wad, LeagueToolkit.Core.Wad.WadChunk chunk)
        {
            try
            {
                using Stream stream = wad.OpenChunk(chunk);
                byte[] buffer = new byte[4];
                int read = stream.Read(buffer, 0, 4);
                if (read < 3) return string.Empty;
                if (read == 3) return Encoding.ASCII.GetString(buffer, 0, 3);
                return Encoding.ASCII.GetString(buffer);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
