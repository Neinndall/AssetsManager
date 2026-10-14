using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using LeagueToolkit.Core.Renderer;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `shader-permutation-probe <shader> <vs|ps> <NAME=VALUE;...> [maxChanges]`: lists the TOC base defines of a shader stage,
    /// whether the requested define set names a permutation the ShaderCache holds and, when not, the nearest one
    /// the resolver falls back to.
    /// </summary>
    internal static class ShaderPermutationProbeDiagnostic
    {
        public static void Run(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: shader-permutation-probe <shader> <vs|ps> <NAME=VALUE;...> [maxChanges]");
                return;
            }

            string cache = new[]
                {
                    @"C:\Riot Games\League of Legends (PBE)",
                    @"C:\Riot Games\League of Legends"
                }
                .Select(root => Path.Combine(root, @"Game\DATA\FINAL\ShaderCache.dx11.wad.client"))
                .FirstOrDefault(File.Exists);
            using var wad = new WadFile(cache);
            string toc = GameShaderProgramResolver.TocPath(args[0], args[1]);
            ShaderToc table;
            using (var bytes = wad.LoadChunkDecompressed(XxHash64Ext.Hash(toc)))
            using (var stream = new MemoryStream(bytes.Span.ToArray(), writable: false))
                table = new ShaderToc(stream);

            string[] baseDefines = table.BaseDefines.OrderBy(define => define.Name, StringComparer.Ordinal)
                .Select(define => define.ToString()).ToArray();
            Console.WriteLine($"[Probe] {toc} permutations={table.ShaderHashes.Count} baseDefines={string.Join(",", baseDefines)}");

            KeyValuePair<string, string>[] requested = args[2]
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Split('=', 2))
                .Select(parts => new KeyValuePair<string, string>(parts[0], parts.Length > 1 ? parts[1] : "1"))
                .ToArray();
            Console.WriteLine($"[Probe] requested key={GameShaderPermutationLookup.Key(requested, table.BaseDefines)}");

            GameShaderPermutationLookup.Match match = GameShaderPermutationLookup.Find(
                table,
                requested,
                maxChanges: args.Length > 3 ? int.Parse(args[3]) : GameShaderPermutationLookup.MaxChanges);
            if (match == null)
                Console.WriteLine("[Probe] no permutation within the change limit");
            else if (match.Exact)
                Console.WriteLine($"[Probe] exact permutation #{match.Index}");
            else
                Console.WriteLine($"[Probe] nearest permutation #{match.Index} after {string.Join(" ", match.Changes)}: " +
                                  GameShaderPermutationLookup.Key(match.Defines, table.BaseDefines));
        }
    }
}
