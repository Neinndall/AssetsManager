using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `vfx-emission-window-census`: for every emitter of the installed champion and map BINs that authors both
    /// timeBeforeFirstEmission and lifetime, how the delay compares with the lifetime. Emitters whose delay
    /// reaches their lifetime never emit if the lifetime counts from the system's start.
    /// </summary>
    internal static class VfxEmissionWindowCensusDiagnostic
    {
        public static void Run(string[] args)
        {
            string hashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            var binPaths = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.game.txt")))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && line.EndsWith(".bin", StringComparison.Ordinal) && !line.Contains("/animations/") &&
                    ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    binPaths[hash] = line[(space + 1)..];
            }
            var seen = new HashSet<uint>();
            int both = 0, delayReaches = 0, delayWithin = 0, single = 0, singleReaches = 0;
            var ratios = new List<float>();
            var examples = new List<string>();
            string final = Path.Combine(InstalledSkins.FindInstall(), @"Game\DATA\FINAL");
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client", SearchOption.AllDirectories)
                         .Where(path => path.Contains(@"\Champions\") || path.Contains(@"\Maps\"))
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                using var wad = new WadFile(wadPath);
                foreach (ulong chunk in wad.Chunks.Keys)
                {
                    if (!binPaths.TryGetValue(chunk, out string binPath)) continue;
                    IReadOnlyDictionary<uint, VfxSystemDefinition> systems;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                        systems = VfxSystemParser.ExtractAll(new BinTree(stream));
                    }
                    catch { continue; }
                    foreach ((uint hash, VfxSystemDefinition system) in systems)
                    {
                        if (!seen.Add(hash)) continue;
                        foreach (VfxEmitterDefinition emitter in system.Emitters ?? Array.Empty<VfxEmitterDefinition>())
                        {
                            if (emitter.Disabled || emitter.TimeBeforeFirstEmission <= 0f || emitter.EmitterLifetime is not > 0f) continue;
                            float life = emitter.EmitterLifetime.Value;
                            both++;
                            if (emitter.IsSingleParticle) single++;
                            ratios.Add(emitter.TimeBeforeFirstEmission / life);
                            if (emitter.TimeBeforeFirstEmission > life)
                            {
                                delayReaches++;
                                if (emitter.IsSingleParticle) singleReaches++;
                                if (examples.Count < 12) examples.Add($"{binPath} {system.Name}/{emitter.Name} delay={emitter.TimeBeforeFirstEmission} life={life} single={emitter.IsSingleParticle}");
                            }
                            else delayWithin++;
                        }
                    }
                }
            }
            ratios.Sort();
            Console.WriteLine($"[Window] emitters with delay and lifetime={both} (single-particle {single})");
            Console.WriteLine($"[Window] delay > lifetime: {delayReaches} (single-particle {singleReaches}); delay <= lifetime: {delayWithin}");
            if (ratios.Count > 0)
                Console.WriteLine($"[Window] delay/lifetime quantiles: p10={ratios[ratios.Count / 10]:0.00} p50={ratios[ratios.Count / 2]:0.00} p90={ratios[ratios.Count * 9 / 10]:0.00}");
            foreach (string example in examples) Console.WriteLine($"[Window] e.g. {example}");
        }
    }
}
