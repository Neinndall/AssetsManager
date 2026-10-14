using System;
using System.IO;
using System.Linq;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Semantics;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    internal static class MapLayersAuditDiagnostic
    {
        internal static void Run(string[] args)
        {
            if (args.Length != 2 || !File.Exists(args[0]) || !File.Exists(args[1]))
            {
                Console.WriteLine("Usage: map-layers-audit <mapgeo-path> <materials-bin-path>");
                return;
            }

            using var geometryStream = File.OpenRead(args[0]);
            var geometry = new MapGeometryDecoder().Decode(geometryStream);
            using var materialsStream = File.OpenRead(args[1]);
            var tree = new BinTree(materialsStream);
            var chunks = new MapPlaceableParser().Parse(tree);
            var characters = new MapCharacterParser().Parse(chunks);
            var particles = new MapParticleParser().Parse(chunks);
            var placeables = chunks.SelectMany(chunk => chunk.Items).ToArray();

            Console.WriteLine($"[Layers] geometry={Path.GetFileName(args[0])} meshes={geometry.Meshes.Count} opening=0x{MapGeometrySemantics.OpeningFlags(geometry):x2}");
            foreach (var group in geometry.Meshes.GroupBy(mesh => mesh.Visibility).OrderBy(group => group.Key))
                Console.WriteLine($"[Mask] 0x{group.Key:x2} meshes={group.Count()} controllers={group.Count(mesh => mesh.VisibilityControllerPathHash != 0)}");
            Console.WriteLine($"[Placeables] total={placeables.Length} characters={characters.Count} particles={particles.Count} controlled={placeables.Count(item => item.VisibilityController.HasValue)}");

            foreach (var layer in MapGeometrySemantics.Layers(geometry))
            {
                int flags = layer.Flag;
                var drawn = MapGeometrySemantics.DrawnMeshesForFlags(geometry, flags).ToArray();
                var stood = MapCharacterSemantics.StoodForFlags(characters, flags);
                var played = MapParticleSemantics.PlayedForFlags(particles, flags);
                var authoredParticles = particles.Where(item => item.Placeable.IsVisibleForFlags(flags)).ToArray();
                string materials = string.Join(" | ", drawn
                    .SelectMany(mesh => geometry.Submeshes.Skip(mesh.FirstSubmesh).Take(mesh.SubmeshCount))
                    .Select(run => geometry.Materials[run.MaterialIndex])
                    .Where(IsVariantRelated)
                    .Distinct()
                    .OrderBy(path => path));
                Console.WriteLine($"[Layer] {layer.Index + 1} flags=0x{flags:x2} meshes={drawn.Length} triangles={layer.Triangles} structures={stood.Count} particles={played.Count}/{authoredParticles.Length} origin={MapGeometrySemantics.CalculateOriginForFlags(geometry, flags)}");
                Console.WriteLine($"[Materials] {materials}");
            }

            foreach (var mesh in geometry.Meshes)
            {
                string[] names = geometry.Submeshes.Skip(mesh.FirstSubmesh).Take(mesh.SubmeshCount)
                    .Select(run => geometry.Materials[run.MaterialIndex])
                    .Where(path => path.Contains("Baron", StringComparison.OrdinalIgnoreCase))
                    .Distinct().ToArray();
                if (names.Length > 0)
                    Console.WriteLine($"[BaronMesh] mask=0x{mesh.Visibility:x2} controller={mesh.VisibilityControllerPathHash:x8} materials={string.Join(" | ", names)}");
            }

            foreach (var particle in particles.Where(item => IsVariantRelated(item.Name)))
                Console.WriteLine($"[VariantParticle] {particle.Name} mask=0x{particle.Placeable.Visibility:x2} controller={particle.VisibilityController?.ToString("x8") ?? "-"} transitional={particle.Transitional} startDisabled={particle.StartDisabled}");
            foreach (var character in characters.Where(item => IsVariantRelated(item.Name) || IsVariantRelated(item.Skin)))
                Console.WriteLine($"[VariantCharacter] {character.Name} skin={character.Skin} mask=0x{character.Placeable.Visibility:x2} controller={character.VisibilityController?.ToString("x8") ?? "-"} team={character.Team}");

            Console.WriteLine("[Layers] Counts describe authored masks and the current preview filters. They do not evaluate gameplay visibility controllers.");
        }

        private static bool IsVariantRelated(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string[] terms = { "dragon", "baron", "infernal", "mountain", "ocean", "cloud", "hextech", "chemtech", "void", "atakhan" };
            return terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
    }
}
