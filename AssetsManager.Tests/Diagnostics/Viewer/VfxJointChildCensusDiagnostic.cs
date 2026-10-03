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
    /// <summary>Counts authored bone-child parents across installed champion and map BINs.</summary>
    internal static class VfxJointChildCensusDiagnostic
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

            var counts = new Dictionary<string, int>();
            var owners = new HashSet<uint>();
            var examples = new List<string>();
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
                        if (emitter.Disabled || emitter.ChildParticleSet?.Bones is not { Count: > 0 }) continue;
                        string kind = emitter.PrimitiveKind.ToString();
                        counts[kind] = counts.GetValueOrDefault(kind) + 1;
                        owners.Add(system.PathHash);
                        if (examples.Count < 30)
                            examples.Add($"{path} {system.Name}/{emitter.Name} kind={kind} birthScale={emitter.BirthScale.Constant} scaleOverLife={emitter.ScaleOverLife?.Constant} mesh={emitter.MeshPath}");
                    }
                }
            }
            Console.WriteLine($"[JointChildCensus] systems={owners.Count} emitters={counts.Values.Sum()}");
            foreach (var pair in counts.OrderByDescending(pair => pair.Value))
                Console.WriteLine($"[JointChildCensus] {pair.Key}={pair.Value}");
            foreach (string example in examples) Console.WriteLine($"[JointChildCensus] {example}");
            foreach (WadFile wad in wads) wad.Dispose();
        }
    }
}
