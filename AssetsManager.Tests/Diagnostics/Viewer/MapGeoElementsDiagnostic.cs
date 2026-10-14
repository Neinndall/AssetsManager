using System;
using System.IO;
using System.Linq;
using System.Numerics;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Memory;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>Lists the vertex elements of MAPGEO meshes whose material matches a filter, with sample values.</summary>
    internal static class MapGeoElementsDiagnostic
    {
        internal static void Run(string[] args)
        {
            if (args.Length < 1 || !File.Exists(args[0]))
            {
                Console.WriteLine("Usage: mapgeo-elements <mapgeo> [material-filter]");
                return;
            }

            string filter = args.Length > 1 ? args[1] : null;
            using var stream = File.OpenRead(args[0]);
            using var asset = new EnvironmentAsset(stream);
            var layouts = asset.Meshes
                .Where(mesh => filter == null ||
                               mesh.Submeshes.Any(sub => sub.Material?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
                .GroupBy(mesh => string.Join(", ", mesh.VerticesView.Buffers
                    .SelectMany(buffer => buffer.Description.Elements)
                    .Select(element => $"{element.Name}:{element.Format}")));
            foreach (var layout in layouts)
            {
                Console.WriteLine($"[Layout] meshes={layout.Count()} elements={layout.Key}");
                EnvironmentAssetMesh sample = layout.First();
                Console.WriteLine($"   sample material={sample.Submeshes.First().Material} transformIdentity={sample.Transform.IsIdentity}");
                if (sample.VerticesView.TryGetAccessor(ElementName.Texcoord5, out VertexElementAccessor pivot))
                {
                    string values = pivot.Element.Format switch
                    {
                        ElementFormat.XYZ_Float32 => string.Join(" ", pivot.AsVector3Array().ToArray().Take(3)),
                        ElementFormat.XY_Float32 => string.Join(" ", pivot.AsVector2Array().ToArray().Take(3)),
                        _ => pivot.Element.Format.ToString()
                    };
                    Vector3 position = sample.VerticesView.TryGetAccessor(ElementName.Position, out var positions)
                        ? positions.AsVector3Array()[0]
                        : default;
                    Console.WriteLine($"   texcoord5[0..2]={values} position[0]={position}");
                }
                if (sample.VerticesView.TryGetAccessor(ElementName.PrimaryColor, out VertexElementAccessor color))
                {
                    var colors = color.AsBgraU8Array();
                    Console.WriteLine($"   color[0..2]={string.Join(" ", Enumerable.Range(0, Math.Min(3, colors.Count)).Select(i => colors[i].ToString()))}");
                }
            }
        }
    }
}
