using System;
using System.IO;
using System.Linq;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Viewer.Vfx.Composition;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Utils;
using LeagueToolkit.Hashing;

namespace AssetsManager.Tests.Diagnostics.Viewer;

internal static class AnimationCatalogAuditDiagnostic
{
    internal static void Run(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            Console.WriteLine("Usage: animation-catalog-audit <skin.bin>");
            return;
        }

        string skinPath = Path.GetFullPath(args[0]);
        using var logger = new Serilog.LoggerConfiguration().CreateLogger();
        var log = new LogService(logger);
        using var hashes = new HashResolverService(new DirectoriesCreator(), log);
        hashes.LoadAllHashesAsync().GetAwaiter().GetResult();
        using var loader = new VfxLoadingService(hashes);
        var bundle = loader.Load(skinPath, log);
        string searchDirectory = Path.GetDirectoryName(skinPath);
        string Resolve(string path) => loader.ResolveAssetPath(path, searchDirectory, ".anm");
        using var catalog = new VfxClipCatalog();
        var selected = VfxClipCatalog.SelectGraphClips(bundle.Clips, bundle.OwnerSceneContext?.AnimationGraphPathHash ?? 0);
        var items = catalog.BuildMetadata(bundle, Resolve);
        Console.WriteLine($"[Catalog] skin={skinPath} graph=0x{bundle.OwnerSceneContext?.AnimationGraphPathHash:x8} " +
                          $"allClips={bundle.Clips.Count} selected={selected.Count} listed={items.Count}");
        foreach (string bin in bundle.LoadedBins) Console.WriteLine($"[BIN] {bin}");
        foreach (var item in items)
        {
            Console.WriteLine($"[Clip] 0x{item.Clip.OwnerPathHash:x8} name={item.Name} " +
                              $"class=0x{item.Clip.OwnerClassHash:x8} authored={item.Clip.AnimationFilePath} resolved={item.FilePath}");
            if (item.Name.IndexOf("recall", StringComparison.OrdinalIgnoreCase) < 0 &&
                !item.Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
            Console.WriteLine($"[NameTable] 0x{item.Clip.OwnerPathHash:x8} BIN={hashes.ResolveBinHash(item.Clip.OwnerPathHash)}");
            for (int gear = 0; gear < 3; gear++)
            {
                var playlist = VfxClipCatalog.ResolvePlaylist(item.Clip, selected, gearIndex: gear);
                Console.WriteLine($"[Playlist] clip=0x{item.Clip.OwnerPathHash:x8} gear={gear} " +
                                  string.Join("; ", playlist.Select(clip => $"0x{clip.OwnerPathHash:x8}:{clip.AnimationFilePath} => {Resolve(clip.AnimationFilePath)}")));
            }
            var prepared = catalog.PrepareAsync(item, bundle, Resolve, log, default).GetAwaiter().GetResult();
            Console.WriteLine($"[Decoded] clip=0x{item.Clip.OwnerPathHash:x8} duration={prepared.Duration}");
        }
        foreach (var item in items.Where(item => !string.IsNullOrWhiteSpace(item.FilePath)))
        {
            string stem = Path.GetFileNameWithoutExtension(item.FilePath);
            if (Fnv1a.HashLower(stem) == item.Clip.OwnerPathHash)
                Console.WriteLine($"[VerifiedName] {stem}=0x{item.Clip.OwnerPathHash:x8}");
        }
    }
}
