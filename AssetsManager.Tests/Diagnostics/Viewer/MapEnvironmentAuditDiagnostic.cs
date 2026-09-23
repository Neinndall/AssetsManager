using System;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    internal static class MapEnvironmentAuditDiagnostic
    {
        public static void Run(string root)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                Console.WriteLine("Usage: map-environment-audit <extracted-map-root>");
                return;
            }

            string[] files = Directory.GetFiles(root, "*.materials.bin", SearchOption.AllDirectories);
            int parsed = 0;
            int withEffects = 0;
            int depthFog = 0;
            int heightFog = 0;
            int dof = 0;
            int ssao = 0;
            int failed = 0;

            foreach (string path in files.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (!MapPath.TryFromMapFile(path, out MapPath map))
                        continue;

                    using Stream stream = File.OpenRead(path);
                    var tree = new BinTree(stream);
                    MapPostEffectsData effects = MapPostEffectsParser.Parse(tree, map);
                    MapSsaoData ambient = MapSsaoParser.Parse(tree, map);
                    parsed++;

                    bool drawsEffects = effects?.DrawsAnything == true;
                    bool drawsSsao = ambient?.DrawsAnything == true;
                    if (drawsEffects) withEffects++;
                    if (effects?.DepthFog.Enabled == true) depthFog++;
                    if (effects?.HeightFog.Enabled == true) heightFog++;
                    if (effects?.DepthOfField.Enabled == true) dof++;
                    if (drawsSsao) ssao++;

                    if (drawsEffects || drawsSsao)
                    {
                        Console.WriteLine($"[MapEnvironment] {map.Value}");
                        if (effects?.DepthFog.Enabled == true)
                            Console.WriteLine($"  depthFog start={effects.DepthFog.Start} end={effects.DepthFog.End} max={effects.DepthFog.MaxIntensity} color={effects.DepthFog.Color}");
                        if (effects?.HeightFog.Enabled == true)
                            Console.WriteLine($"  heightFog start={effects.HeightFog.Start} end={effects.HeightFog.End} max={effects.HeightFog.MaxIntensity} color={effects.HeightFog.Color}");
                        if (effects?.DepthOfField.Enabled == true)
                            Console.WriteLine($"  dof focal={effects.DepthOfField.FocalDistance} width={effects.DepthOfField.InFocusWidth} coc={effects.DepthOfField.Coc}");
                        if (drawsSsao)
                            Console.WriteLine($"  ssao samples={ambient.SampleCount} radius={ambient.SampleRadius} bias={ambient.Bias} power={ambient.Power} intensity={ambient.Intensity} scale={ambient.BufferScale} edgeAware={ambient.EdgeAwareBlur}");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"[MapEnvironment] FAIL {path}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Console.WriteLine(
                $"[MapEnvironment] files={files.Length}, parsed={parsed}, failed={failed}, " +
                $"postEffects={withEffects}, depthFog={depthFog}, heightFog={heightFog}, dof={dof}, ssao={ssao}.");
        }
    }
}
