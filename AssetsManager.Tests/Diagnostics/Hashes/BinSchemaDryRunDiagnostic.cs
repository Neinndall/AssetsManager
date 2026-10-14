using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using Serilog;

namespace AssetsManager.Tests.Diagnostics.Hashes
{
    // Runs each Meta Schema sub-method on a throwaway copy of the BIN catalogs and inventory,
    // reporting verified and gated findings without touching the user's data.
    internal static class BinSchemaDryRunDiagnostic
    {
        private static readonly string[] SubMethods =
        {
            "bin-schema-crossdomain", "bin-schema-reverse-suffix", "bin-schema-family-lattice"
        };

        public static async Task Run(string[] args)
        {
            string root = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? @"C:\Riot Games\League of Legends (PBE)";
            var source = new DirectoriesCreator();
            foreach (string id in SubMethods)
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "bin-schema-dryrun", id);
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
                var directories = new DirectoriesCreator(tempDir);
                directories.CreateHashesDirectories();
                Directory.CreateDirectory(directories.HashLabPath);
                foreach (string file in Directory.EnumerateFiles(source.HashesPath, "hashes.bin*"))
                    File.Copy(file, Path.Combine(directories.HashesPath, Path.GetFileName(file)));
                foreach (string file in Directory.EnumerateFiles(source.HashLabPath))
                    File.Copy(file, Path.Combine(directories.HashLabPath, Path.GetFileName(file)));

                var logger = new LoggerConfiguration().MinimumLevel.Debug()
                    .Filter.ByIncludingOnly(e => e.RenderMessage().Contains("expected chance", StringComparison.Ordinal))
                    .WriteTo.Console().CreateLogger();
                var log = new LogService(logger);
                var store = new BinRstHashGuessingStore(directories);
                var persistence = new HashGuessPersistenceService(new HashGuessingStore(directories), store);
                using var resolver = new HashResolverService(directories, log);
                using var http = new System.Net.Http.HttpClient();
                var service = new BinRstHashGuessingService(store, persistence, resolver, directories, log,
                    new MetaSchemaHashSource(http, directories, log));

                var started = DateTime.UtcNow;
                var result = await service.RunStructuralGuessingAsync(root, true, false, null, CancellationToken.None, new HashSet<string> { id });
                Console.WriteLine($"{id}: {result.Matches.Count(m => m.CanPromote)} verified, {result.Matches.Count(m => !m.CanPromote)} candidates, {(DateTime.UtcNow - started).TotalSeconds:F0}s");
                foreach (var match in result.Matches.Where(m => m.CanPromote).Take(8))
                    Console.WriteLine($"      {match.DomainText} {match.HashText} {match.Value}");
            }
        }
    }
}
