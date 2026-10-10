using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using LeagueToolkit.Core.Wad;
using Xunit;

namespace AssetsManager.Tests.Support;

public sealed class InstalledChampionProjects : IDisposable
{
    private readonly Dictionary<string, string> _roots = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _temporaryRoots = new();
    private static string HashesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AssetsManager", "hashes", "hashes.game.txt");
    private static string ConfiguredRoot(string champion) =>
        Environment.GetEnvironmentVariable($"ASSETSMANAGER_{champion.ToUpperInvariant()}_DIAGNOSTIC_ROOT");
    private static string WadPath(string champion) => InstalledSkins.FindInstall() is { } install
        ? Path.Combine(install, "Game", "DATA", "FINAL", "Champions", champion + ".wad.client") : null;

    internal static string UnavailableReason(string champion)
    {
        if (ConfiguredRoot(champion) is { Length: > 0 } root)
            return Directory.Exists(root) ? null : $"The configured {champion} project folder does not exist.";
        return File.Exists(WadPath(champion)) && File.Exists(HashesPath) ? null
            : $"Provide an extracted {champion} project or an installed client and hashes.game.txt.";
    }

    internal string GetRoot(string champion)
    {
        if (_roots.TryGetValue(champion, out string held)) return held;
        if (ConfiguredRoot(champion) is { Length: > 0 } configured)
            return _roots[champion] = configured;

        string root = Directory.CreateTempSubdirectory($"am-{champion.ToLowerInvariant()}-project-").FullName;
        _temporaryRoots.Add(root);
        using var wad = new WadFile(WadPath(champion));
        int extracted = 0;
        // Preserve real BIN links and local textures, without changing the installed WAD or catalogs.
        foreach (string line in File.ReadLines(HashesPath))
        {
            int space = line.IndexOf(' ');
            if (space < 0 || !ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out ulong hash) || !wad.Chunks.TryGetValue(hash, out var chunk)) continue;
            string logical = line[(space + 1)..].Trim();
            if (Path.GetExtension(logical).ToLowerInvariant() is not (".bin" or ".skn" or ".skl" or ".tex")) continue;
            string destination = Path.GetFullPath(Path.Combine(root, logical));
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Asset path is outside the temporary project: {logical}");
            // Windows cannot store very long BIN names; the production index resolves WAD hash names.
            if (Path.GetFileName(destination).Length > 240)
                destination = Path.Combine(Path.GetDirectoryName(destination)!, $"{hash:x16}{Path.GetExtension(logical)}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var data = wad.LoadChunkDecompressed(chunk);
            using var output = File.Create(destination);
            output.Write(data.Span);
            extracted++;
        }
        if (extracted == 0) throw new InvalidDataException($"No named assets were extracted from {champion}'s WAD.");
        return _roots[champion] = root;
    }

    public void Dispose()
    {
        foreach (string root in _temporaryRoots) Directory.Delete(root, recursive: true);
    }
}

public sealed class InstalledChampionFactAttribute : FactAttribute
{
    public InstalledChampionFactAttribute(string champion) => Skip = InstalledChampionProjects.UnavailableReason(champion);
}
