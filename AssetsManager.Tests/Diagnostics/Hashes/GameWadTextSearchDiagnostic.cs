using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    // Finds which decompressed chunks of matching WADs contain an ASCII text, printing the chunk
    // path (or hash), its magic and the surrounding bytes, to learn where GAME names are stored.
    internal static class GameWadTextSearchDiagnostic
    {
        public static async Task Run(string[] args)
        {
            string wadFilter = args.FirstOrDefault(a => a.StartsWith("--wad=", StringComparison.Ordinal))?[6..] ?? "Bootstrap";
            string[] needles = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
            var directories = new DirectoriesCreator();
            var log = new LogService(new Serilog.LoggerConfiguration().MinimumLevel.Warning().CreateLogger());
            using var resolver = new HashResolverService(directories, log);
            await resolver.LoadAllHashesAsync();
            int printed = 0;

            foreach (string wadPath in Directory.EnumerateFiles(@"C:\Riot Games\League of Legends (PBE)\Game", "*.wad.client", SearchOption.AllDirectories)
                .Where(p => Path.GetFileName(p).Contains(wadFilter, StringComparison.OrdinalIgnoreCase)))
            {
                using var wad = new WadFile(wadPath);
                foreach (var (chunkHash, chunk) in wad.Chunks)
                {
                    if (chunk.Compression == WadChunkCompression.Satellite) continue;
                    string text;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        text = Encoding.Latin1.GetString(data.Span);
                    }
                    catch { continue; }
                    foreach (string needle in needles)
                    {
                        int at = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                        if (at < 0 || printed++ > 60) continue;
                        string magic = text.Length >= 4 ? text[..4] : text;
                        int start = Math.Max(0, at - 60);
                        string around = new string(text.Substring(start, Math.Min(160, text.Length - start)).Select(c => c is >= ' ' and <= '~' ? c : '.').ToArray());
                        Console.WriteLine($"{Path.GetFileName(wadPath)} {chunkHash:x16} {resolver.ResolveHash(chunkHash)} magic={new string(magic.Select(c => c is >= ' ' and <= '~' ? c : '.').ToArray())} '{needle}' at {at}");
                        Console.WriteLine($"      {around}");
                    }
                }
            }
        }
    }
}
