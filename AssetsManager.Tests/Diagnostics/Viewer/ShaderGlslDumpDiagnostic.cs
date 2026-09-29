using System;
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
    /// `shader-glsl-dump <shader.ps|shader.vs> <outDir> [maxPermutations]`: translates the ShaderCache permutations of
    /// a game shader stage (e.g. ASSETS/Shaders/HLSL/Filters/MipChainBloomUpsample.ps) to GLSL files, with the
    /// reflected constant buffers, to read engine passes the skins never reference.
    /// </summary>
    internal static class ShaderGlslDumpDiagnostic
    {
        public static void Run(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: shader-glsl-dump <shader.ps|shader.vs> <outDir> [maxPermutations]");
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
            int max = args.Length > 2 ? int.Parse(args[2]) : 8;
            string name = Path.GetFileName(args[0]);
            for (int index = 0; index < Math.Min(max, table.ShaderIds.Count); index++)
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
