using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Tests.Support;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Wad;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `mapgeo-heights <Map11|Map12|...>`: the height (Y) distribution of every vertex of the installed map's MAPGEO
    /// files, as percentiles, to judge how far the map surface sits from the flat ground at height 0.
    /// </summary>
    internal static class MapGeoHeightsDiagnostic
    {
        internal static void Run(string[] args)
        {
            string map = args.Length > 0 ? args[0] : "Map11";
            string hashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            var paths = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.game.txt")))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && line.EndsWith(".mapgeo", StringComparison.Ordinal) &&
                    ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    paths[hash] = line[(space + 1)..];
            }

            string wadPath = Path.Combine(InstalledSkins.FindInstall(), @"Game\DATA\FINAL\Maps\Shipping", map + ".wad.client");
            using var wad = new WadFile(wadPath);
            foreach (ulong chunk in wad.Chunks.Keys.Where(paths.ContainsKey))
            {
                using var data = wad.LoadChunkDecompressed(chunk);
                using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                EnvironmentAsset asset;
                try { asset = new EnvironmentAsset(stream); }
                catch (Exception ex) { Console.WriteLine($"[Heights] {paths[chunk]}: {ex.GetType().Name}"); continue; }

                using (asset)
                {
                    var heights = new List<float>();
                    foreach (EnvironmentAssetMesh mesh in asset.Meshes)
                    {
                        if (!mesh.VerticesView.TryGetAccessor(ElementName.Position, out var positions)) continue;
                        foreach (Vector3 position in positions.AsVector3Array())
                            heights.Add(Vector3.Transform(position, mesh.Transform).Y);
                    }
                    if (heights.Count == 0) continue;

                    heights.Sort();
                    string Percentile(double p) => heights[(int)Math.Min(heights.Count - 1, p * heights.Count)].ToString("0", CultureInfo.InvariantCulture);
                    int nearZero = heights.Count(y => Math.Abs(y) <= 10f);
                    Console.WriteLine(
                        $"[Heights] {paths[chunk]} vertices={heights.Count} min={Percentile(0)} p5={Percentile(0.05)} p25={Percentile(0.25)} " +
                        $"median={Percentile(0.5)} p75={Percentile(0.75)} p95={Percentile(0.95)} max={Percentile(1)} within10ofZero={100.0 * nearZero / heights.Count:0.0}%");
                }
            }
        }
    }
}
