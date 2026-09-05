using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Globalization;
using Serilog;
using AssetsManager.Utils;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Hashes;
using AssetsManager.Tests.Diagnostics.Viewer;
using AssetsManager.Tests.Diagnostics.Hashes;
using AssetsManager.Tests.Diagnostics.Monitor;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics
{
    class Program
    {
        private static readonly string PbeDirectory = @"C:\Riot Games\League of Legends (PBE)";

        static async Task Main(string[] args)
        {
            if (args.Length > 0 && string.Equals(args[0], "manifest-verify", StringComparison.OrdinalIgnoreCase))
            {
                await ManifestVerificationDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "game-baseline", StringComparison.OrdinalIgnoreCase))
            {
                await GameBaselineDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "game-grep-anm-profile", StringComparison.OrdinalIgnoreCase))
            {
                GameGrepAnmProfileDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "game-grep-full-profile", StringComparison.OrdinalIgnoreCase))
            {
                GameGrepFullProfileDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "anm-audit", StringComparison.OrdinalIgnoreCase))
            {
                string path = @"C:\Users\danielpriego\AppData\Local\AssetsManager\hashes\hashes.game.txt";
                if (!File.Exists(path)) path = @"C:\Users\danielpriego\Downloads\hashes.game.txt";
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var regex = new System.Text.RegularExpressions.Regex(@"/animations/(?:[^/_]+_)*(?:skin\d+_)?([^/]+)\.anm$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var examples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string line in File.ReadLines(path))
                {
                    var match = regex.Match(line);
                    if (match.Success)
                    {
                        string stem = match.Groups[1].Value.ToLowerInvariant();
                        counts[stem] = counts.GetValueOrDefault(stem) + 1;
                        if (!examples.ContainsKey(stem)) examples[stem] = line;
                    }
                }
                Console.WriteLine("=== Top Animation Stems in hashes.game.txt ===");
                foreach (var pair in counts.OrderByDescending(p => p.Value).Take(40))
                {
                    Console.WriteLine($"Stem: '{pair.Key}' (Count: {pair.Value}) -> Example: {examples[pair.Key]}");
                }
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "fiora-grep-probe", StringComparison.OrdinalIgnoreCase))
            {
                FioraGrepProbeDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "game-unknowns-audit", StringComparison.OrdinalIgnoreCase))
            {
                GameUnknownsAuditDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "game-texture-probe", StringComparison.OrdinalIgnoreCase))
            {
                GameTextureProbeDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "game-link-inspect", StringComparison.OrdinalIgnoreCase))
            {
                GameChunkLinkInspectorDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "bin-dump", StringComparison.OrdinalIgnoreCase))
            {
                await BinDumpDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "quick-hash-check", StringComparison.OrdinalIgnoreCase))
            {
                QuickHashCheck.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "full-coverage", StringComparison.OrdinalIgnoreCase))
            {
                FullUnknownsCoverageDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "game-crack-lab", StringComparison.OrdinalIgnoreCase))
            {
                GameCrackLabDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "game-memory-lab", StringComparison.OrdinalIgnoreCase))
            {
                GameInMemoryResolutionDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "game-champion-texture-lab", StringComparison.OrdinalIgnoreCase))
            {
                GameChampionTextureLabDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "lcu-unknowns-audit", StringComparison.OrdinalIgnoreCase))
            {
                string pbe = args.Length > 1 ? args[1] : PbeDirectory;
                LcuUnknownsAuditDiagnostic.Run(pbe);
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "lcu-export-unknowns", StringComparison.OrdinalIgnoreCase))
            {
                LcuExportUnknownsDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "lcu-crack-lab", StringComparison.OrdinalIgnoreCase))
            {
                LcuCrackLabDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && args[0] == "check-plugins")
            {
                CheckPluginsWads();
                return;
            }
            if (args.Length > 0 && args[0] == "vfx-audit")
            {
                AuditVfxBins(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && args[0] == "vfx-raw-audit")
            {
                AuditRawVfxBins(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && args[0] == "vfx-event-audit")
            {
                AuditVfxEvents(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && args[0] == "vfx-texture-export")
            {
                ExportVfxTexture(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && args[0] == "list-extensions")
            {
                await ListAllExtensionsAsync();
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "inspect-skn", StringComparison.OrdinalIgnoreCase))
            {
                string targetPath = args.Length > 1 ? args[1] : null;
                InspectSknDiagnostic.Run(targetPath);
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "champion-bin-audit", StringComparison.OrdinalIgnoreCase))
            {
                ChampionSkinBinDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "champion-bin-structure-audit", StringComparison.OrdinalIgnoreCase))
            {
                string rootPath = args.Length > 1 ? args[1] : PbeDirectory;
                string hashesPath = args.Length > 2
                    ? args[2]
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AssetsManager",
                        "hashes");
                string filter = args.Length > 3 &&
                                 !string.IsNullOrWhiteSpace(args[3]) &&
                                 !string.Equals(args[3], "-", StringComparison.Ordinal) &&
                                 !string.Equals(args[3], "*", StringComparison.Ordinal)
                    ? args[3]
                    : null;
                int maxBins = args.Length > 4 && int.TryParse(args[4], out int parsedMax) ? parsedMax : 12;
                ChampionSkinBinStructureDiagnostic.Run(rootPath, hashesPath, filter, Math.Max(1, maxBins));
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "submesh-texture-audit", StringComparison.OrdinalIgnoreCase))
            {
                SubmeshTextureAuditDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "champion-texture-audit", StringComparison.OrdinalIgnoreCase))
            {
                ChampionTextureDiagnostic.Run(args.Skip(1).ToArray());
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "mapgeo-memory", StringComparison.OrdinalIgnoreCase))
            {
                string mapGeoPath = args.Length > 1 ? args[1] : null;
                string materialsPath = args.Length > 2 ? args[2] : null;
                string gameDataPath = args.Length > 3 ? args[3] : null;
                MapGeometryMemoryDiagnostic.Run(mapGeoPath, materialsPath, gameDataPath);
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "mapgeo-probe", StringComparison.OrdinalIgnoreCase))
            {
                string mapGeoPath = args.Length > 1 ? args[1] : null;
                string materialsPath = args.Length > 2 ? args[2] : null;
                string gameDataPath = args.Length > 3 ? args[3] : null;
                MapGeometryProbeDiagnostic.Run(mapGeoPath, materialsPath, gameDataPath);
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "lcu-bin-probe", StringComparison.OrdinalIgnoreCase))
            {
                string rootPath = args.Length > 1 ? args[1] : PbeDirectory;
                LcuBinProbeDiagnostic.Run(rootPath);
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "lcu-wad-content", StringComparison.OrdinalIgnoreCase))
            {
                string rootPath = args.Length > 1 ? args[1] : PbeDirectory;
                string filter = args.Length > 2 ? args[2] : null;
                LcuWadContentProbe.Run(rootPath, filter);
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "lcu-string-search", StringComparison.OrdinalIgnoreCase))
            {
                string rootPath = args.Length > 1 ? args[1] : PbeDirectory;
                string needles = args.Length > 2 ? args[2] : null;
                LcuStringSearchProbe.Run(rootPath, needles);
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "shader-shareddata", StringComparison.OrdinalIgnoreCase))
            {
                string rootPath = args.Length > 1 ? args[1] : PbeDirectory;
                string hashesPath = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
                ShaderSharedDataProbe.Run(rootPath, hashesPath);
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "lcu-bin-hash-scan", StringComparison.OrdinalIgnoreCase))
            {
                string rootPath = args.Length > 1 ? args[1] : PbeDirectory;
                LcuBinaryHashScanProbe.Run(rootPath);
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "lcu-bin-all-wads", StringComparison.OrdinalIgnoreCase))
            {
                string rootPath = args.Length > 1 ? args[1] : PbeDirectory;
                string hashesPath = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
                bool allWads = args.Length > 3 && string.Equals(args[3], "--all", StringComparison.OrdinalIgnoreCase);
                LcuBinAllWadsProbe.Run(rootPath, hashesPath, allWads);
                return;
            }
            if (args.Length > 0 && string.Equals(args[0], "lcu-methods", StringComparison.OrdinalIgnoreCase))
            {
                string rootPath = args.Length > 1 ? args[1] : PbeDirectory;
                string hashesPath = args.Length > 2 ? args[2] : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
                string targets = args.Length > 3 ? args[3] : null;
                LcuMethodsProbe.Run(rootPath, hashesPath, targets);
                return;
            }

            if (args.Length == 0 || !string.Equals(args[0], "guessing", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("No benchmark selected. Use 'dotnet test' for the test suite or pass 'guessing' explicitly.");
                return;
            }

            Console.WriteLine("==================================================");
            Console.WriteLine("    ASSETSMANAGER OFFLINE HASH LAB BENCHMARK");
            Console.WriteLine("==================================================");

            if (!Directory.Exists(PbeDirectory))
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: PBE Directory not found at: {PbeDirectory}");
                Console.ResetColor();
                return;
            }

            string tempDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Benchmark_TempData");
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
            Directory.CreateDirectory(tempDir);

            // Copy real hashes databases to temp directory
            string realAppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager");
            string realHashesPath = Path.Combine(realAppDataPath, "hashes");
            string realHashLabPath = Path.Combine(realAppDataPath, "hash_lab");

            string tempHashesPath = Path.Combine(tempDir, "hashes");
            string tempHashLabPath = Path.Combine(tempDir, "hash_lab");

            Directory.CreateDirectory(tempHashesPath);
            Directory.CreateDirectory(tempHashLabPath);

            Console.WriteLine("Copying hashes databases to temporary workspace...");
            CopyDirectory(realHashesPath, tempHashesPath);
            CopyDirectory(realHashLabPath, tempHashLabPath);

            // Initialize services for temp environment
            var directories = new DirectoriesCreator(tempDir);
            var serilogLogger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.Console()
                .CreateLogger();
            var log = new LogService(serilogLogger);
            var store = new BinRstHashGuessingStore(directories);
            var pathStore = new HashGuessingStore(directories);
            var persistence = new HashGuessPersistenceService(pathStore, store);
            var resolver = new HashResolverService(directories, log);
            using var metaHttpClient = new System.Net.Http.HttpClient();
            var metaSchema = new MetaSchemaHashSource(metaHttpClient, directories, log);
            var service = new BinRstHashGuessingService(store, persistence, resolver, directories, log, metaSchema);

            // Create Controlled Hidden Test Corpus (Blind Test)
            Console.WriteLine("Establishing Controlled Hidden Test Corpus (Blind Test)...");
            var hiddenCorpus = await EstablishHiddenCorpusAsync(store, tempHashesPath, tempHashLabPath);
            Console.WriteLine($"Corpus established. Hidden: {hiddenCorpus.Count} known hashes.");

            // Perform Benchmark
            GC.Collect();
            GC.WaitForPendingFinalizers();

            var process = Process.GetCurrentProcess();
            TimeSpan startCpu = process.TotalProcessorTime;
            long startMemory = process.WorkingSet64;
            var stopwatch = Stopwatch.StartNew();

            Console.WriteLine("\n[1/3] Building BIN & RST Inventory (Discovery)...");
            var progress = new ProgressTracker();
            var inventory = await service.BuildInventoryAsync(PbeDirectory, true, true, progress, CancellationToken.None);
            
            Console.WriteLine($" -> Scanned BINs: {inventory.ScannedBins}");
            Console.WriteLine($" -> Scanned RSTs: {inventory.ScannedStringTables}");

            Console.WriteLine("\n[2/3] Running Content-based Offline Guessing...");
            var contentResult = await service.RunContentGuessingAsync(PbeDirectory, true, true, progress, CancellationToken.None);
            Console.WriteLine($" -> Files scanned: {contentResult.ScannedFiles}");
            Console.WriteLine($" -> Matches found: {contentResult.Matches.Count}");

            Console.WriteLine("\n[3/3] Running Structural-based Offline Guessing...");
            var structuralResult = await service.RunStructuralGuessingAsync(PbeDirectory, true, true, progress, CancellationToken.None);
            Console.WriteLine($" -> Candidates checked: {progress.CheckedFiles}");
            Console.WriteLine($" -> Matches found: {structuralResult.Matches.Count}");

            stopwatch.Stop();
            TimeSpan endCpu = process.TotalProcessorTime;
            long endMemory = process.WorkingSet64;

            // Analyze blind test recovery
            var allMatches = contentResult.Matches.Concat(structuralResult.Matches).ToList();
            int recovered = 0;
            foreach (var match in allMatches)
            {
                if (hiddenCorpus.TryGetValue(match.Hash, out string expectedName))
                {
                    if (string.Equals(match.Value, expectedName, StringComparison.OrdinalIgnoreCase))
                    {
                        recovered++;
                    }
                }
            }

            Console.WriteLine("\n================ BENCHMARK RESULTS ================");
            Console.WriteLine($"Total Execution Time : {stopwatch.Elapsed.TotalSeconds:F2} seconds");
            Console.WriteLine($"CPU Time consumed    : {(endCpu - startCpu).TotalSeconds:F2} seconds");
            Console.WriteLine($"Peak RAM (Working Set): {endMemory / (1024 * 1024):N0} MB");
            Console.WriteLine($"Memory Delta         : {(endMemory - startMemory) / (1024 * 1024):N0} MB");
            Console.WriteLine($"Blind Test Recovery  : {recovered} / {hiddenCorpus.Count} ({((double)recovered / hiddenCorpus.Count) * 100:F1}%)");
            
            // FNV Collision check
            var fnvCollisions = allMatches.GroupBy(m => m.Hash).Where(g => g.Select(x => x.Value.ToLowerInvariant()).Distinct().Count() > 1).ToList();
            Console.WriteLine($"FNV Collisions Found : {fnvCollisions.Count}");
            foreach (var col in fnvCollisions)
            {
                Console.WriteLine($" -> Hash {col.Key:x16}: {string.Join(" vs ", col.Select(x => x.Value).Distinct())}");
            }
            Console.WriteLine("==================================================");

            // Test Cancellation Graceful Exit
            Console.WriteLine("\nTesting Cancellation Graceful Exit...");
            using var cts = new CancellationTokenSource();
            var cancelTask = service.RunStructuralGuessingAsync(PbeDirectory, true, true, progress, cts.Token);
            await Task.Delay(50);
            cts.Cancel();
            try
            {
                await cancelTask;
                Console.WriteLine(" -> Verification: OK (Exited cleanly)");
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine(" -> Verification: OK (Canceled as expected)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($" -> Verification: FAILED ({ex.Message})");
            }

            // Cleanup temp directory
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch { }
        }

        private static void CopyDirectory(string source, string dest)
        {
            if (!Directory.Exists(source)) return;
            foreach (string dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dir.Replace(source, dest));
            }
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, file.Replace(source, dest), true);
            }
        }

        private static async Task<Dictionary<ulong, string>> EstablishHiddenCorpusAsync(BinRstHashGuessingStore store, string hashesPath, string hashLabPath)
        {
            var corpus = new Dictionary<ulong, string>();

            // Load some known entries
            var binEntries = await store.LoadKnownAsync(InternalHashKind.BinEntries, CancellationToken.None);
            var binFields = await store.LoadKnownAsync(InternalHashKind.BinFields, CancellationToken.None);
            var binTypes = await store.LoadKnownAsync(InternalHashKind.BinTypes, CancellationToken.None);
            var binHashes = await store.LoadKnownAsync(InternalHashKind.BinHashes, CancellationToken.None);

            // Hide 142 random BIN Local GREP results
            var localGrep = binEntries.Take(142).ToList();
            foreach (var pair in localGrep) corpus[pair.Key] = pair.Value;

            // Hide 48 hashes of "Practice Tool" (e.g. from binFields or binEntries)
            var practiceTool = binFields.Skip(200).Take(48).ToList();
            foreach (var pair in practiceTool) corpus[pair.Key] = pair.Value;

            // Hide 100 representatively hidden known hashes
            var representative = binHashes.Take(100).ToList();
            foreach (var pair in representative) corpus[pair.Key] = pair.Value;

            // Remove hidden corpus from known files in temp workspace
            RemoveHashesFromFile(Path.Combine(hashesPath, "hashes.binentries.txt"), corpus.Keys, 8);
            RemoveHashesFromFile(Path.Combine(hashesPath, "hashes.binfields.txt"), corpus.Keys, 8);
            RemoveHashesFromFile(Path.Combine(hashesPath, "hashes.binhashes.txt"), corpus.Keys, 8);
            RemoveHashesFromFile(Path.Combine(hashesPath, "hashes.bintypes.txt"), corpus.Keys, 8);

            // Insert them into unknowns txt files to simulate historical unknowns
            File.AppendAllLines(
                Path.Combine(hashLabPath, "unknowns.binentries.txt"),
                localGrep.Select(x => x.Key.ToString("x8"))
            );
            File.AppendAllLines(
                Path.Combine(hashLabPath, "unknowns.binfields.txt"),
                practiceTool.Select(x => x.Key.ToString("x8"))
            );
            File.AppendAllLines(
                Path.Combine(hashLabPath, "unknowns.binhashes.txt"),
                representative.Select(x => x.Key.ToString("x8"))
            );

            return corpus;
        }

        private static void RemoveHashesFromFile(string path, IEnumerable<ulong> hashesToRemove, int width)
        {
            if (!File.Exists(path)) return;
            var set = hashesToRemove.Select(h => h.ToString(width == 16 ? "x16" : "x8")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remainingLines = File.ReadLines(path)
                .Where(line =>
                {
                    if (line.Length <= width) return true;
                    string prefix = line[..width];
                    return !set.Contains(prefix);
                })
                .ToList();
            File.WriteAllLines(path, remainingLines);
        }

        private class ProgressTracker : IProgress<InternalHashProgress>
        {
            public int CheckedFiles { get; set; }
            public void Report(InternalHashProgress value)
            {
                CheckedFiles = value.ProcessedFiles;
                Console.Write($"\r -> [Progress] stage={value.CurrentStage} processed={value.ProcessedFiles} matches={value.FoundMatches}          ");
            }
        }

        private static void CheckPluginsWads()
        {
            Console.WriteLine("Scanning Plugins folder for WADs containing BIN or RST signatures...");
            string pluginsDir = Path.Combine(PbeDirectory, "LeagueClient", "Plugins");
            if (!Directory.Exists(pluginsDir))
            {
                var dirs = Directory.GetDirectories(PbeDirectory, "*Plugins*", SearchOption.AllDirectories);
                if (dirs.Length > 0) pluginsDir = dirs[0];
            }

            if (!Directory.Exists(pluginsDir))
            {
                Console.WriteLine($"Plugins directory not found at: {pluginsDir}");
                return;
            }

            Console.WriteLine($"Searching in: {pluginsDir}");
            var wads = Directory.EnumerateFiles(pluginsDir, "*.wad*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Console.WriteLine($"Found {wads.Count} WAD files in Plugins.");
            int totalBins = 0;
            int totalRsts = 0;

            foreach (var wadPath in wads)
            {
                try
                {
                    using var wad = new LeagueToolkit.Core.Wad.WadFile(wadPath);
                    int bins = 0;
                    int rsts = 0;
                    foreach (var pair in wad.Chunks)
                    {
                        string sig = GetChunkSignature(wad, pair.Value);
                        if (sig == "PROP" || sig == "PTCH") bins++;
                        else if (sig.StartsWith("RST")) rsts++;
                    }
                    if (bins > 0 || rsts > 0)
                    {
                        Console.WriteLine($" -> {Path.GetFileName(wadPath)}: {bins} BINs, {rsts} RSTs");
                        totalBins += bins;
                        totalRsts += rsts;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error reading {Path.GetFileName(wadPath)}: {ex.Message}");
                }
            }

            Console.WriteLine($"Finished scanning. Total BINs in Plugins: {totalBins}, Total RSTs: {totalRsts}");
        }

        private static string GetChunkSignature(LeagueToolkit.Core.Wad.WadFile wad, LeagueToolkit.Core.Wad.WadChunk chunk)
        {
            try
            {
                using Stream stream = wad.OpenChunk(chunk);
                byte[] buffer = new byte[4];
                int read = stream.Read(buffer, 0, 4);
                if (read < 3) return string.Empty;
                if (read == 3) return System.Text.Encoding.ASCII.GetString(buffer, 0, 3);
                return System.Text.Encoding.ASCII.GetString(buffer);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static async Task ListAllExtensionsAsync()
        {
            Console.WriteLine("Scanning PBE directory for all unique file extensions...");

            // 1. Loose files on disk
            var looseExtensions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var files = Directory.EnumerateFiles(PbeDirectory, "*.*", SearchOption.AllDirectories);
            int looseCount = 0;
            foreach (string file in files)
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = "[no_extension]";
                looseExtensions[ext] = looseExtensions.GetValueOrDefault(ext) + 1;
                looseCount++;
            }

            Console.WriteLine($"\n--- Loose Files on Disk ({looseCount} total) ---");
            foreach (var pair in looseExtensions.OrderByDescending(x => x.Value))
            {
                Console.WriteLine($" {pair.Key}: {pair.Value}");
            }

            // 2. Files inside WAD containers
            Console.WriteLine("\nLoading game paths to resolve WAD file names...");
            var gamePaths = new Dictionary<ulong, string>();
            string hashesFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            string wadHashesFile = Path.Combine(hashesFolder, "hashes.game.txt");
            if (File.Exists(wadHashesFile))
            {
                foreach (string line in File.ReadLines(wadHashesFile))
                {
                    if (line.Length > 17 && line[16] == ' ')
                    {
                        if (ulong.TryParse(line[..16], System.Globalization.NumberStyles.HexNumber, null, out ulong hash))
                        {
                            gamePaths[hash] = line[17..].Trim();
                        }
                    }
                }
            }

            Console.WriteLine($"Loaded {gamePaths.Count} resolved game paths.");

            var wadExtensions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var wads = Directory.EnumerateFiles(PbeDirectory, "*.wad*", SearchOption.AllDirectories)
                .Where(p => p.EndsWith(".wad", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".wad.client", StringComparison.OrdinalIgnoreCase))
                .ToList();

            Console.WriteLine($"Scanning {wads.Count} WAD containers...");
            int resolvedCount = 0;
            int unknownCount = 0;
            var unknownSignatures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string wadPath in wads)
            {
                try
                {
                    using var wad = new LeagueToolkit.Core.Wad.WadFile(wadPath);
                    foreach (var pair in wad.Chunks)
                    {
                        if (gamePaths.TryGetValue(pair.Key, out string path))
                        {
                            string ext = Path.GetExtension(path).ToLowerInvariant();
                            if (string.IsNullOrEmpty(ext)) ext = "[no_extension]";
                            wadExtensions[ext] = wadExtensions.GetValueOrDefault(ext) + 1;
                            resolvedCount++;
                        }
                        else
                        {
                            string sig = GetChunkSignature(wad, pair.Value);
                            if (string.IsNullOrEmpty(sig)) sig = "[unknown_sig]";
                            unknownSignatures[sig] = unknownSignatures.GetValueOrDefault(sig) + 1;
                            unknownCount++;
                        }
                    }
                }
                catch { }
            }

            Console.WriteLine($"\n--- Resolved Files inside WADs ({resolvedCount} total) ---");
            foreach (var pair in wadExtensions.OrderByDescending(x => x.Value))
            {
                Console.WriteLine($" {pair.Key}: {pair.Value}");
            }

            Console.WriteLine($"\n--- Unknown Chunks inside WADs ({unknownCount} total by Signature) ---");
            foreach (var pair in unknownSignatures.OrderByDescending(x => x.Value))
            {
                Console.WriteLine($" {pair.Key}: {pair.Value}");
            }
        }

        private static void AuditVfxBins(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: vfx-audit <skin.bin> <project-root> [system-filter]");
                return;
            }

            using var logger = new LoggerConfiguration().CreateLogger();
            var logService = new AssetsManager.Services.Core.LogService(logger);
            var service = new AssetsManager.Services.Viewer.Vfx.Loading.VfxLoadingService();

            string binPath = Path.GetFullPath(args[0]);
            string rootPath = Path.GetFullPath(args[1]);

            Console.WriteLine($"Testing VfxLoadingService.Load...");
            Console.WriteLine($"binPath: {binPath}");
            Console.WriteLine($"rootPath: {rootPath}");

            var bundle = service.Load(binPath, logService);
            Console.WriteLine($"\n[RESULT] Total VFX Systems Loaded: {bundle.Systems.Count}");
            Console.WriteLine($"[RESULT] Event Sequences Loaded: {bundle.EventSequences.Count}");
            Console.WriteLine($"[RESULT] BIN Files Loaded: {bundle.LoadedBins.Count}");
            var compositions = AssetsManager.Services.Viewer.Vfx.Composition.VfxAbilityCompositionBuilder.BuildAll(
                bundle.EventSequences.Values,
                bundle.Systems,
                bundle.ResourceMap);
            Console.WriteLine($"[RESULT] Ability Compositions: {compositions.Count} " +
                $"({compositions.Sum(item => item.ResolvedCount)} resolved events, " +
                $"{compositions.Sum(item => item.UnresolvedCount)} unresolved events)");
            Console.WriteLine($"[RESULT] Owner Scene: " +
                (bundle.OwnerSceneContext is null
                    ? "not resolved"
                    : $"mesh={bundle.OwnerSceneContext.MeshPath}, skeleton={bundle.OwnerSceneContext.SkeletonPath}, scale={bundle.OwnerSceneContext.SkinScale}"));
            foreach (string loadedBin in bundle.LoadedBins)
                Console.WriteLine($"   BIN: {loadedBin}");
            var meshResolver = new AssetsManager.Services.Viewer.Vfx.Resources.VfxResourceResolver();

            string systemFilter = args.Length > 2 ? args[2] : null;
            if (!string.IsNullOrWhiteSpace(systemFilter))
            {
                foreach (var composition in compositions.Where(item => item.Events.Any(compositionEvent =>
                    compositionEvent.System?.Name?.Contains(systemFilter, StringComparison.OrdinalIgnoreCase) == true)))
                {
                    Console.WriteLine($" - Composition: 0x{composition.SequencePathHash:X8} | " +
                        $"frames={composition.StartFrame}..{composition.EndFrame} | tick={composition.TickDuration} | " +
                        $"resolved={composition.ResolvedCount}/{composition.Events.Count}");
                    foreach (var compositionEvent in composition.Events)
                    {
                        Console.WriteLine($"   * frame={compositionEvent.Event.StartFrame}..{compositionEvent.Event.EndFrame} | " +
                            $"system={compositionEvent.System?.Name ?? "UNRESOLVED"} | " +
                            $"hash=0x{compositionEvent.ResolvedSystemHash:X8} | key=0x{compositionEvent.Event.EffectKey:X8} | " +
                            $"loop={compositionEvent.Event.IsLoop} | kill={compositionEvent.Event.IsKillEvent}");
                    }
                }
            }
            var selectedSystems = string.IsNullOrWhiteSpace(systemFilter)
                ? bundle.Systems.Values.Take(20)
                : bundle.Systems.Values.Where(system =>
                    system.Name.Contains(systemFilter, StringComparison.OrdinalIgnoreCase));

            foreach (var sys in selectedSystems)
            {
                Console.WriteLine(
                    $" - System: '{sys.Name}' | Emitters: {sys.Emitters.Count} | " +
                    $"VisibilityRadius: {sys.VisibilityRadius} | Transform: {sys.Transform}");
                if (string.IsNullOrWhiteSpace(systemFilter)) continue;
                foreach (var emitter in sys.Emitters)
                {
                    string children = emitter.ChildParticleSet is null
                        ? "-"
                        : string.Join(", ", emitter.ChildParticleSet.Children.Select(child =>
                            $"{child.Name}[system={child.SystemHash:X8}, key={child.EffectKey:X8}]"));
                    string spawn = emitter.SpawnShape is null
                        ? "-"
                        : $"{emitter.SpawnShape.Kind}[size={emitter.SpawnShape.Size}, " +
                          $"radius={emitter.SpawnShape.Radius}, height={emitter.SpawnShape.Height}, " +
                          $"offset={emitter.SpawnShape.EmitOffset.Constant}, flags={emitter.SpawnShape.Flags}]";
                    string meshBounds = "-";
                    string textureInfo = DescribeVfxTexture(meshResolver.ResolveTexture(emitter.TexturePath, rootPath));
                    var mesh = emitter.PrimitiveKind == AssetsManager.Views.Models.Viewer.VfxPrimitiveKind.AttachedMesh &&
                               bundle.OwnerSceneContext is not null
                        ? meshResolver.ResolveAttachedMesh(
                            bundle.OwnerSceneContext.MeshPath,
                            emitter.AttachedSubmeshHashes,
                            rootPath,
                            bundle.OwnerSceneContext.SkinScale)
                        : meshResolver.ResolveMesh(emitter.MeshPath, rootPath);
                    if (mesh is { } decoded && decoded.Positions.Length >= 3)
                    {
                        var min = new System.Numerics.Vector3(float.PositiveInfinity);
                        var max = new System.Numerics.Vector3(float.NegativeInfinity);
                        foreach (uint vertexIndex in decoded.Indices.Distinct())
                        {
                            int index = checked((int)vertexIndex * 3);
                            if (index + 2 >= decoded.Positions.Length) continue;
                            var position = new System.Numerics.Vector3(
                                decoded.Positions[index],
                                decoded.Positions[index + 1],
                                decoded.Positions[index + 2]);
                            min = System.Numerics.Vector3.Min(min, position);
                            max = System.Numerics.Vector3.Max(max, position);
                        }
                        meshBounds = $"indices={decoded.Indices.Length}, {min}..{max} size={max - min}";
                    }
                    Console.WriteLine(
                        $"   * {emitter.Name} | primitive={emitter.PrimitiveKind} | " +
                        $"importance={emitter.Importance} | " +
                        $"rate={emitter.Rate.Constant} | rateIsPeriod={emitter.RateIsPeriod} | " +
                        $"birthPeriod={emitter.BirthTimePeriod} | emitterLife={emitter.EmitterLifetime} | " +
                        $"particleLife={emitter.ParticleLifetime.Constant} | single={emitter.IsSingleParticle} | " +
                        $"birthScale={emitter.BirthScale.Constant} | scale0={emitter.ScaleOverLife?.Constant} | " +
                        $"uniform={emitter.IsUniformScale} | position={emitter.EmitterPosition.Constant} | " +
                        $"blend={emitter.BlendMode}/{AssetsManager.Views.Models.Viewer.VfxBlendModes.Describe(emitter.BlendMode)} | " +
                        $"texture={emitter.TexturePath ?? "-"} [{textureInfo}] | textureMult={emitter.TextureMultPath ?? "-"} | " +
                        $"birthColor={emitter.BirthColor.Constant} | color={emitter.ColorOverLife?.Constant} | " +
                        $"birthRotation={emitter.BirthRotation?.Constant} | renderState={emitter.RenderState} | " +
                        $"reflection={emitter.Reflection} | erosion={emitter.AlphaErosion} | " +
                        $"spawn={spawn} | velocity={emitter.BirthVelocity?.Constant} | " +
                        $"acceleration={emitter.Acceleration?.Constant} | mesh={emitter.MeshPath ?? "-"} | " +
                        $"meshBounds={meshBounds} | " +
                        $"attachedSubmeshes={string.Join(",", emitter.AttachedSubmeshHashes ?? Array.Empty<uint>())} | " +
                        $"children={children}");
                }
            }
        }

        private static string DescribeVfxTexture(System.Windows.Media.Imaging.BitmapSource source)
        {
            if (source is null) return "missing";
            var bitmap = source.Format == System.Windows.Media.PixelFormats.Bgra32
                ? source
                : new System.Windows.Media.Imaging.FormatConvertedBitmap(
                    source,
                    System.Windows.Media.PixelFormats.Bgra32,
                    null,
                    0);
            int stride = bitmap.PixelWidth * 4;
            byte[] pixels = new byte[stride * bitmap.PixelHeight];
            bitmap.CopyPixels(pixels, stride, 0);
            int minimumAlpha = 255;
            int maximumAlpha = 0;
            long alphaTotal = 0;
            for (int index = 3; index < pixels.Length; index += 4)
            {
                int alpha = pixels[index];
                minimumAlpha = Math.Min(minimumAlpha, alpha);
                maximumAlpha = Math.Max(maximumAlpha, alpha);
                alphaTotal += alpha;
            }
            double averageAlpha = pixels.Length == 0 ? 0 : alphaTotal / (pixels.Length / 4d);
            return $"{bitmap.PixelWidth}x{bitmap.PixelHeight}, alpha={minimumAlpha}..{maximumAlpha}, avg={averageAlpha:F1}";
        }

        private static void ExportVfxTexture(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: vfx-texture-export <texture.tex> <output.png>");
                return;
            }
            var source = AssetsManager.Utils.TextureUtils.LoadTextureFromFile(Path.GetFullPath(args[0]));
            if (source is null) throw new InvalidDataException("Texture could not be decoded.");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
            using var output = File.Create(Path.GetFullPath(args[1]));
            encoder.Save(output);
            Console.WriteLine($"Exported {source.PixelWidth}x{source.PixelHeight}: {Path.GetFullPath(args[1])}");
        }

        private static readonly Dictionary<uint, string> RawVfxNames = BuildRawVfxNames();

        private static Dictionary<uint, string> BuildRawVfxNames()
        {
            string[] names =
            {
                "particleName", "particlePath", "VfxSystemDefinitionData", "VfxEmitterDefinitionData",
                "doesParticleLifetimeScale", "colorRenderFlags", "StencilReferenceId", "timeActiveDuringPeriod",
                "meshRenderFlags", "stencilMode", "birthRotationalVelocity0", "numFrames", "emissionMeshName",
                "timeBeforeFirstEmission", "particleLinger", "flexBirthUVOffset", "colorLookUpTypeX", "uvMode",
                "colorLookUpTypeY", "isGroundLayer", "particleLifetime", "isLocalOrientation", "flexScaleBirthScale",
                "TextureFlipV", "TextureFlipU", "textureMult", "acceleration", "texAddressModeBase", "velocity",
                "birthUvRotateRate", "materialOverrideDefinitions", "disabled", "isUniformScale",
                "birthRotationalAcceleration", "particleIsLocalOrientation", "particleLingerType", "emitterLinger",
                "SpawnShape", "texture", "disableBackfaceCull", "emitterName", "color", "reflectionDefinition",
                "isSingleParticle", "colorblindVisibility", "CustomMaterial", "offsetLifeScalingSymmetryMode",
                "FlexShapeDefinition", "rateByVelocityFunction", "birthOrbitalVelocity", "WriteAlphaOnly",
                "emitterUvScrollRate", "lifetime", "HasVariableStartTime", "emissionSurfaceDefinition",
                "EmitterPosition", "birthRotation0", "particleUVScrollRate", "miscRenderFlags", "modulationFactor",
                "ParticlesShareRandomValue", "depthBiasFactors", "offsetLifetimeScaling", "flexParticleLifetime",
                "rotation0", "uvTransformCenter", "startFrame", "renderPhaseOverride", "flexRate",
                "flexBirthRotationalVelocity0", "doesLifetimeScale", "directionVelocityScale", "primitive",
                "stencilRef", "pass", "useEmissionMeshNormalForBirth", "FlexInstanceScale", "birthDrag",
                "birthColor", "texDiv", "paletteDefinition", "censorModulateValue", "isRotationEnabled", "uvRotation",
                "period", "frameRate", "flexBirthVelocity", "childParticleSetDefinition", "birthAcceleration",
                "falloffTexture", "sliceTechniqueRange", "uvScrollClamp", "IsEmitterSpace", "fieldCollectionDefinition",
                "rate", "translationOverride", "doesCastShadow", "particleColorTexture", "rotationOverride", "drag",
                "birthUVOffset", "Linger", "importance", "alphaRef", "isFollowingTerrain", "LegacySimple",
                "distortionDefinition", "SortEmittersByPos", "emissionMeshScale", "flexBirthUVScrollRate",
                "softParticleParams", "particleUVRotateRate", "alphaErosionDefinition", "birthFrameRate",
                "isTexturePixelated", "MaximumRateByVelocity", "bindWeight", "colorLookUpOffsets", "ChanceToNotExist",
                "birthUvScrollRate", "scale0", "useNavmeshMask", "isDirectionOriented", "Audio", "isRandomStartFrame",
                "scaleOverride", "worldAcceleration", "directionVelocityMinScale", "hasPostRotateOrientation",
                "uvScale", "colorLookUpScales", "birthScale0", "Filtering", "uvParallaxScale", "birthVelocity",
                "postRotateOrientationAxis", "blendMode",
                "isRandomStartFrameMult", "texDivMult", "ParticleIntegratedUvScrollMult", "birthUvScrollRateMult",
                "UvRotationMult", "TextureMultFilpV", "uvTransformCenterMult", "TextureMultFilpU",
                "texAddressModeMult", "birthUVOffsetMult", "uvScrollClampMult", "flexBirthUVScrollRateMult",
                "uvScaleMult", "ParticleIntegratedUvRotateMult", "emitterUvScrollRateMult", "uvScrollAlphaMult",
                "birthUvRotateRateMult", "constantValue", "dynamics", "times", "values", "keyTimes", "keyValues",
                "mMeshName", "mSimpleMeshName", "mAnimationName", "mTrail", "mBirthTilingSize", "mSmoothingMode",
                "mMode", "mMaxAddedPerFrame", "mCutoff", "erosionMapName", "erosionDriveCurve", "erosionFeatherIn",
                "erosionFeatherOut", "erosionMapAddressMode", "erosionMapChannelMixer", "normalMapTexture",
                "distortion", "distortionMode", "beginIn", "deltaIn", "beginOut", "deltaOut"
            };
            return names
                .Select(name => (Hash: Fnv1a.HashLower(name), Name: name))
                .GroupBy(item => item.Hash)
                .ToDictionary(group => group.Key, group => group.First().Name);
        }

        private static void AuditRawVfxBins(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("Usage: vfx-raw-audit <skin.bin> <system-filter> [emitter-filter]");
                return;
            }

            string binPath = Path.GetFullPath(args[0]);
            string systemFilter = args[1];
            string emitterFilter = args.Length > 2 ? args[2] : null;
            using var stream = File.OpenRead(binPath);
            var tree = new BinTree(stream);
            uint systemClass = Fnv1a.HashLower("VfxSystemDefinitionData");
            uint emitterClass = Fnv1a.HashLower("VfxEmitterDefinitionData");
            uint particleName = Fnv1a.HashLower("particleName");
            uint emitterName = Fnv1a.HashLower("emitterName");

            Console.WriteLine($"[RAW VFX] {binPath}");
            Console.WriteLine($"Objects={tree.Objects.Count} Dependencies={tree.Dependencies.Count}");
            foreach (var system in tree.Objects.Values.Where(item => item.ClassHash == systemClass))
            {
                string name = RawString(system.Properties, particleName) ?? $"0x{system.PathHash:x8}";
                if (!name.Contains(systemFilter, StringComparison.OrdinalIgnoreCase)) continue;

                Console.WriteLine($"\nSYSTEM {name} path=0x{system.PathHash:x8}");
                var emitters = system.Properties.Values
                    .OfType<BinTreeContainer>()
                    .SelectMany(container => container.Elements)
                    .OfType<BinTreeStruct>()
                    .Where(item => item.ClassHash == emitterClass)
                    .ToArray();
                foreach (var emitter in emitters)
                {
                    string nameValue = RawString(emitter.Properties, emitterName) ?? "(emitter)";
                    if (!string.IsNullOrWhiteSpace(emitterFilter) &&
                        !nameValue.Contains(emitterFilter, StringComparison.OrdinalIgnoreCase)) continue;

                    Console.WriteLine($"  EMITTER {nameValue} properties={emitter.Properties.Count}");
                    foreach (var property in emitter.Properties.OrderBy(item => item.Key))
                    {
                        string propertyName = RawVfxNames.TryGetValue(property.Key, out string knownName)
                            ? knownName
                            : $"0x{property.Key:x8}";
                        if (!IsRawVisualField(propertyName) && knownName is not null) continue;
                        Console.WriteLine($"    {propertyName} [{property.Value.Type}] = {DescribeRaw(property.Value)}");
                    }
                }
            }
        }

        private static bool IsRawVisualField(string name) => name switch
        {
            "emitterName" or "primitive" or "texture" or "textureMult" or "texAddressModeBase" or "texDiv" or
            "uvMode" or "uvScale" or "uvParallaxScale" or "uvRotation" or "uvTransformCenter" or "birthUVOffset" or "birthUvScrollRate" or
            "particleUVScrollRate" or "uvScrollClamp" or "emitterUvScrollRate" or "TextureFlipU" or "TextureFlipV" or
            "texAddressModeMult" or "uvScrollAlphaMult" or "TextureMultFilpU" or "TextureMultFilpV" or
            "isLocalOrientation" or "particleIsLocalOrientation" or "isGroundLayer" or "isFollowingTerrain" or
            "IsEmitterSpace" or "isUniformScale" or "birthScale0" or "scale0" or "birthRotation0" or "rotation0" or
            "birthRotationalVelocity0" or "birthRotationalAcceleration" or "isRotationEnabled" or "rotationOverride" or
            "colorRenderFlags" or "modulationFactor" or "WriteAlphaOnly" or "birthColor" or "color" or
            "alphaErosionDefinition" or "distortionDefinition" or "softParticleParams" or "CustomMaterial" or
            "materialOverrideDefinitions" or "Linger" or "particleLinger" or "particleLingerType" or "emitterLinger" or
            "lifetime" or "particleLifetime" or "rate" or "isSingleParticle" or "pass" or "renderPhaseOverride" or
            "miscRenderFlags" or "meshRenderFlags" or "disableBackfaceCull" or "EmitterPosition" or "SpawnShape" or
            "birthVelocity" or "velocity" or "birthAcceleration" or "acceleration" or "worldAcceleration" or "birthDrag" or
            "drag" or "birthOrbitalVelocity" or "fieldCollectionDefinition" or "emissionSurfaceDefinition" or
            "childParticleSetDefinition" or "frameRate" or "birthFrameRate" or "numFrames" or "startFrame" or
            "isRandomStartFrame" or "particleColorTexture" or "isDirectionOriented" or
            "stencilMode" or "stencilRef" or "StencilReferenceId" => true,
            _ => false
        };

        private static void AuditVfxEvents(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("Usage: vfx-event-audit <animation.bin>");
                return;
            }

            string binPath = Path.GetFullPath(args[0]);
            using var stream = File.OpenRead(binPath);
            var tree = new BinTree(stream);
            uint eventMapHash = Fnv1a.HashLower("mEventDataMap");
            uint clipMapHash = Fnv1a.HashLower("mClipDataMap");
            Console.WriteLine($"[VFX EVENTS] {binPath}");
            Console.WriteLine($"mClipDataMap=0x{Fnv1a.HashLower("mClipDataMap"):x8} mEventDataMap=0x{eventMapHash:x8}");
            Console.WriteLine($"Objects={tree.Objects.Count} Dependencies={tree.Dependencies.Count}");
            foreach (BinTreeObject owner in tree.Objects.Values)
            {
                Console.WriteLine($"OBJECT path=0x{owner.PathHash:x8} class=0x{owner.ClassHash:x8}");
                foreach (var entry in owner.Properties)
                    Console.WriteLine($"  property=0x{entry.Key:x8} type={entry.Value.Type}");
                if (owner.Properties.TryGetValue(clipMapHash, out BinTreeProperty clipProperty) && clipProperty is BinTreeMap clipMap)
                {
                    foreach (var clip in clipMap.Take(5))
                        Console.WriteLine($"  CLIP key={DescribeRaw(clip.Key)} value={DescribeRaw(clip.Value)}");
                }
                if (!owner.Properties.TryGetValue(eventMapHash, out BinTreeProperty property)) continue;
                Console.WriteLine($"OWNER path=0x{owner.PathHash:x8} class=0x{owner.ClassHash:x8} mapType={property.Type}");
                if (property is not BinTreeMap map) continue;
                foreach (var pair in map.Take(12))
                {
                    Console.WriteLine($"  key={DescribeRaw(pair.Key)} value={DescribeRaw(pair.Value)}");
                }
            }
        }

        private static string RawString(IReadOnlyDictionary<uint, BinTreeProperty> properties, uint hash) =>
            properties.TryGetValue(hash, out BinTreeProperty property) && property is BinTreeString value
                ? value.Value
                : null;

        private static string DescribeRaw(BinTreeProperty property, int depth = 0)
        {
            if (property is BinTreeOptional optional)
                return optional.Value is null ? "<none>" : $"optional({DescribeRaw(optional.Value, depth + 1)})";
            if (property is BinTreeStruct structure)
            {
                if (depth >= 3) return $"struct 0x{structure.ClassHash:x8} ({structure.Properties.Count})";
                string contents = string.Join(
                    ", ",
                    structure.Properties.OrderBy(item => item.Key).Select(item =>
                    {
                        string name = RawVfxNames.TryGetValue(item.Key, out string knownName)
                            ? knownName
                            : $"0x{item.Key:x8}";
                        return $"{name}={DescribeRaw(item.Value, depth + 1)}";
                    }));
                return $"struct 0x{structure.ClassHash:x8} {{{contents}}}";
            }
            if (property is BinTreeContainer container)
            {
                if (depth >= 4) return $"container[{container.Elements.Count}]";
                return $"container[{container.Elements.Count}]" +
                    (container.Elements.Count == 0 ? "" : $" [{string.Join(", ", container.Elements.Take(6).Select(item => DescribeRaw(item, depth + 1)))}]");
            }
            object value = property.GetType().GetProperty("Value")?.GetValue(property);
            return value switch
            {
                null => "<null>",
                float number => number.ToString("G9", CultureInfo.InvariantCulture),
                double number => number.ToString("G9", CultureInfo.InvariantCulture),
                System.Numerics.Vector2 vector => $"<{vector.X:G9},{vector.Y:G9}>",
                System.Numerics.Vector3 vector => $"<{vector.X:G9},{vector.Y:G9},{vector.Z:G9}>",
                System.Numerics.Vector4 vector => $"<{vector.X:G9},{vector.Y:G9},{vector.Z:G9},{vector.W:G9}>",
                string text => $"\"{text}\"",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? value.ToString()
            };
        }
    }
}
