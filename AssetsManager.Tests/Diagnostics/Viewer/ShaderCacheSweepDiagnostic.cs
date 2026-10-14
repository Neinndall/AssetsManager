using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Shaders;
using LeagueToolkit.Core.Renderer;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `shader-cache-sweep [hashes.game.txt] [--per-toc N] [--filter text] [--dump dir]`: translates every
    /// DX11 permutation of ShaderCache.dx11.wad.client (deduplicated by bytecode) and groups the failures,
    /// to measure what the DXBC → SPIR-V → GLSL translator does not cover yet.
    /// </summary>
    internal static class ShaderCacheSweepDiagnostic
    {
        private const int RecordsPerBundle = 100;

        public static void Run(string[] args)
        {
            string hashes = args.Where((arg, index) => !arg.StartsWith("--", StringComparison.Ordinal) &&
                                                       (index == 0 || !args[index - 1].StartsWith("--", StringComparison.Ordinal)))
                                .FirstOrDefault() ??
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "AssetsManager", "hashes", "hashes.game.txt");
            int perToc = int.TryParse(Option(args, "--per-toc"), out int limit) ? limit : int.MaxValue;
            string filter = Option(args, "--filter");
            string dump = Option(args, "--dump");

            string cache = new[]
                {
                    @"C:\Riot Games\League of Legends (PBE)",
                    @"C:\Riot Games\League of Legends"
                }
                .Select(root => Path.Combine(root, @"Game\DATA\FINAL\ShaderCache.dx11.wad.client"))
                .FirstOrDefault(File.Exists);
            if (cache == null || !File.Exists(hashes))
            {
                Console.WriteLine("Usage: shader-cache-sweep [hashes.game.txt] [--per-toc N] [--filter text] [--dump dir]");
                return;
            }

            string[] tocs = File.ReadLines(hashes)
                .Select(line => line.Split(' ', 2))
                .Where(parts => parts.Length == 2 && parts[1].EndsWith("-dx11", StringComparison.Ordinal))
                .Select(parts => parts[1])
                .Where(path => filter == null || path.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();

            using var wad = new WadFile(cache);
            var seen = new HashSet<ulong>();
            var failures = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var dimensions = new Dictionary<string, int>(StringComparer.Ordinal);
            var categories = new Dictionary<string, (int Stages, int Failed)>(StringComparer.Ordinal);
            int missingTocs = 0;
            int permutations = 0;
            int unique = 0;
            int translated = 0;
            var clock = Stopwatch.StartNew();

            foreach (string toc in tocs)
            {
                ulong tocHash = XxHash64Ext.Hash(toc);
                if (!wad.Chunks.ContainsKey(tocHash))
                {
                    missingTocs++;
                    continue;
                }

                ShaderToc table;
                try
                {
                    using var tocBytes = wad.LoadChunkDecompressed(tocHash);
                    using var tocStream = new MemoryStream(tocBytes.Span.ToArray(), writable: false);
                    table = new ShaderToc(tocStream);
                }
                catch (Exception ex)
                {
                    AddFailure(failures, "toc: " + ex.Message, toc);
                    continue;
                }

                GameShaderTranslator.Stage stage = toc.EndsWith(".vs-dx11", StringComparison.Ordinal)
                    ?GameShaderTranslator.Stage.Vertex
                    : GameShaderTranslator.Stage.Pixel;
                string category = Category(toc);
                var bundles = new Dictionary<uint, byte[]>();

                foreach (uint shaderId in table.ShaderIds.Distinct().Take(perToc))
                {
                    permutations++;
                    byte[] dxbc;
                    try
                    {
                        uint bundleStart = RecordsPerBundle * (shaderId / RecordsPerBundle);
                        if (!bundles.TryGetValue(bundleStart, out byte[] bundle))
                        {
                            using var bundleBytes = wad.LoadChunkDecompressed(XxHash64Ext.Hash($"{toc}_{bundleStart}"));
                            bundles[bundleStart] = bundle = bundleBytes.Span.ToArray();
                        }
                        dxbc = GameShaderProgramResolver.ReadBundleRecord(bundle, shaderId % RecordsPerBundle);
                    }
                    catch (Exception ex)
                    {
                        AddFailure(failures, "bundle: " + ex.Message, $"{toc}#{shaderId}");
                        continue;
                    }

                    if (!seen.Add(System.IO.Hashing.XxHash64.HashToUInt64(dxbc)))
                        continue;

                    unique++;
                    (int stages, int failed) = categories.GetValueOrDefault(category);
                    try
                    {
                        ShaderReflectionData reflection = DxbcReflection.Reflect(dxbc);
                        GameShaderTranslator.TranslatedStage result = GameShaderTranslator.TranslateStage(dxbc, reflection, stage);
                        translated++;
                        foreach (GameShaderTranslator.TextureBinding texture in result.Sidecar.Textures)
                            dimensions[texture.Dimension.ToString()] = dimensions.GetValueOrDefault(texture.Dimension.ToString()) + 1;
                        categories[category] = (stages + 1, failed);
                    }
                    catch (Exception ex)
                    {
                        categories[category] = (stages + 1, failed + 1);
                        string owner = $"{toc}#{shaderId}";
                        AddFailure(failures, Normalize(ex.GetType().Name + ": " + ex.Message), owner);
                        if (dump != null)
                        {
                            Directory.CreateDirectory(dump);
                            File.WriteAllBytes(Path.Combine(dump, string.Concat(owner.Replace('/', '_').Split(Path.GetInvalidFileNameChars())) + ".dxbc"), dxbc);
                        }
                    }
                }
            }

            Console.WriteLine(
                $"[ShaderSweep] cache={cache} tocs={tocs.Length} missingTocs={missingTocs} permutations={permutations} " +
                $"uniqueBytecode={unique} translated={translated}/{unique} failed={unique - translated} elapsed={clock.Elapsed:mm\\:ss}.");
            foreach ((string category, (int stages, int failed)) in categories.OrderByDescending(pair => pair.Value.Failed).ThenBy(pair => pair.Key))
                Console.WriteLine($"[ShaderSweep] category {category}: failed={failed}/{stages}");
            Console.WriteLine("[ShaderSweep] textureDimensions=" + string.Join(", ", dimensions.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key}:{pair.Value}")));
            foreach ((string failure, List<string> owners) in failures.OrderByDescending(pair => pair.Value.Count))
                Console.WriteLine($"[ShaderSweep] FAIL x{owners.Count}: {failure} e.g. {string.Join(", ", owners.Take(3))}");
        }

        private static string Category(string toc)
        {
            string[] parts = toc.Split('/');
            return parts.Length > 4 ? $"{parts[2]}/{parts[3]}" : toc;
        }

        // Collapses ids, offsets and names so one root cause groups into one line.
        private static string Normalize(string message)
        {
            string firstLine = message.Split('\n')[0].Trim();
            return Regex.Replace(firstLine, @"0x[0-9a-fA-F]+|\d+", "#");
        }

        private static void AddFailure(IDictionary<string, List<string>> failures, string failure, string owner)
        {
            if (!failures.TryGetValue(failure, out List<string> owners))
                failures[failure] = owners = new List<string>();
            owners.Add(owner);
        }

        private static string Option(string[] args, string name)
        {
            int at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }
    }
}
