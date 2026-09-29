using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Tests.Support;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `vfx-mesh-color-census`: reads every installed particle SCB/SCO (paths under a Particles folder) and counts
    /// those whose vertex colours are not opaque white, the meshes USE_VERTEX_COLORS changes.
    /// </summary>
    internal static class VfxMeshColorCensusDiagnostic
    {
        public static void Run(string[] args)
        {
            string hashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            var meshPaths = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.game.txt")))
            {
                int space = line.IndexOf(' ');
                if (space <= 0) continue;
                string path = line[(space + 1)..];
                if (!path.Contains("/particles/", StringComparison.Ordinal) ||
                    !(path.EndsWith(".scb", StringComparison.Ordinal) || path.EndsWith(".sco", StringComparison.Ordinal)))
                    continue;
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
                        StaticMesh mesh = path.EndsWith(".sco", StringComparison.Ordinal)
                            ? StaticMesh.ReadAscii(stream)
                            : StaticMesh.ReadBinary(stream);
                        if (!mesh.HasVertexColors || mesh.VertexColors.Count == 0) continue;
                        bool faded = mesh.VertexColors.Any(color => color.A < 0.999f);
                        bool tint = mesh.VertexColors.Any(color => color.R < 0.999f || color.G < 0.999f || color.B < 0.999f);
                        if (!faded && !tint) continue;
                        colored++;
                        if (faded) alphaFaded++;
                        if (tint) tinted++;
                        if (examples.Count < 15) examples.Add($"{path} alpha={(faded ? "fades" : "opaque")} rgb={(tint ? "tinted" : "white")}");
                    }
                    catch
                    {
                        failed++;
                    }
                }
            }

            Console.WriteLine($"[MeshColor] particle meshes={total} non-white vertex colours={colored} (alpha fades={alphaFaded}, rgb tinted={tinted}) unreadable={failed}");
            foreach (string example in examples)
                Console.WriteLine($"[MeshColor]   {example}");
        }
    }
}
