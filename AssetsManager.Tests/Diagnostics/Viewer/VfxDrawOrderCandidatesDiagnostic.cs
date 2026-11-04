using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Tests.Diagnostics.Viewer;

/// <summary>Finds authored parent/child draw-order inversions in explicitly selected installed BINs.</summary>
internal static class VfxDrawOrderCandidatesDiagnostic
{
    internal static void Run(string[] args)
    {
        if (args.Length < 2)
            throw new ArgumentException("Usage: vfx-draw-order-candidates <output.json> <bin-path> [bin-path...]");
        string install = InstalledSkins.FindInstall() ?? throw new InvalidOperationException("An installed shader cache is required.");
        var documents = new Dictionary<string, BinTree>(StringComparer.OrdinalIgnoreCase);
        BinTree Load(string path)
        {
            if (!documents.TryGetValue(path, out BinTree tree))
                documents[path] = tree = VfxSnapshotDiagnostic.LoadBin(install, path);
            return tree;
        }
        var results = new List<object>();
        foreach (string binPath in args.Skip(1))
        {
            BinTree tree = Load(binPath) ?? throw new FileNotFoundException("The selected BIN was not found.", binPath);
            VfxDiagnosticCatalog catalog = VfxDiagnosticCatalog.Load(binPath, tree, Load, null);
            int candidates = 0;
            foreach (var parent in catalog.Systems.Values)
            foreach (var emitter in parent.Emitters)
            foreach (var reference in emitter.ChildParticleSet?.Children ?? Array.Empty<VfxChildSystemReference>())
            {
                var child = VfxPlaybackGraphRuntime.ResolveSystem(reference, catalog.Systems, parent.ResourceMap ?? catalog.ResourceMap);
                if (child == null) continue;
                var inversions = new List<object>();
                foreach (var rootEmitter in parent.Emitters)
                foreach (var childEmitter in child.Emitters)
                {
                    var rootPhase = VfxRenderPhaseSemantics.Resolve(rootEmitter);
                    var childPhase = VfxRenderPhaseSemantics.Resolve(childEmitter);
                    if (!(rootPhase.Under && childPhase.Under || rootPhase.Over && childPhase.Over)) continue;
                    if (rootEmitter.IsGroundLayer != childEmitter.IsGroundLayer) continue;
                    if (rootEmitter.HasResolvedCustomMaterial || childEmitter.HasResolvedCustomMaterial) continue;
                    if (rootEmitter.BlendMode == 3 || childEmitter.BlendMode == 3) continue;
                    if (!VfxBlendModes.ShouldSortBackToFront(rootEmitter.BlendMode) &&
                        !VfxBlendModes.ShouldSortBackToFront(childEmitter.BlendMode)) continue;
                    if (VfxDrawOrderUtils.CompareEmitters(rootEmitter, 0, childEmitter, 0) <= 0) continue;
                    inversions.Add(new
                    {
                        Root = Describe(rootEmitter), Child = Describe(childEmitter),
                        Phase = rootPhase.Under ? "under" : "over"
                    });
                }
                if (inversions.Count == 0) continue;
                candidates++;
                results.Add(new { Bin = binPath, Parent = parent.Name, parent.PathHash,
                    SpawnEmitter = emitter.Name, Child = child.Name, Inversions = inversions });
            }
            Console.WriteLine($"[DrawOrder] {binPath}: systems={catalog.Systems.Count}, candidate-links={candidates}.");
        }
        string output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { Install = install, Results = results },
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[DrawOrder] Saved {results.Count} candidate links. Candidates are not confirmed visual defects: {output}");
    }

    private static object Describe(VfxEmitterDefinition definition) => new
    {
        definition.Name, definition.PrimitiveKind, definition.BlendMode,
        Pass = definition.RenderState?.RenderPass ?? 0, definition.IsSimpleEmitter,
        definition.MiscRenderFlags, definition.IsGroundLayer, definition.TexturePath,
        definition.Disabled, definition.HasResolvedCustomMaterial
    };
}
