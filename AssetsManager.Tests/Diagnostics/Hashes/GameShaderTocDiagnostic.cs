using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AssetsManager.Utils;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    // Lists the readable words inside unresolved shader bundles (TOC3 tables and DXBC bytecode):
    // defines, constant buffers and resource names are the only naming evidence they carry.
    internal static class GameShaderTocDiagnostic
    {
        private static readonly Regex Words = new(@"[A-Za-z_][A-Za-z0-9_]{3,}", RegexOptions.Compiled);

        public static void Run(string[] args)
        {
            var directories = new DirectoriesCreator();
            var unknown = File.ReadLines(Path.Combine(directories.HashLabPath, "unknowns.game.txt"))
                .Select(l => ulong.TryParse(l.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong h) ? h : 0)
                .Where(h => h != 0).ToHashSet();
            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Microsoft", "HLSL", "Shader", "Compiler", "RDEF", "ISGN", "OSGN", "SHEX", "SHDR", "STAT", "DXBC", "TOC3",
                "baseDefines", "shaders", "float4", "float3", "float2", "float", "SV_Position", "SV_Target", "TEXCOORD", "POSITION"
            };
            foreach (string wadPath in Directory.EnumerateFiles(@"C:\Riot Games\League of Legends (PBE)\Game", "*.wad.client", SearchOption.AllDirectories)
                .Where(p => p.Contains("Bootstrap", StringComparison.OrdinalIgnoreCase) || p.Contains("ShaderCache", StringComparison.OrdinalIgnoreCase)))
            {
                using var wad = new WadFile(wadPath);
                foreach (var (hash, chunk) in wad.Chunks)
                {
                    if (!unknown.Contains(hash) || chunk.Compression == WadChunkCompression.Satellite) continue;
                    using var data = wad.LoadChunkDecompressed(chunk);
                    string text = Encoding.Latin1.GetString(data.Span);
                    string kind = text.Contains("TOC3") ? "TOC3" : text.Contains("DXBC") ? "DXBC" : "other";
                    var words = Words.Matches(text).Select(m => m.Value).Where(w => !ignored.Contains(w)).Distinct().Take(14);
                    Console.WriteLine($"{Path.GetFileName(wadPath)} {hash:x16} {kind,-5} {data.Length,7}B  {string.Join(" ", words)}");
                }
            }
        }
    }
}
