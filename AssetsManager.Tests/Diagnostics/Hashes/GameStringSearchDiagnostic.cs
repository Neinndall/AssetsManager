using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    /// <summary>
    /// Read-only byte search inside a single WAD's decompressed chunks. Finds which
    /// chunks reference a given ASCII token (e.g. a texture stem with no known BIN
    /// referrer). Usage: game-string-search &lt;wadPath&gt; &lt;needle&gt;
    /// </summary>
    internal static class GameStringSearchDiagnostic
    {
        public static void Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: game-string-search <wadPath> <needle>");
                return;
            }

            string wadPath = Path.GetFullPath(args[0]);
            string needle = args[1];
            if (!File.Exists(wadPath))
            {
                Console.WriteLine($"WAD not found: {wadPath}");
                return;
            }

            byte[] needleBytes = Encoding.ASCII.GetBytes(needle);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var known = new HashSet<ulong>();
            string gameHashesPath = Path.Combine(localAppData, "AssetsManager", "hashes", "hashes.game.txt");
            if (File.Exists(gameHashesPath))
            {
                foreach (string line in File.ReadLines(gameHashesPath))
                {
                    if (line.Length > 17 && line[16] == ' ' &&
                        ulong.TryParse(line.AsSpan(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                        known.Add(hash);
                }
            }

            Console.WriteLine($"Searching '{needle}' in {Path.GetFileName(wadPath)}...");
            int chunks = 0;
            int hits = 0;
            using var wad = new WadFile(wadPath);
            foreach (var pair in wad.Chunks)
            {
                chunks++;
                byte[] data;
                try
                {
                    using var owner = wad.LoadChunkDecompressed(pair.Value);
                    ArraySegment<byte> seg = owner.DangerousGetArray();
                    data = seg.Array[seg.Offset..(seg.Offset + seg.Count)];
                }
                catch
                {
                    continue;
                }

                int index = IndexOf(data, needleBytes);
                if (index < 0)
                    continue;
                hits++;
                Console.WriteLine($"[HIT] {pair.Key:x16} ({(known.Contains(pair.Key) ? "known" : "unknown")}) offset={index} size={data.Length}");
                int start = Math.Max(0, index - 48);
                int length = Math.Min(48 + needleBytes.Length + 48, data.Length - start);
                Console.WriteLine($"      ...{ToPrintable(data, start, length)}...");
                if (hits >= 20)
                {
                    Console.WriteLine("  (first 20 hits shown)");
                    break;
                }
            }

            Console.WriteLine($"Scanned {chunks} chunks, {hits} hits.");
        }

        private static int IndexOf(byte[] data, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= data.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (data[i + j] != needle[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                    return i;
            }

            return -1;
        }

        private static string ToPrintable(byte[] data, int start, int length)
        {
            var builder = new StringBuilder(length);
            for (int i = start; i < start + length; i++)
                builder.Append(data[i] >= 32 && data[i] <= 126 ? (char)data[i] : '.');
            return builder.ToString();
        }
    }
}
