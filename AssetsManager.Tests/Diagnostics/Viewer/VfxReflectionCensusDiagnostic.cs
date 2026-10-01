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
    /// `vfx-reflection-census`: the non-mesh VFX emitters of the installed skin and map BINs that carry a
    /// <c>reflectionDefinition</c> or a screen-space UV mode, by primitive, with what the reflection can add:
    /// a cubemap, a rim (fresnel with a non-black colour) or a direct/glancing opacity.
    /// </summary>
    internal static class VfxReflectionCensusDiagnostic
    {
        public static void Run(string[] args)
        {
            string install = InstalledSkins.FindInstall();
            string hashes = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes", "hashes.game.txt");
            var paths = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(hashes))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && line.EndsWith(".bin", StringComparison.Ordinal) &&
                    ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    paths[hash] = line[(space + 1)..];
            }

            string final = Path.Combine(install, @"Game\DATA\FINAL");
            var wads = new List<WadFile>();
            var where = new Dictionary<ulong, WadFile>();
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client", SearchOption.AllDirectories)
                         .Where(path => path.Contains(@"\Champions\") || path.Contains(@"\Maps\"))
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                var wad = new WadFile(wadPath);
                wads.Add(wad);
                foreach (ulong hash in wad.Chunks.Keys)
                    if (paths.ContainsKey(hash)) where.TryAdd(hash, wad);
            }

            var reflective = new Dictionary<string, int[]>(StringComparer.Ordinal);
            var screenSpace = new Dictionary<string, int>(StringComparer.Ordinal);
            var examples = new List<string>();
            var foldedFlipbooks = new Dictionary<string, int>(StringComparer.Ordinal);
            var foldExamples = new List<string>();
            foreach ((ulong hash, string path) in paths.Where(pair => where.ContainsKey(pair.Key) &&
                         (System.Text.RegularExpressions.Regex.IsMatch(pair.Value, @"^data/characters/[^/]+/skins/skin\d+\.bin$") ||
                          pair.Value.EndsWith(".materials.bin", StringComparison.Ordinal))))
            {
                BinTree tree;
                try
                {
                    using var data = where[hash].LoadChunkDecompressed(hash);
                    using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                    tree = new BinTree(stream);
                }
                catch { continue; }

                IReadOnlyDictionary<uint, VfxSystemDefinition> systems;
                try { systems = VfxSystemParser.ExtractAll(tree); }
                catch { continue; }

                foreach (VfxSystemDefinition system in systems.Values)
                {
                    foreach (VfxEmitterDefinition emitter in system.Emitters ?? Array.Empty<VfxEmitterDefinition>())
                    {
                        if (emitter.Disabled || emitter.IsMeshPrimitive || emitter.Distortion is not null) continue;
                        string kind = emitter.PrimitiveKind.ToString();
                        if (emitter.UvMode == 1)
                            screenSpace[kind] = screenSpace.GetValueOrDefault(kind) + 1;
                        bool stock = emitter.UvMode is 1 or 2 || emitter.Reflection is not null;
                        bool flipbook = emitter.TexDiv.X * emitter.TexDiv.Y > 1.5f;
                        bool movesUv = emitter.UvScrollRate != System.Numerics.Vector2.Zero ||
                                       emitter.EmitterUvScrollRate != System.Numerics.Vector2.Zero ||
                                       emitter.BirthUvScrollRateCurve is not null || emitter.ParticleUvScrollRate is not null ||
                                       emitter.UvScale is not null || emitter.UvRotation is not null ||
                                       emitter.BirthUvRotateRate is not null || emitter.ParticleUvRotateRate is not null ||
                                       emitter.BirthUvOffset is not null;
                        if (stock && flipbook)
                        {
                            string key = $"{kind} address={emitter.RenderState?.TextureAddressMode ?? 0} movesUv={movesUv}";
                            foldedFlipbooks[key] = foldedFlipbooks.GetValueOrDefault(key) + 1;
                            if (movesUv && foldExamples.Count < 6)
                                foldExamples.Add($"{path} {system.Name}/{emitter.Name} texDiv={emitter.TexDiv}");
                        }

                        VfxReflectionDefinition reflection = emitter.Reflection;
                        if (reflection is null) continue;
                        if (!reflective.TryGetValue(kind, out int[] counts))
                            reflective[kind] = counts = new int[5];
                        bool cubemap = !string.IsNullOrWhiteSpace(reflection.TexturePath);
                        bool rim = reflection.Fresnel > 0f &&
                                   (reflection.FresnelColor.X + reflection.FresnelColor.Y + reflection.FresnelColor.Z) > 0f;
                        bool opacity = reflection.DirectOpacity > 0f || reflection.GlancingOpacity > 0f;
                        counts[0]++;
                        if (cubemap) counts[1]++;
                        if (rim) counts[2]++;
                        if (cubemap && opacity) counts[3]++;
                        if (!cubemap && !rim) counts[4]++;
                        if (cubemap && examples.Count < 12)
                            examples.Add($"{kind} {path} {system.Name}/{emitter.Name} cube={reflection.TexturePath} direct={reflection.DirectOpacity} glancing={reflection.GlancingOpacity} fresnel={reflection.Fresnel}");
                    }
                }
            }

            Console.WriteLine("[ReflectionCensus] primitive: reflective / with cubemap / with rim / cubemap+opacity / nothing to add");
            foreach ((string kind, int[] counts) in reflective.OrderByDescending(pair => pair.Value[0]))
                Console.WriteLine($"[ReflectionCensus] {kind}: {counts[0]} / {counts[1]} / {counts[2]} / {counts[3]} / {counts[4]}");
            foreach ((string kind, int count) in screenSpace.OrderByDescending(pair => pair.Value))
                Console.WriteLine($"[ReflectionCensus] UvMode 1 {kind}: {count}");
            foreach (string line in examples) Console.WriteLine($"[ReflectionCensus] cubemap {line}");
            foreach ((string key, int count) in foldedFlipbooks.OrderByDescending(pair => pair.Value))
                Console.WriteLine($"[ReflectionCensus] stock flipbook {key}: {count}");
            foreach (string line in foldExamples) Console.WriteLine($"[ReflectionCensus] stock flipbook moving uv {line}");
            foreach (WadFile wad in wads) wad.Dispose();
        }
    }
}
