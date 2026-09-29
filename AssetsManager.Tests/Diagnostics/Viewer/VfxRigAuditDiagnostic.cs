using System;
using System.Linq;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Services.Viewer.Vfx.Semantics;

namespace AssetsManager.Tests.Diagnostics.Viewer;

internal static class VfxRigAuditDiagnostic
{
    internal static void Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: vfx-rig-audit <skin.bin> [system-name-filter]");
            return;
        }
        using var service = new VfxLoadingService();
        var bundle = service.Load(args[0], null);
        string filter = args.Length > 1 ? args[1] : null;
        Console.WriteLine($"Loaded BINs: {bundle.LoadedBins.Count}; spell declarations: {bundle.SpellPreviews.Count}");
        foreach (var system in bundle.Systems.Values.OrderBy(item => item.Name))
        {
            if (filter != null && !system.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            var rig = VfxSystemRigResolver.Resolve(system, bundle,
                hash => bundle.Systems.TryGetValue(hash, out var target) ? target.ParticlePath : null);
            Console.WriteLine($"{system.PathHash:x8} {system.Name}: {rig.Preset} | {rig.Reason}");
        }
    }
}