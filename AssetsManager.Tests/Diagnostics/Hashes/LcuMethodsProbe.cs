using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    internal static class LcuMethodsProbe
    {
        private static readonly HashSet<string> SkippedExtensions = new(StringComparer.Ordinal)
        {
            "png", "jpg", "ttf", "webm", "ogg", "dds", "tga"
        };

        private static readonly Assembly Assembly = typeof(HashGuessEngine).Assembly;

        public static void Run(string pbeRoot, string hashesPath, string targetsArg)
        {
            string hashesLcu = Path.Combine(hashesPath, "hashes.lcu.txt");
            string hashesGame = Path.Combine(hashesPath, "hashes.game.txt");
            if (!File.Exists(hashesLcu)) { Console.WriteLine($"Missing {hashesLcu}"); return; }

            ulong[] targets = targetsArg.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => ulong.Parse(value.Split('.')[0].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToArray();
            Console.WriteLine($"Targets: {string.Join(", ", targets.Select(value => $"{value:x16}"))}");

            IReadOnlyList<string> lcuPaths = LoadCatalogPaths(hashesLcu);
            IReadOnlyList<string> gamePaths = File.Exists(hashesGame) ? LoadCatalogPaths(hashesGame) : Array.Empty<string>();
            Console.WriteLine($"LCU catalog: {lcuPaths.Count} paths | GAME catalog: {gamePaths.Count} paths");

            object lcuGuesser = CreateLcuGuesser(lcuPaths);
            object gameGuesser = CreateGameGuesser(gamePaths);
            var engine = new HashGuessEngine(HashGuessDomain.Lcu, targets.ToHashSet());

            RunPhase("GREP (Wad Path Grep principal)", () => RunGrepPhase(lcuGuesser, engine, pbeRoot, hashesPath), engine);
            if (engine.RemainingUnknownCount == 0) return;

            RunPhase("GAME hash cross-domain", () =>
                GuessFromGameHashes(lcuGuesser, engine, gameGuesser), engine);
            if (engine.RemainingUnknownCount == 0) return;

            RunPhase("Language variants", () =>
                SubstituteRegionLang(lcuGuesser, engine, int.MaxValue), engine);
            if (engine.RemainingUnknownCount == 0) return;

            RunPhase("Number variants", () =>
                SubstituteNumbers(lcuGuesser, engine, 10_000), engine);
            if (engine.RemainingUnknownCount == 0) return;

            RunPhase("Plugin variants", () =>
                SubstitutePlugin(lcuGuesser, engine, int.MaxValue), engine);
            if (engine.RemainingUnknownCount == 0) return;

            RunPhase("Extension variants", () =>
                SubstituteExtensions(lcuGuesser, engine, int.MaxValue), engine);
            if (engine.RemainingUnknownCount == 0) return;

            RunPhase("Patterns", () =>
                GuessPatterns(lcuGuesser, engine, int.MaxValue), engine);
            if (engine.RemainingUnknownCount == 0) return;

            RunPhase("Basename substitution (budget 10M)", () =>
                SubstituteBasenames(lcuGuesser, engine, 10_000_000), engine);
            if (engine.RemainingUnknownCount == 0) return;

            RunPhase("Basename word substitution (budget 50M)", () =>
                SubstituteBasenameWords(lcuGuesser, engine, 50_000_000), engine);
            if (engine.RemainingUnknownCount == 0) return;

            RunPhase("FOCUSED game-data ALL 1:1 (wordlist completo, budget 2M)", () =>
                RunGameDataFocused(lcuGuesser, engine, wordCap: int.MaxValue, budget: 2_000_000), engine);
            if (engine.RemainingUnknownCount == 0) return;

            RunPhase("FOCUSED game-data ALL 2:2 (top 500, budget 2M)", () =>
                RunGameDataFocused(lcuGuesser, engine, wordCap: int.MaxValue, budget: 2_000_000, doubleMode: true, doubleCap: 500), engine);
            if (engine.RemainingUnknownCount == 0) return;

            RunPhase("FOCUSED game-data ALL 1->2 pares (top 800, budget 2M)", () =>
                RunGameDataPairs(lcuGuesser, engine, wordCap: 800, budget: 2_000_000), engine);
            if (engine.RemainingUnknownCount == 0) return;

            RunPhase("FOCUSED game-data ALL word addition (wordlist completo, budget 1M)", () =>
                RunGameDataAddition(lcuGuesser, engine, wordCap: int.MaxValue, budget: 1_000_000), engine);
            if (engine.RemainingUnknownCount == 0) return;

            RunPhase("GENERAL AddBasenameWord (time-boxed 120s, todas las rutas)", () =>
                RunGeneralWordAddition(lcuGuesser, engine, 10_000_000), engine, timeoutCancelled: "timeout");
            if (engine.RemainingUnknownCount == 0) return;

            RunPhase("V1 path patterns (time-boxed 120s)", () =>
                RunV1(lcuGuesser, engine, TimeSpan.FromSeconds(120)), engine, timeoutCancelled: "timeout");

            Console.WriteLine(engine.RemainingUnknownCount == 0
                ? "RESULTADO: RESUELTO con los metodos actuales."
                : "RESULTADO: NINGUN metodo actual lo resuelve.");
        }

        private static void RunPhase(string name, Func<int> action, HashGuessEngine engine, string timeoutCancelled = null)
        {
            if (engine.RemainingUnknownCount == 0) return;
            var stopwatch = Stopwatch.StartNew();
            int checkedCount = action();
            stopwatch.Stop();
            string outcome = checkedCount < 0 ? $"({timeoutCancelled} sin resolver)" : string.Empty;
            Console.WriteLine($"[{name}] checked={checkedCount:N0} remaining={engine.RemainingUnknownCount} elapsed={stopwatch.Elapsed.TotalSeconds:F1}s {outcome}".TrimEnd());
            foreach (var match in engine.Matches.Values)
                Console.WriteLine($"   >>> RESUELTO: {match.Hash:x16} = {match.Path} ({match.Strategy})");
        }

        private static int RunGrepPhase(object lcuGuesser, HashGuessEngine engine, string pbeRoot, string hashesPath)
        {
            Dictionary<ulong, string> catalog = LoadCatalog(Path.Combine(hashesPath, "hashes.lcu.txt"));
            string[] wads = Directory.EnumerateFiles(Path.Combine(pbeRoot, "Plugins"), "*.wad", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            MethodInfo grepWad = GetMethod(lcuGuesser, "GrepWad");
            int checkedCount = 0;
            int processed = 0;
            foreach (string wadPath in wads)
            {
                try
                {
                    using var wad = new WadFile(wadPath);
                    foreach (var chunk in wad.Chunks.Values)
                    {
                        processed++;
                        if (chunk.Compression == WadChunkCompression.Satellite) continue;
                        string resolvedPath = catalog.TryGetValue(chunk.PathHash, out string path) ? path : string.Empty;
                        string ext = Path.GetExtension(resolvedPath).TrimStart('.').ToLowerInvariant();
                        if (ext.Length == 0) ext = InferExtension(wad, chunk);
                        if (SkippedExtensions.Contains(ext)) continue;
                        try
                        {
                            using var dataOwner = wad.LoadChunkDecompressed(chunk);
                            ArraySegment<byte> data = dataOwner.DangerousGetArray();
                            grepWad.Invoke(lcuGuesser, new object[] { engine, data, resolvedPath, wadPath, chunk.PathHash });
                            checkedCount++;
                        }
                        catch { }
                    }
                }
                catch { }
            }
            Console.WriteLine($"   [GREP] wads={wads.Length} chunks={processed:N0} textChunksGrep={checkedCount:N0}");
            return checkedCount;
        }

        private static string InferExtension(WadFile wad, WadChunk chunk)
        {
            try
            {
                using Stream stream = wad.OpenChunk(chunk);
                Span<byte> buffer = stackalloc byte[4];
                int read = stream.Read(buffer);
                if (read < 3) return string.Empty;
                string sig = Encoding.ASCII.GetString(buffer[..read]);
                return sig switch
                {
                    ".PNG" => "png",
                    "OggS" => "ogg",
                    "..xm" => "webm",
                    "OTTO" => "ttf",
                    ".E.." => "ttf",
                    _ => string.Empty
                };
            }
            catch
            {
                return string.Empty;
            }
        }

        private static IReadOnlyList<string> LoadCatalogPaths(string path)
        {
            var paths = new List<string>(1_000_000);
            foreach (string line in File.ReadLines(path))
            {
                int separator = line.IndexOf(' ');
                if (separator <= 0 || separator == line.Length - 1) continue;
                string value = PathUtils.NormalizePath(line[(separator + 1)..]);
                if (value.Length > 0) paths.Add(value);
            }
            return paths;
        }

        private static Dictionary<ulong, string> LoadCatalog(string path)
        {
            var catalog = new Dictionary<ulong, string>(1_000_000);
            foreach (string line in File.ReadLines(path))
            {
                int separator = line.IndexOf(' ');
                if (separator <= 0 || separator == line.Length - 1) continue;
                if (!ulong.TryParse(line.AsSpan(0, separator), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash)) continue;
                string value = PathUtils.NormalizePath(line[(separator + 1)..]);
                if (value.Length > 0) catalog[hash] = value;
            }
            return catalog;
        }

        private static object CreateHashFile(HashGuessDomain domain, IEnumerable<string> paths)
        {
            Type hashFileType = GetType("AssetsManager.Services.Hashes.Guessers.HashFile");
            ConstructorInfo ctor = hashFileType.GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(HashGuessDomain), typeof(IEnumerable<string>) },
                null);
            return ctor.Invoke(new object[] { domain, paths });
        }

        private static object CreateLcuGuesser(IReadOnlyList<string> paths)
        {
            Type type = GetType("AssetsManager.Services.Hashes.Guessers.LcuHashGuesser");
            object hashFile = CreateHashFile(HashGuessDomain.Lcu, paths);
            Type hashFileType = hashFile.GetType();
            ConstructorInfo ctor = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .First(c =>
                {
                    ParameterInfo[] parameters = c.GetParameters();
                    return parameters.Length == 2 && parameters[0].ParameterType == hashFileType;
                });
            return ctor.Invoke(new[] { hashFile, null });
        }

        private static object CreateGameGuesser(IReadOnlyList<string> paths)
        {
            Type type = GetType("AssetsManager.Services.Hashes.Guessers.GameHashGuesser");
            object hashFile = CreateHashFile(HashGuessDomain.Game, paths);
            Type hashFileType = hashFile.GetType();
            ConstructorInfo ctor = type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .First(c =>
                {
                    ParameterInfo[] parameters = c.GetParameters();
                    return parameters.Length >= 1 && parameters[0].ParameterType == hashFileType;
                });
            object[] args = new object[ctor.GetParameters().Length];
            args[0] = hashFile;
            return ctor.Invoke(args);
        }

        private static int SubstituteRegionLang(object lcuGuesser, HashGuessEngine engine, int budget) =>
            (int)Invoke(lcuGuesser, "SubstituteRegionLang", engine, CancellationToken.None, budget, null);

        private static int SubstituteNumbers(object lcuGuesser, HashGuessEngine engine, int maximum) =>
            (int)Invoke(lcuGuesser, "SubstituteNumbers", engine, CancellationToken.None, maximum, null, null);

        private static int SubstitutePlugin(object lcuGuesser, HashGuessEngine engine, int budget) =>
            (int)Invoke(lcuGuesser, "SubstitutePlugin", engine, CancellationToken.None, budget, null);

        private static int CheckIter(object guesser, HashGuessEngine engine, object candidates, string source)
        {
            int checkedCount = 0;
            foreach (object candidate in (System.Collections.IEnumerable)candidates)
            {
                string path = GetFieldOrProperty<string>(candidate, "Path");
                if (string.IsNullOrEmpty(path)) continue;
                checkedCount++;
                engine.Check(path, GetStrategy(candidate), source);
                if (engine.RemainingUnknownCount == 0) break;
            }
            return checkedCount;
        }

        private static HashGuessStrategy GetStrategy(object candidate)
        {
            object value = GetFieldOrProperty<object>(candidate, "Strategy");
            return value is null ? default : (HashGuessStrategy)value;
        }

        private static T GetFieldOrProperty<T>(object target, string name)
        {
            Type type = target.GetType();
            FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field != null) return (T)field.GetValue(target);
            PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            return property != null ? (T)property.GetValue(target) : default;
        }

        private static int SubstituteBasenames(object lcuGuesser, HashGuessEngine engine, int budget) =>
            (int)Invoke(lcuGuesser, "SubstituteBasenames", engine, CancellationToken.None, null, null, budget, null);

        private static int SubstituteBasenameWords(object lcuGuesser, HashGuessEngine engine, int budget) =>
            (int)Invoke(lcuGuesser, "SubstituteBasenameWords", engine, CancellationToken.None, null, null, null, 1, 1, budget, null);

        private static int SubstituteExtensions(object lcuGuesser, HashGuessEngine engine, int budget) =>
            (int)Invoke(lcuGuesser, "SubstituteExtensions", engine, CancellationToken.None, budget, null, null);

        private static int GuessPatterns(object lcuGuesser, HashGuessEngine engine, int budget) =>
            (int)Invoke(lcuGuesser, "GuessPatterns", engine, CancellationToken.None, budget, null);

        private static int GuessFromGameHashes(object lcuGuesser, HashGuessEngine engine, object gameGuesser) =>
            (int)Invoke(lcuGuesser, "GuessFromGameHashes", engine, gameGuesser, CancellationToken.None, int.MaxValue, null);

        private static IReadOnlyList<string> GetGameDataPaths(object lcuGuesser)
        {
            IReadOnlyList<string> paths = GetKnownPaths(lcuGuesser);
            return paths.Where(path =>
                path.StartsWith("plugins/rcp-be-lol-game-data/", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private static int RunGameDataFocused(object lcuGuesser, HashGuessEngine engine, int wordCap, int budget, bool doubleMode = false, int doubleCap = 150)
        {
            var paths = GetGameDataPaths(lcuGuesser);
            var words = HashGuessEngine.BuildBasenameWordlist(paths);
            int checkedCount = 0;
            if (!doubleMode)
            {
                checkedCount += RunInstance(lcuGuesser, "RunFocusedWordlistSubstitution", engine, paths, words.Take(wordCap), budget);
            }
            else
            {
                checkedCount += RunInstance(lcuGuesser, "RunFocusedWordlistDoubleSubstitution", engine, paths, words.Take(doubleCap), budget);
            }
            return checkedCount;
        }

        private static int RunGameDataPairs(object lcuGuesser, HashGuessEngine engine, int wordCap, int budget)
        {
            var paths = GetGameDataPaths(lcuGuesser);
            var words = HashGuessEngine.BuildBasenameWordlist(paths).Take(wordCap).ToList();
            return RunInstance(lcuGuesser, "SubstituteBasenameWordsCore", engine, paths, words, budget, oldWordCount: 1, newWordCount: 2);
        }

        private static int RunGameDataAddition(object lcuGuesser, HashGuessEngine engine, int wordCap, int budget)
        {
            var paths = GetGameDataPaths(lcuGuesser);
            var words = HashGuessEngine.BuildBasenameWordlist(paths).Take(wordCap).ToList();
            return RunInstance(lcuGuesser, "AddBasenameWordCore", engine, paths, words, budget);
        }

        private static int RunGeneralWordAddition(object lcuGuesser, HashGuessEngine engine, int budget)
        {
            MethodInfo addBasenameWord = lcuGuesser.GetType().GetMethod("AddBasenameWord", BindingFlags.Instance | BindingFlags.NonPublic);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            try
            {
                return (int)addBasenameWord.Invoke(lcuGuesser, new object[] { engine, cts.Token });
            }
            catch (TargetInvocationException exception) when (exception.InnerException is OperationCanceledException)
            {
                return -1;
            }
        }

        private static IReadOnlyList<string> GetKnownPaths(object guesser)
        {
            PropertyInfo property = guesser.GetType().GetProperty("KnownPaths", BindingFlags.Instance | BindingFlags.NonPublic);
            return (IReadOnlyList<string>)property.GetValue(guesser);
        }

        private static int RunStatic(string methodName, HashGuessEngine engine, IReadOnlyList<string> paths, IEnumerable<string> words, int budget, int oldWordCount = 1, int newWordCount = 1)
        {
            MethodInfo method = typeof(HashGuessEngine).Assembly
                .GetType("AssetsManager.Services.Hashes.Guessers.HashGuesser", throwOnError: true)
                .GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
            object[] args;
            if (methodName == "AddBasenameWordCore")
            {
                args = new object[] { engine, paths, words, CancellationToken.None, budget, "Focused game-data word addition", null };
            }
            else
            {
                args = new object[] { engine, paths, words, oldWordCount, newWordCount, CancellationToken.None, budget, "Focused game-data PNG pairs", null };
            }
            return (int)method.Invoke(null, args);
        }

        private static int RunInstance(object guesser, string methodName, HashGuessEngine engine, IReadOnlyList<string> paths, IEnumerable<string> words, int budget, int oldWordCount = 1, int newWordCount = 1)
        {
            MethodInfo method = typeof(HashGuessEngine).Assembly
                .GetType("AssetsManager.Services.Hashes.Guessers.HashGuesser", throwOnError: true)
                .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            object[] args;
            if (methodName == "RunFocusedWordlistSubstitution" || methodName == "RunFocusedWordlistDoubleSubstitution")
            {
                args = new object[] { engine, paths, words, CancellationToken.None, budget };
            }
            else
            {
                args = new object[] { engine, paths, words, oldWordCount, newWordCount, CancellationToken.None, budget, "Focused game-data PNG pairs", null };
            }

            return (int)method.Invoke(guesser, args);
        }

        private static int RunV1(object lcuGuesser, HashGuessEngine engine, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                return (int)Invoke(lcuGuesser, "RunV1PathPatterns", engine, null, cts.Token, null, null);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is OperationCanceledException)
            {
                return -1;
            }
        }

        private static object Invoke(object target, string methodName, params object[] args) =>
            GetMethod(target, methodName).Invoke(target, args);

        private static MethodInfo GetMethod(object target, string name) =>
            target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);

        private static Type GetType(string fullName) => Assembly.GetType(fullName, throwOnError: true);
    }
}
