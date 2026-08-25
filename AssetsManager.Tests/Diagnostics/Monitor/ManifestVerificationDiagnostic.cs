using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Monitor;
using AssetsManager.Utils;
using Serilog;

namespace AssetsManager.Tests.Diagnostics.Monitor;

internal static class ManifestVerificationDiagnostic
{
    public static async Task Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine(
                "Usage: dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj -- " +
                "manifest-verify <manifest-path-or-url> <target-root> [filter|-] [locale1,locale2]");
            Environment.ExitCode = 2;
            return;
        }

        string manifestSource = args[0];
        string targetRoot = Path.GetFullPath(args[1]);
        string diagnosticRoot = Path.Combine(
            Path.GetTempPath(),
            $"AssetsManager_ManifestVerification_{Guid.NewGuid():N}");
        string filter = args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]) && args[2] != "-"
            ? args[2]
            : null;
        List<string> languages = args.Length > 3
            ? args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : null;

        try
        {
            using var httpClient = new HttpClient();
            byte[] manifestBytes = await LoadManifestAsync(httpClient, manifestSource);
            var manifest = new RmanService().Parse(manifestBytes);
            var logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .CreateLogger();
            var logService = new LogService(logger);
            var directories = new DirectoriesCreator(diagnosticRoot);
            using var downloader = new ManifestDownloader(
                httpClient,
                logService,
                directories,
                new HashService());

            Console.WriteLine("[ManifestVerification] Verification-only mode; no files will be downloaded or repaired.");
            int filesToPatch = await downloader.VerifyManifestAsync(manifest, targetRoot, filter, languages);
            Console.WriteLine($"[ManifestVerification] Files requiring repair: {filesToPatch:N0}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[ManifestVerification] Failed: {exception}");
            Environment.ExitCode = 1;
        }
        finally
        {
            if (Directory.Exists(diagnosticRoot))
            {
                try
                {
                    Directory.Delete(diagnosticRoot, recursive: true);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[ManifestVerification] Failed to clean diagnostic workspace: {exception}");
                    Environment.ExitCode = 1;
                }
            }
        }
    }

    private static async Task<byte[]> LoadManifestAsync(HttpClient httpClient, string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out Uri uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return await httpClient.GetByteArrayAsync(uri);
        }

        string path = Path.GetFullPath(source);
        if (!File.Exists(path))
            throw new FileNotFoundException("RMAN manifest was not found.", path);

        return await File.ReadAllBytesAsync(path);
    }
}
