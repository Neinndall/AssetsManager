using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>Installed BIN closure for VFX diagnostics, retaining each system's declaring resource map.</summary>
    internal sealed record VfxDiagnosticCatalog(
        IReadOnlyDictionary<uint, VfxSystemDefinition> Systems,
        IReadOnlyDictionary<uint, uint> ResourceMap,
        VfxOwnerSceneContext Owner,
        IReadOnlyCollection<uint> PrimarySystemHashes)
    {
        internal static VfxDiagnosticCatalog Load(string primaryPath, BinTree primary,
            Func<string, BinTree> loadBin, IEnumerable<BinTree> shaderTrees)
        {
            ArgumentNullException.ThrowIfNull(primary);
            var systems = new Dictionary<uint, VfxSystemDefinition>();
            var trees = new List<BinTree>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Normalize(primaryPath) };
            var pending = new Queue<string>();
            uint[] primaryHashes = Array.Empty<uint>();
            IReadOnlyDictionary<uint, uint> resourceMap = null;
            VfxOwnerSceneContext owner = null;
            int opened = 0;
            Add(primary, isPrimary: true);
            while (pending.Count > 0 && opened < BinDocumentClosureLoader.MaximumLinkedBins)
            {
                string path = pending.Dequeue();
                if (!seen.Add(path)) continue;
                opened++;
                BinTree tree = loadBin(path);
                if (tree != null) Add(tree, isPrimary: false);
            }
            foreach (uint hash in systems.Keys.ToArray())
                systems[hash] = VfxGraphParser.ResolveLinkedCustomMaterials(systems[hash], trees, shaderTrees: shaderTrees);
            return new VfxDiagnosticCatalog(systems, resourceMap, owner, primaryHashes);

            void Add(BinTree tree, bool isPrimary)
            {
                VfxBinDocument document = VfxGraphParser.ParseDocument(tree, shaderTrees: shaderTrees);
                trees.Add(tree);
                if (isPrimary)
                {
                    resourceMap = document.SkinResourceMap;
                    primaryHashes = document.Systems.Keys.ToArray();
                }
                owner ??= document.OwnerSceneContext;
                foreach (var pair in document.Systems) systems.TryAdd(pair.Key, pair.Value);
                foreach (string dependency in document.Dependencies)
                    if (!string.IsNullOrWhiteSpace(dependency)) pending.Enqueue(Normalize(dependency));
            }
        }

        private static string Normalize(string path) => path.Replace('\\', '/').ToLowerInvariant();
    }
}
