using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Shaders;
using LeagueToolkit.Core.Renderer;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `shader-glsl-dump <shader.ps|shader.vs> <outDir> [maxPermutations] [--defines NAME=VALUE;...]`: translates the ShaderCache permutations of
    /// a game shader stage (e.g. ASSETS/Shaders/HLSL/Filters/MipChainBloomUpsample.ps) to GLSL files, with the
    /// reflected constant buffers, to read engine passes the skins never reference.
    /// </summary>
    internal static class ShaderGlslDumpDiagnostic
    {
        public static void Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: shader-glsl-dump <shader.ps|shader.vs> <outDir> [maxPermutations] [--defines NAME=VALUE;...]");
                return;
            }

            string cache = new[] { @"C:\Riot Games\League of Legends (PBE)", @"C:\Riot Games\League of Legends" }
                .Select(root => Path.Combine(root, @"Game\DATA\FINAL\ShaderCache.dx11.wad.client"))
                .FirstOrDefault(File.Exists);
            using var wad = new WadFile(cache);
            string stage = args[0].EndsWith(".vs", StringComparison.OrdinalIgnoreCase) ? "vs" : "ps";
            string toc = GameShaderProgramResolver.TocPath(args[0], stage);
            ShaderToc table;
            using (var bytes = wad.LoadChunkDecompressed(XxHash64Ext.Hash(toc)))
            using (var stream = new MemoryStream(bytes.Span.ToArray(), writable: false))
                table = new ShaderToc(stream);

            Console.WriteLine($"[Dump] {toc} permutations={table.ShaderHashes.Count} baseDefines=" +
                              string.Join(",", table.BaseDefines.Select(define => define.ToString())));
            Directory.CreateDirectory(args[1]);
            int max = args.Length > 2 && int.TryParse(args[2], out int parsedMax) ? parsedMax : 8;
            string name = Path.GetFileName(args[0]);
            IEnumerable<int> indices = Enumerable.Range(0, Math.Min(max, table.ShaderIds.Count));
            int definesAt = Array.IndexOf(args, "--defines");
            if (definesAt >= 0 && definesAt + 1 < args.Length)
            {
                // One exact permutation: `--defines NAME=VALUE;...` as the material would request it.
                var requested = args[definesAt + 1].Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(entry => entry.Split('=', 2))
                    .Select(parts => new KeyValuePair<string, string>(parts[0], parts.Length > 1 ? parts[1] : "1"));
                GameShaderPermutationLookup.Match match = GameShaderPermutationLookup.Find(table, requested, fallback: false);
                if (match == null)
                {
                    Console.WriteLine("[Dump] no exact permutation for the requested defines");
                    return;
                }
                indices = new[] { match.Index };
            }
            foreach (int index in indices)
            {
                uint id = table.ShaderIds[index];
                byte[] dxbc;
                using (var bundle = wad.LoadChunkDecompressed(XxHash64Ext.Hash(GameShaderProgramResolver.BundlePath(toc, id))))
                    dxbc = GameShaderProgramResolver.ReadBundleRecord(bundle.Span, id % 100);

                ShaderReflectionData reflection = DxbcReflection.Reflect(dxbc);
                string header = string.Join("\n", reflection.ConstantBuffers.Select(buffer =>
                    $"// cbuffer {buffer.Name} size={buffer.Size}: " + string.Join(", ", buffer.Members.Select(m => $"{m.Name}@{m.Offset}"))));
                GameShaderTranslator.TranslatedStage translated = GameShaderTranslator.TranslateStage(
                    dxbc, reflection, stage == "vs" ? GameShaderTranslator.Stage.Vertex : GameShaderTranslator.Stage.Pixel);
                string path = Path.Combine(args[1], $"{name}_{index}.glsl");
                File.WriteAllText(path, header + "\n" + translated.Glsl);
                Console.WriteLine($"[Dump] #{index} id={id} -> {path}");
            }
        }
    }
}
