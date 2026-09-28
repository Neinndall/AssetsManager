using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Semantics;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// Evaluates the map visibility contract (domains + controller graph) against shipped files and
    /// reports what each transformation and secondary state draws. Optionally dumps both BINs as Ritobin.
    /// </summary>
    internal static class MapControllersAuditDiagnostic
    {
        internal static async Task Run(string[] args)
        {
            if (args.Length < 3 || !File.Exists(args[0]) || !File.Exists(args[1]) || !File.Exists(args[2]))
            {
                Console.WriteLine("Usage: map-controllers-audit <mapgeo-path> <materials-bin-path> <map-bin-path> [ritobin-output-dir]");
                return;
            }

            var log = new LogService(new Serilog.LoggerConfiguration().CreateLogger());
            using var resolver = new HashResolverService(new DirectoriesCreator(), log);
            await resolver.LoadAllHashesAsync();

            if (args.Length > 3)
            {
                string output = Directory.CreateDirectory(args[3]).FullName;
                var serializer = new BinRitobinSerializer(resolver);
                foreach (string bin in new[] { args[1], args[2] })
                {
                    string target = Path.Combine(output, Path.GetFileName(bin) + ".ritobin.txt");
                    File.WriteAllText(target, await serializer.WriteBinTreeAsRitobinAsync(File.ReadAllBytes(bin)));
                    Console.WriteLine($"[Ritobin] {target}");
                }
            }

            MapGeometryData geometry;
            using (var stream = File.OpenRead(args[0]))
                geometry = new MapGeometryDecoder().Decode(stream);
            BinTree materials;
            using (var stream = File.OpenRead(args[1]))
                materials = new BinTree(stream);
            BinTree map;
            using (var stream = File.OpenRead(args[2]))
                map = new BinTree(stream);

            var parser = new MapVisibilityParser();
            MapVisibilityDefinitions definitions = parser.ParseDefinitions(map, resolver.ResolveBinHash);
            var controllers = parser.ParseControllers(materials);
            var visibility = new MapSceneVisibility(
                definitions,
                controllers,
                MapVisibilitySemantics.Opening(definitions, MapGeometrySemantics.OpeningFlags(geometry)));
            var chunks = new MapPlaceableParser().Parse(materials);
            var characters = new MapCharacterParser().Parse(chunks);
            var particles = new MapParticleParser().Parse(chunks);

            Console.WriteLine($"[Visibility] opening=({visibility.Opening}) controllers={controllers.Count} " +
                              $"kinds={string.Join(", ", controllers.Values.GroupBy(c => c.Kind).Select(g => $"{g.Key}:{g.Count()}"))}");
            PrintDomain("Primary", definitions.Primary);
            PrintDomain("Secondary", definitions.Secondary);
            Console.WriteLine($"[Mutators] {string.Join(", ", visibility.MutatorNames)}");

            var states = new List<(string Label, MapVisibilityState State)> { ("Base", visibility.Opening) };
            if (definitions.Primary is { IsEmpty: false } primary)
            {
                foreach (MapVisibilityFlagData flag in primary.Flags.Where(flag => (primary.InitialMask & flag.Flag) == 0))
                    states.Add((flag.Label, visibility.Opening.WithFlags(MapVisibilitySemantics.TransformationFlags(primary, flag.BitIndex))));
            }
            if (definitions.Secondary is { IsEmpty: false } secondary)
            {
                foreach (MapVisibilityFlagData flag in secondary.Flags.Where(flag => flag.Flag != visibility.Opening.SecondaryFlags))
                    states.Add(($"Secondary {flag.Label}", visibility.Opening.WithSecondaryFlags(flag.Flag)));
            }
            foreach (string mutator in visibility.MutatorNames)
                states.Add(($"Mutator {mutator}", visibility.Opening.WithMutator(mutator, true)));

            HashSet<string> baseline = DrawnMaterials(geometry, visibility, visibility.Opening);
            foreach ((string label, MapVisibilityState state) in states)
            {
                HashSet<string> drawn = DrawnMaterials(geometry, visibility, state);
                int meshes = MapGeometrySemantics.DrawnMeshesFor(geometry, visibility, state).Count();
                int structures = MapCharacterSemantics.StoodFor(characters, visibility, state).Count;
                var played = MapParticleSemantics.PlayedFor(particles, visibility, state);
                Console.WriteLine($"[State] {label} ({state}) meshes={meshes} structures={structures} particles={played.Count}");
                if (!ReferenceEquals(state, visibility.Opening))
                {
                    Console.WriteLine($"   + {Summarize(drawn.Except(baseline))}");
                    Console.WriteLine($"   - {Summarize(baseline.Except(drawn))}");
                    var baseParticles = MapParticleSemantics.PlayedFor(particles, visibility, visibility.Opening).Select(p => p.Name).ToHashSet();
                    Console.WriteLine($"   +vfx {Summarize(played.Select(p => p.Name).Where(name => !baseParticles.Contains(name)))}");
                }
            }
        }

        private static void PrintDomain(string label, MapVisibilityDomainData domain)
        {
            if (domain == null)
            {
                Console.WriteLine($"[{label}] none");
                return;
            }
            Console.WriteLine($"[{label}] initial=0x{domain.InitialMask:x2} range={domain.MinIndex}..{domain.MaxIndex} " +
                              string.Join(", ", domain.Flags.Select(flag => $"{flag.BitIndex}:{flag.Label}")));
        }

        private static HashSet<string> DrawnMaterials(MapGeometryData geometry, MapSceneVisibility visibility, MapVisibilityState state) =>
            MapGeometrySemantics.DrawnMeshesFor(geometry, visibility, state)
                .SelectMany(mesh => geometry.Submeshes.Skip(mesh.FirstSubmesh).Take(mesh.SubmeshCount))
                .Select(run => geometry.Materials[run.MaterialIndex].Split('/').Last())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        private static string Summarize(IEnumerable<string> values)
        {
            string[] list = values.Distinct().OrderBy(value => value).ToArray();
            return list.Length == 0 ? "(none)" : $"{list.Length}: {string.Join(", ", list.Take(14))}{(list.Length > 14 ? ", ..." : "")}";
        }
    }
}
