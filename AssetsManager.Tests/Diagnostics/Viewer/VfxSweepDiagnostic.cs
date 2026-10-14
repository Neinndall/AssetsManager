using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Tests.Support;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using Silk.NET.OpenGL;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `vfx-sweep [--champions] [--maps] [--filter TEXT] [--max-skins N] [--max-bins N] [--max-systems N] [--max-seconds N] [--custom-only] [--no-owner] [--csv FILE [--resume]]`:
    /// loads VFX systems of the installed BINs (skin BINs, the shared character BINs they link, map materials) the way
    /// 3D Studio does and flags, per emitter:
    /// resources it authors that the engine cannot load (split into absent from the game, not extracted and not
    /// decoded), emitters drawn with the stock program instead of the game's and why, root emitters that emit
    /// nothing during the sampled seconds (4 by default), child emitters alive past their first emission without a
    /// particle, particles with non-finite or runaway positions, and emitters holding more than 2000 live particles.
    /// With --csv each BIN's findings are appended as soon as it is swept and the BIN is logged in FILE.done with the
    /// systems it covered, so a stopped sweep continues with --resume and the summary covers the whole CSV.
    /// </summary>
    internal static class VfxSweepDiagnostic
    {
        private sealed record Finding(string Flag, string Bin, string System, string Emitter, string Detail);

        private const float SampleStep = 1f / 30f;
        private const float DefaultSampleSeconds = 4f;
        private const float RunawayDistance = 50000f;
        private const int FloodCount = 2000;

        public static void Run(string[] args)
        {
            bool champions = args.Contains("--champions") || !args.Contains("--maps");
            bool maps = args.Contains("--maps");
            string filter = Option(args, "--filter");
            int maxSkins = int.TryParse(Option(args, "--max-skins"), out int skins) ? skins : 1;
            int maxBins = int.TryParse(Option(args, "--max-bins"), out int bins) ? bins : int.MaxValue;
            int maxSystems = int.TryParse(Option(args, "--max-systems"), out int systemsCap) ? systemsCap : 60;
            float maxSeconds = float.TryParse(Option(args, "--max-seconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out float seconds) && seconds > 0f
                ? seconds
                : DefaultSampleSeconds;
            string csvPath = Option(args, "--csv");
            bool resume = args.Contains("--resume");
            string donePath = csvPath == null ? null : csvPath + ".done";
            var doneBins = new HashSet<string>(StringComparer.Ordinal);
            var resumedSystems = new HashSet<uint>();
            if (csvPath != null && resume && File.Exists(donePath))
            {
                foreach (string line in File.ReadLines(donePath))
                {
                    string[] parts = line.Split('|');
                    doneBins.Add(parts[0]);
                    if (parts.Length > 1)
                        foreach (string hash in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
                            if (uint.TryParse(hash, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint value))
                                resumedSystems.Add(value);
                }
            }
            else if (csvPath != null)
            {
                File.WriteAllText(csvPath, "flag,bin,system,emitter,detail" + Environment.NewLine);
                File.WriteAllText(donePath, string.Empty);
            }
            bool customOnly = args.Contains("--custom-only");

            string install = InstalledSkins.FindInstall();
            AppSettings settings = InstalledSkins.Settings(install);
            var log = new LogService(new Serilog.LoggerConfiguration().CreateLogger());
            string hashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            string final = Path.Combine(install, @"Game\DATA\FINAL");

            // Every chunk the game ships, to tell a resource the game lacks from one we fail to load.
            var wadOf = new Dictionary<ulong, string>();
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client", SearchOption.AllDirectories)
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                using var wad = new WadFile(wadPath);
                foreach (ulong chunk in wad.Chunks.Keys) wadOf.TryAdd(chunk, wadPath);
            }

            var skinPattern = new Regex(@"^data/characters/([a-z0-9_]+)/skins/skin(\d+)\.bin$");
            // Shared BINs beside the skins (e.g. ezreal_multi_skins_*.bin) hold the systems several skins link.
            var sharedPattern = new Regex(@"^data/characters/[a-z0-9_]+/[^/]+\.bin$");
            var perChampion = new Dictionary<string, int>(StringComparer.Ordinal);
            var targets = new List<string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.game.txt")))
            {
                int space = line.IndexOf(' ');
                if (space < 0 || !line.EndsWith(".bin", StringComparison.Ordinal)) continue;
                string path = line[(space + 1)..];
                if (!ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash) ||
                    !wadOf.ContainsKey(hash)) continue;
                if (filter != null && !path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                Match skin = skinPattern.Match(path);
                if (champions && skin.Success && !path.StartsWith("data/characters/tft", StringComparison.Ordinal))
                {
                    int count = perChampion.GetValueOrDefault(skin.Groups[1].Value);
                    if (count >= maxSkins) continue;
                    perChampion[skin.Groups[1].Value] = count + 1;
                    targets.Add(path);
                }
                else if (champions && sharedPattern.IsMatch(path) && !path.StartsWith("data/characters/tft", StringComparison.Ordinal))
                    targets.Add(path);
                else if (maps && path.StartsWith("data/maps/", StringComparison.Ordinal) && path.EndsWith(".materials.bin", StringComparison.Ordinal))
                    targets.Add(path);
            }
            targets = targets.OrderBy(path => path, StringComparer.Ordinal).Take(maxBins).ToList();
            Console.WriteLine($"[VfxSweep] bins={targets.Count} (champions={champions} maps={maps} maxSkins={maxSkins} maxSystems={maxSystems} maxSeconds={maxSeconds})");

            var wadProvider = new WadContentProvider(log, new WadNodeLoaderService(null, log), new DirectoriesCreator(), new SvgParser());
            var resolver = new MapAssetResolver(wadProvider, settings);
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);

            // VFX custom materials find their CustomShaderDefs in the global shader BIN, as the app loads them.
            BinTree shaders = LoadBin(wadOf, "data/shaders/shaders.bin");
            BinTree[] shaderTrees = shaders == null ? null : new[] { shaders };
            var findings = new List<Finding>();
            var seenSystems = new HashSet<uint>(resumedSystems);
            if (doneBins.Count > 0)
                Console.WriteLine($"[VfxSweep] resuming: {doneBins.Count} BINs already swept");
            int systemCount = 0, emitterCount = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (string binPath in targets)
            {
                if (doneBins.Contains(binPath)) continue;
                int firstFinding = findings.Count;
                var binSystems = new List<uint>();
                try
                {
                    SweepBin(binPath, binSystems);
                }
                finally
                {
                    if (csvPath != null)
                    {
                        File.AppendAllLines(csvPath, findings.Skip(firstFinding).Select(CsvRow));
                        File.AppendAllText(donePath, $"{binPath}|{string.Join(",", binSystems.Select(hash => hash.ToString("x8")))}{Environment.NewLine}");
                    }
                }
            }

            void SweepBin(string binPath, List<uint> binSystems)
            {
                BinTree tree = LoadBin(wadOf, binPath);
                if (tree == null) return;
                VfxDiagnosticCatalog catalog;
                try
                {
                    catalog = VfxDiagnosticCatalog.Load(binPath, tree, path => LoadBin(wadOf, path), shaderTrees);
                }
                catch (Exception ex)
                {
                    findings.Add(new Finding("PARSE_FAIL", binPath, "", "", ex.Message));
                    return;
                }
                IReadOnlyDictionary<uint, uint> resourceMap = catalog.ResourceMap;
                IReadOnlyDictionary<uint, VfxSystemDefinition> systems = catalog.Systems;
                VfxSystemDefinition[] chosen = systems
                    .Where(pair => catalog.PrimarySystemHashes.Contains(pair.Key))
                    .Where(pair => !customOnly || pair.Value.Emitters.Any(emitter => emitter.CustomMaterialPathHash != 0))
                    .Where(pair => seenSystems.Add(pair.Key)).Select(pair => pair.Value)
                    .OrderBy(system => system.Name, StringComparer.Ordinal).Take(maxSystems).ToArray();
                binSystems.AddRange(chosen.Select(system => system.PathHash));
                if (chosen.Length == 0) return;

                VfxOwnerSceneContext owner = args.Contains("--no-owner") ? null : catalog.Owner;
                VfxSceneResourceContext resources;
                try
                {
                    resources = VfxSceneResourceContext.CreateAsync(
                        VfxSceneResourceContext.ReachableSystems(systems, resourceMap, chosen), null, resolver, null, log, ownerSceneContext: owner)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    findings.Add(new Finding("EXTRACT_FAIL", binPath, "", "", ex.Message));
                    return;
                }

                using (resources)
                {
                    var session = new VfxRenderSession(log);
                    session.Initialize(gl, settings);
                    session.SetWorldTransform(Matrix4x4.Identity);
                    session.SetViewportSize(256, 256);
                    foreach (VfxSystemDefinition system in chosen)
                    {
                        systemCount++;
                        emitterCount += system.Emitters?.Count ?? 0;
                        try
                        {
                            Check(session, system, systems, resourceMap, resources.SearchDirectory, binPath, wadOf, findings, maxSeconds, owner);
                        }
                        catch (Exception ex)
                        {
                            findings.Add(new Finding("RUNTIME_THROW", binPath, system.Name, "", ex.GetType().Name + ": " + ex.Message));
                        }
                    }
                    session.Dispose();
                }
                Console.WriteLine($"[VfxSweep] {binPath} systems={chosen.Length} findings={findings.Count} elapsed={clock.Elapsed:mm\\:ss}");
            }

            Console.WriteLine($"[VfxSweep] systems={systemCount} emitters={emitterCount} findings={findings.Count} elapsed={clock.Elapsed:hh\\:mm\\:ss}");
            // A CSV holds every run of a resumed sweep; summarize all of it.
            IReadOnlyList<Finding> all = csvPath != null ? ReadCsv(csvPath) : findings;
            foreach (IGrouping<string, Finding> flag in all.GroupBy(finding => finding.Flag).OrderByDescending(group => group.Count()))
            {
                Console.WriteLine($"[VfxSweep] {flag.Key} x{flag.Count()} systems={flag.Select(f => f.Bin + f.System).Distinct().Count()}");
                foreach (IGrouping<string, Finding> detail in flag.GroupBy(finding => finding.Detail).OrderByDescending(group => group.Count()).Take(8))
                {
                    Finding example = detail.First();
                    Console.WriteLine($"[VfxSweep]   x{detail.Count()} {Clip(detail.Key, 140)} e.g. {example.Bin} {example.System}/{example.Emitter}");
                }
            }
            if (csvPath != null)
                Console.WriteLine($"[VfxSweep] csv={csvPath} findings={all.Count}");
        }

        private static string CsvRow(Finding finding) =>
            $"{finding.Flag},{finding.Bin},\"{finding.System}\",\"{finding.Emitter}\",\"{finding.Detail?.Replace('"', '\'')}\"";

        private static IReadOnlyList<Finding> ReadCsv(string path)
        {
            var rows = new List<Finding>();
            foreach (string line in File.ReadLines(path).Skip(1))
            {
                // flag,bin,"system","emitter","detail": the quoted fields never hold a double quote.
                int firstComma = line.IndexOf(',');
                int secondComma = firstComma < 0 ? -1 : line.IndexOf(',', firstComma + 1);
                if (secondComma < 0) continue;
                string[] quoted = line[(secondComma + 1)..].Split("\",\"");
                if (quoted.Length != 3) continue;
                rows.Add(new Finding(line[..firstComma], line[(firstComma + 1)..secondComma],
                    quoted[0].TrimStart('"'), quoted[1], quoted[2].TrimEnd('"')));
            }
            return rows;
        }

        private static void Check(
            VfxRenderSession session,
            VfxSystemDefinition system,
            IReadOnlyDictionary<uint, VfxSystemDefinition> catalog,
            IReadOnlyDictionary<uint, uint> resourceMap,
            string searchDirectory,
            string binPath,
            IReadOnlyDictionary<ulong, string> wadOf,
            List<Finding> findings,
            float maxSeconds,
            VfxOwnerSceneContext owner)
        {
            session.SetSystem(new VfxSystemModel
            {
                Name = system.Name,
                Definition = system,
                SystemCatalog = catalog,
                ResourceMap = resourceMap,
                SearchDirectory = searchDirectory,
                OwnerSceneContext = owner,
                PlaybackSeed = VfxRenderSession.IdleEffectSeed,
                TotalDuration = VfxDurationCalculator.SystemSpan(system),
                Speed = 1
            });
            void Flag(string flag, VfxEmitterDefinition emitter, string detail) =>
                findings.Add(new Finding(flag, binPath, system.Name, emitter?.Name ?? "", detail));

            // Resources: checked right after SetSystem, before any upload consumes the pending decodes.
            var checkedDefinitions = new HashSet<VfxEmitterDefinition>(ReferenceEqualityComparer.Instance);
            foreach (VfxPlaybackRuntime.EmitterState state in session.Graphs.SelectMany(graph => graph.Runtimes).SelectMany(runtime => runtime.Emitters))
            {
                VfxEmitterDefinition def = state.Def;
                if (!checkedDefinitions.Add(def) || def.Disabled) continue;
                void Resource(string kind, string path, object pending)
                {
                    if (string.IsNullOrWhiteSpace(path) || pending != null) return;
                    Flag(ResourceFlag(path, searchDirectory, wadOf), def, $"{kind} {path}");
                }
                Resource("texture", def.TexturePath, state.PendingTexture);
                Resource("textureMult", def.TextureMultPath, state.PendingTextureMult);
                Resource("distortion", def.Distortion?.NormalMapTexturePath, state.PendingDistortionTexture);
                Resource("erosion", def.AlphaErosion?.TexturePath, state.PendingErosionTexture);
                Resource("reflection", def.Reflection?.TexturePath, state.PendingReflectionTexture);
                Resource("palette", def.PaletteDefinition?.PaletteTexturePath, state.PendingPaletteTexture);
                Resource("colorRamp", def.ParticleColorTexturePath, state.PendingColorGradient);
                if (def.IsMeshPrimitive && def.PrimitiveKind != VfxPrimitiveKind.AttachedMesh)
                    Resource("mesh", def.MeshPath, state.PendingMesh);
                foreach ((string path, object pending) in state.PendingProgramTextures)
                    Resource("materialTexture", path, pending);
                if (def.CustomMaterialPathHash != 0 && !def.HasResolvedCustomMaterial)
                    Flag("CUSTOM_MATERIAL_UNRESOLVED", def, $"0x{def.CustomMaterialPathHash:x8}");

                string fallback = session.GameParticleFallback(def, def.IsMeshPrimitive);
                if (fallback != null)
                    Flag("STOCK_PROGRAM", def, $"{(def.HasResolvedCustomMaterial ? "custom" : def.PrimitiveKind.ToString())}: {fallback}");
            }

            // Simulation: sample the first seconds and watch every emitter and every particle. Child systems start
            // when their parent spawns them, so a child emitter is judged from the first sample its runtime is alive.
            double span = VfxDurationCalculator.SystemSpan(system);
            float until = (float)Math.Min(maxSeconds, double.IsFinite(span) && span > 0 ? span : maxSeconds);
            var spawned = new HashSet<int>();
            var childSpawned = new HashSet<VfxEmitterDefinition>(ReferenceEqualityComparer.Instance);
            var childAlive = new Dictionary<VfxEmitterDefinition, (float First, float Last)>(ReferenceEqualityComparer.Instance);
            var badPositions = new HashSet<VfxEmitterDefinition>(ReferenceEqualityComparer.Instance);
            for (float time = SampleStep; time <= until + 1e-4f; time += SampleStep)
            {
                session.Seek(time);
                foreach (VfxPlaybackGraphRuntime graph in session.Graphs)
                {
                    foreach (VfxPlaybackRuntime runtime in graph.Runtimes)
                    {
                        foreach (VfxPlaybackRuntime.EmitterState state in runtime.Emitters)
                        {
                            if (ReferenceEquals(runtime, graph.Root))
                            {
                                if (state.Particles.Count > 0) spawned.Add(state.SourceOrder);
                            }
                            else if (state.Particles.Count > 0)
                                childSpawned.Add(state.Def);
                            else
                                childAlive[state.Def] = childAlive.TryGetValue(state.Def, out var alive) ? (alive.First, time) : (time, time);
                            if (state.Particles.Count > FloodCount && badPositions.Add(state.Def))
                                Flag("PARTICLE_FLOOD", state.Def, $"t={time:0.00} live={state.Particles.Count}");
                            foreach (VfxPlaybackRuntime.Particle particle in state.Particles)
                            {
                                bool finite = float.IsFinite(particle.Pos.X) && float.IsFinite(particle.Pos.Y) && float.IsFinite(particle.Pos.Z);
                                if ((!finite || particle.Pos.Length() > RunawayDistance) && badPositions.Add(state.Def))
                                    Flag(finite ? "RUNAWAY_POSITION" : "NON_FINITE_POSITION", state.Def, $"t={time:0.00} pos={particle.Pos}");
                            }
                        }
                    }
                }
            }
            IReadOnlyList<VfxEmitterDefinition> emitters = system.Emitters ?? Array.Empty<VfxEmitterDefinition>();
            for (int order = 0; order < emitters.Count; order++)
            {
                VfxEmitterDefinition def = emitters[order];
                if (def.Disabled || spawned.Contains(order)) continue;
                float start = def.TimeBeforeFirstEmission;
                if (start >= until) continue;
                // Lifetime and delay share the system's clock: a delay past the lifetime never emits, in game
                // too (vfx-emission-window-census: 0.4% of emitters, copy-pasted leftovers).
                if (def.EmitterLifetime is { } life && start > life) continue;
                Flag("NO_PARTICLES", def, $"start={start:0.00}s window={until:0.00}s prim={def.PrimitiveKind}");
            }
            foreach ((VfxEmitterDefinition def, (float first, float last)) in childAlive)
            {
                if (def.Disabled || childSpawned.Contains(def)) continue;
                float start = def.TimeBeforeFirstEmission;
                if (def.EmitterLifetime is { } life && start > life) continue;
                // Alive for longer than its emission delay plus a sample, and still empty.
                if (last - first <= start + SampleStep) continue;
                Flag("CHILD_NO_PARTICLES", def, $"start={start:0.00}s alive={first:0.00}-{last:0.00}s prim={def.PrimitiveKind}");
            }
        }

        private static string ResourceFlag(string path, string searchDirectory, IReadOnlyDictionary<ulong, string> wadOf)
        {
            string normalized = path.Replace('\\', '/').ToLowerInvariant();
            string[] candidates = normalized.EndsWith(".dds", StringComparison.Ordinal)
                ? new[] { normalized, normalized[..^4] + ".tex" }
                : normalized.EndsWith(".tex", StringComparison.Ordinal) ? new[] { normalized, normalized[..^4] + ".dds" } : new[] { normalized };
            bool inGame = candidates.Any(candidate => wadOf.ContainsKey(XxHash64Ext.Hash(candidate))) ||
                          (ulong.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash) && wadOf.ContainsKey(hash));
            if (!inGame) return "RESOURCE_ABSENT_IN_GAME";
            bool extracted = candidates.Any(candidate => File.Exists(Path.Combine(searchDirectory, candidate.Replace('/', Path.DirectorySeparatorChar))));
            return extracted ? "RESOURCE_NOT_DECODED" : "RESOURCE_NOT_EXTRACTED";
        }

        private static BinTree LoadBin(IReadOnlyDictionary<ulong, string> wadOf, string binPath)
        {
            ulong hash = XxHash64Ext.Hash(binPath);
            if (!wadOf.TryGetValue(hash, out string wadPath)) return null;
            try
            {
                using var wad = new WadFile(wadPath);
                using var data = wad.LoadChunkDecompressed(hash);
                using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                return new BinTree(stream);
            }
            catch
            {
                return null;
            }
        }

        private static string Option(string[] args, string name)
        {
            int at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }

        private static string Clip(string text, int length) =>
            text == null || text.Length <= length ? text : text[..length] + "…";
    }
}
