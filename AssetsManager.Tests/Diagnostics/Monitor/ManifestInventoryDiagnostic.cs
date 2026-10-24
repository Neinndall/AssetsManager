using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AssetsManager.Services.Monitor;
using AssetsManager.Views.Models.Monitor;

namespace AssetsManager.Tests.Diagnostics.Monitor;

internal static class ManifestInventoryDiagnostic
{
    internal static async Task Run(RmanManifest manifest, string source, string targetRoot, string filter,
        List<string> locales, string outputPrefix, ManifestDownloader downloader)
    {
        string prefix = Path.GetFullPath(outputPrefix);
        Directory.CreateDirectory(Path.GetDirectoryName(prefix)!);
        var allFiles = manifest.Files;
        var selected = ManifestDownloader.SelectFiles(manifest, filter, locales).ToHashSet();
        string[] offeredLocales = { "en_US", "es_ES", "es_MX", "tr_TR" };
        var perLocale = offeredLocales.ToDictionary(locale => locale,
            locale => ManifestDownloader.SelectFiles(manifest, filter, new List<string> { locale }).ToHashSet());
        var tags = manifest.Languages.ToDictionary(language => language.LanguageId, language => language.Name);
        var statuses = new Dictionary<string, int>();
        var pending = new List<object>();
        string rootPrefix = Path.TrimEndingDirectorySeparator(targetRoot) + Path.DirectorySeparatorChar;
        int checkedFiles = 0;
        static string Csv(object value) => "\"" + (value?.ToString() ?? "").Replace("\"", "\"\"") + "\"";
        await using var csv = new StreamWriter(prefix + ".csv", false, new UTF8Encoding(true));
        await csv.WriteLineAsync("File,TagIds,Tags,Selected,en_US,es_ES,es_MX,tr_TR,Status,LocalPath,LocalBytes,ManifestBytes,HashType");
        try
        {
            foreach (var file in allFiles.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase))
            {
                string path = Path.GetFullPath(Path.Combine(targetRoot, file.Name));
                if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Manifest file is outside the target: {file.Name}");
                var local = new FileInfo(path);
                bool included = selected.Contains(file);
                string status = "Excluded";
                if (included)
                {
                    // Reuse the updater's chunk verification, with one file per result and no writes.
                    manifest.Files = new List<RmanFile> { file };
                    bool changed = await downloader.VerifyManifestAsync(manifest, targetRoot) > 0;
                    status = changed ? local.Exists ? "Update" : "Download" : "OK";
                    if (changed) pending.Add(new { file.Name, Status = status, LocalPath = path, file.FileSize });
                    if (++checkedFiles % 50 == 0 || checkedFiles == selected.Count)
                        Console.WriteLine($"[Inventory] Verified {checkedFiles}/{selected.Count} selected files.");
                }
                statuses[status] = statuses.GetValueOrDefault(status) + 1;
                object[] row =
                {
                    file.Name, string.Join(";", file.LanguageIds),
                    string.Join(";", file.LanguageIds.Select(id => tags.GetValueOrDefault(id, $"unknown:{id}"))), included,
                    perLocale["en_US"].Contains(file), perLocale["es_ES"].Contains(file),
                    perLocale["es_MX"].Contains(file), perLocale["tr_TR"].Contains(file), status,
                    path, local.Exists ? local.Length.ToString() : "", file.FileSize, file.HashType
                };
                await csv.WriteLineAsync(string.Join(",", row.Select(Csv)));
            }
        }
        finally { manifest.Files = allFiles; }

        var summary = new
        {
            Manifest = source, ManifestId = $"{manifest.ManifestId:x16}", TargetRoot = targetRoot,
            Locales = locales, Filter = filter, TotalFiles = allFiles.Count, SelectedFiles = selected.Count,
            PerLocale = perLocale.ToDictionary(pair => pair.Key, pair => pair.Value.Count),
            Statuses = statuses, Pending = pending, VerifiedAtUtc = DateTime.UtcNow
        };
        await File.WriteAllTextAsync(prefix + ".json", JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(summary));
        Console.WriteLine($"[Inventory] Reports: {prefix}.csv and {prefix}.json");
    }
}
