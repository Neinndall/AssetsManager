using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AssetsManager.Tests.Support;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `bin-object-extract <outDir> <entry-hash-hex>... [--maps|--champions] [--tree]`: writes every installed BIN that declares
    /// one of the given object path hashes to outDir, for bin-ritobin-file.
    /// </summary>
    internal static class BinObjectExtractDiagnostic
    {
        public static void Run(string[] args)
        {
            string output = Directory.CreateDirectory(args[0]).FullName;
            // Arguments ending in .bin name the BIN itself; the others are object path hashes.
            var named = args.Skip(1).Where(arg => arg.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                .Select(arg => XxHash64Ext.Hash(arg.Replace('\\', '/').ToLowerInvariant())).ToHashSet();
            var wanted = args.Skip(1).Where(arg => !arg.StartsWith("--", StringComparison.Ordinal) && !arg.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
                .Select(arg => uint.Parse(arg.Replace("0x", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToHashSet();
            bool maps = !args.Contains("--champions"), champions = !args.Contains("--maps");
            string hashDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AssetsManager", "hashes");
            var binPaths = new Dictionary<ulong, string>();
            foreach (string line in File.ReadLines(Path.Combine(hashDir, "hashes.game.txt")))
            {
                int space = line.IndexOf(' ');
                if (space > 0 && line.EndsWith(".bin", StringComparison.Ordinal) &&
                    ulong.TryParse(line.AsSpan(0, space), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong hash))
                    binPaths[hash] = line[(space + 1)..];
            }
            string final = Path.Combine(InstalledSkins.FindInstall(), @"Game\DATA\FINAL");
            foreach (string wadPath in Directory.GetFiles(final, "*.wad.client", SearchOption.AllDirectories)
                         .Where(path => (champions && path.Contains(@"\Champions\")) || (maps && (path.Contains(@"\Maps\") || Path.GetFileName(path).StartsWith("Common", StringComparison.OrdinalIgnoreCase))))
                         .Where(path => !Path.GetFileName(path)[..^".wad.client".Length].Contains('.')))
            {
                using var wad = new WadFile(wadPath);
                foreach (ulong chunk in wad.Chunks.Keys)
                {
                    if (!binPaths.TryGetValue(chunk, out string binPath)) continue;
                    byte[] bytes;
                    BinTree tree;
                    try
                    {
                        using var data = wad.LoadChunkDecompressed(chunk);
                        bytes = data.Span.ToArray();
                        using var stream = new MemoryStream(bytes, writable: false);
                        tree = new BinTree(stream);
                    }
                    catch { continue; }
                    if (!named.Contains(chunk) && !tree.Objects.Keys.Any(wanted.Contains)) continue;
                    // --tree keeps the game folder layout, so linked BINs resolve as in an extracted project.
                    string target = args.Contains("--tree")
                        ? Path.Combine(output, binPath.Replace('/', Path.DirectorySeparatorChar))
                        : Path.Combine(output, binPath.Replace('/', '_'));
                    // Names past the file system limit are stored as <hash>.bin at the root, as extracted projects do.
                    if (Path.GetFileName(target).Length > 200)
                        target = Path.Combine(output, $"{chunk:x16}.bin");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.WriteAllBytes(target, bytes);
                    Console.WriteLine($"[Extract] {binPath} -> {target} ({Path.GetFileName(wadPath)})");
                }
            }
        }
    }
}
