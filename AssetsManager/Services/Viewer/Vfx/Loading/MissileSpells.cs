using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Loading;

public sealed partial class VfxLoadingService
{
    // Discovery reads must not replace the opened skin's resource, animation or owner context.
    private void LoadMissileSpells(Bundle bundle, Dictionary<string, BinTree> documents,
        string wadRoot, string searchFolder, LogService log, CancellationToken cancellationToken)
    {
        uint resolverClass = Fnv1a.HashLower("ResourceResolver");
        uint[] missileKeys = [Fnv1a.HashLower("mMissileEffectKey"), Fnv1a.HashLower("mMissileEffectPlayerKey"),
            Fnv1a.HashLower("mMissileEffectEnemyKey")];
        var cache = new Dictionary<string, BinTree>(StringComparer.OrdinalIgnoreCase);
        int reads = 0;

        BinTree Read(string path)
        {
            if (cache.TryGetValue(path, out var cached)) return cached;
            cache[path] = null;
            cancellationToken.ThrowIfCancellationRequested();
            if (reads++ >= Services.Viewer.Loading.BinDocumentClosureLoader.MaximumLinkedBins) return null;
            try
            {
                string resolved = _resources.ResolveLinkedBins(path, wadRoot, searchFolder).FirstOrDefault();
                BinTree tree = null;
                if (resolved != null)
                    tree = documents.GetValueOrDefault(Path.GetFullPath(resolved)) ?? VfxGraphParser.ParseTree(File.ReadAllBytes(resolved));
                else if (_assetResolver != null)
                {
                    var asset = _assetResolver.ResolveVirtualAsync(path, searchFolder, cancellationToken).GetAwaiter().GetResult();
                    if (asset != null)
                    {
                        using var stream = _assetResolver.OpenReadAsync(asset, cancellationToken).GetAwaiter().GetResult();
                        if (stream != null) tree = new BinTree(stream);
                    }
                }
                cache[path] = tree;
                return tree;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { log?.LogDebug($"Could not read missile spell catalog {path}: {ex.Message}"); return null; }
        }

        HashSet<uint> KeysTo(BinTree tree, uint system)
        {
            var keys = new HashSet<uint>();
            if (tree != null)
                foreach (var resolver in tree.Objects.Values.Where(o => o.ClassHash == resolverClass))
                    foreach (var pair in VfxResourceParser.ExtractResourceMap(resolver.Properties))
                        if (pair.Value == system) keys.Add(pair.Key);
            return keys;
        }

        foreach (var system in bundle.Systems.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = ResolveBinEntryPath(system.PathHash);
            if (path == null && !string.IsNullOrEmpty(system.ParticlePath) &&
                Fnv1a.HashLower(system.ParticlePath) == system.PathHash) path = system.ParticlePath;
            string[] parts = path?.Split('/');
            if (parts is not { Length: >= 2 } || !parts[0].Equals("characters", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(parts[1])) continue;

            var keys = KeysTo(documents.GetValueOrDefault(bundle.PrimaryBinPath), system.PathHash);
            string skin = VfxLinkedChildResolver.SkinBin(path);
            if (skin != null) keys.UnionWith(KeysTo(Read(skin), system.PathHash));
            if (keys.Count == 0) continue;
            string character = parts[1].ToLowerInvariant();
            var spells = Read($"data/characters/{character}/{character}.bin");
            if (spells == null) continue;

            var found = new List<VfxSpellPreview>();
            foreach (var obj in spells.Objects.Values.Where(o => o.ClassHash == SpellObjectClass))
            {
                if (VfxValueParser.Get(obj.Properties, Fnv1a.HashLower("mSpell")) is not BinTreeStruct spell ||
                    !missileKeys.Any(key => VfxValueParser.Get(spell.Properties, key) is BinTreeHash hash && keys.Contains(hash.Value))) continue;
                var preview = VfxSpellPreviewReader.Read(obj);
                if (preview.Missile == null) continue;
                string name = preview.ScriptName ?? ResolveBinEntryPath(obj.PathHash) ?? $"0x{obj.PathHash:x8}";
                found.Add(preview with { ScriptName = name });
            }
            if (found.Count > 0) bundle.MissileSpells[system.PathHash] = found;
        }
    }
}
