using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Tests.Support;
using System.Numerics;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `vfx-mesh-color-census [path-filter] [--skn]`: reads every installed particle SCB/SCO (paths under a Particles folder),
    /// or with --skn every character SKN, and counts those whose vertex colours are not opaque white, the meshes
    /// USE_VERTEX_COLORS changes. Examples are limited to paths containing the filter when one is given.
    /// </summary>
    internal static class VfxMeshColorCensusDiagnostic
    {
        public static void Run(string[] args)
        {
            bool skins = args.Contains("--skn");
            string filter = args.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal))?.ToLowerInvariant();
            string hashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            var meshPaths = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.game.txt")))
            {
                int space = line.IndexOf(' ');
                if (space <= 0) continue;
                string path = line[(space + 1)..];
                bool wanted = skins
                    ? path.StartsWith("assets/characters/", StringComparison.Ordinal) && path.EndsWith(".skn", StringComparison.Ordinal)
                    : path.Contains("/particles/", StringComparison.Ordinal) &&
                      (path.EndsWith(".scb", StringComparison.Ordinal) || path.EndsWith(".sco", StringComparison.Ordinal));
                if (!wanted) continue;
                if (ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    meshPaths[hash] = path;
            }

            var seen = new HashSet<ulong>();
            int total = 0, colored = 0, alphaFaded = 0, tinted = 0, failed = 0;
            var examples = new List<string>();
            string final = Path.Combine(InstalledSkins.FindInstall(), @"Game\DATA\FINAL");
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client", SearchOption.AllDirectories))
            {
                using var wad = new WadFile(wadPath);
                foreach (ulong chunk in wad.Chunks.Keys)
                {
                    if (!meshPaths.TryGetValue(chunk, out string path) || !seen.Add(chunk)) continue;
                    total++;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                        Vector4[] colors = skins ? SkinColors(stream) : StaticColors(stream, path);
                        if (colors == null || colors.Length == 0) continue;
                        bool faded = colors.Any(color => color.W < 0.999f);
                        bool tint = colors.Any(color => color.X < 0.999f || color.Y < 0.999f || color.Z < 0.999f);
                        if (!faded && !tint) continue;
                        colored++;
                        if (faded) alphaFaded++;
                        if (tint) tinted++;
                        if (examples.Count < 15 && (filter == null || path.Contains(filter, StringComparison.Ordinal))) examples.Add($"{path} alpha={(faded ? "fades" : "opaque")} rgb={(tint ? "tinted" : "white")}");
                    }
                    catch
                    {
                        failed++;
                    }
                }
            }

            Console.WriteLine($"[MeshColor] {(skins ? "character skins" : "particle meshes")}={total} non-white vertex colours={colored} (alpha fades={alphaFaded}, rgb tinted={tinted}) unreadable={failed}");
            foreach (string example in examples)
                Console.WriteLine($"[MeshColor]   {example}");
        }

        private static Vector4[] StaticColors(Stream stream, string path)
        {
            StaticMesh mesh = path.EndsWith(".sco", StringComparison.Ordinal) ? StaticMesh.ReadAscii(stream) : StaticMesh.ReadBinary(stream);
            return mesh.HasVertexColors ? mesh.VertexColors.Select(color => new Vector4(color.R, color.G, color.B, color.A)).ToArray() : null;
        }

        private static Vector4[] SkinColors(Stream stream)
        {
            using SkinnedMesh mesh = SkinnedMesh.ReadFromSimpleSkin(stream);
            return mesh.VerticesView.TryGetAccessor(ElementName.PrimaryColor, out VertexElementAccessor accessor)
                ? accessor.AsBgraU8Array().ToArray().Select(color => new Vector4(color.r, color.g, color.b, color.a) / 255f).ToArray()
                : null;
        }
    }
}
