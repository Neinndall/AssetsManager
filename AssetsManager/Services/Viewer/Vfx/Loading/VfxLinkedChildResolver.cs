using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Loading;

/// <summary>Resolves missing child keys through the opened document and the owning skin's BIN.</summary>
internal static class VfxLinkedChildResolver
{
    internal static string SkinBin(string path)
    {
        string[] parts = path?.Split('/');
        return parts is { Length: >= 5 } && parts[0].Equals("characters", StringComparison.OrdinalIgnoreCase) &&
            parts[2].Equals("skins", StringComparison.OrdinalIgnoreCase)
                ? $"data/characters/{parts[1].ToLowerInvariant()}/skins/{parts[3].ToLowerInvariant()}.bin" : null;
    }

    internal static IEnumerable<uint> MissingKeys(VfxSystemDefinition system,
        IReadOnlyDictionary<uint, VfxSystemDefinition> systems)
    {
        foreach (var emitter in system.Emitters)
        foreach (var child in emitter.ChildParticleSet?.Children ?? Array.Empty<VfxChildSystemReference>())
        {
            if (child is null || child.EffectKey == 0 ||
                (child.SystemHash != 0 && systems.ContainsKey(child.SystemHash)) ||
                system.ResourceMap?.ContainsKey(child.EffectKey) == true) continue;
            yield return child.EffectKey;
        }
    }

    internal static VfxSystemDefinition Resolve(VfxSystemDefinition system,
        IReadOnlyDictionary<uint, VfxSystemDefinition> systems,
        IReadOnlyDictionary<uint, uint> opened, IReadOnlyDictionary<uint, uint> skin)
    {
        Dictionary<uint, uint> map = null;
        foreach (uint key in MissingKeys(system, systems).Distinct())
        {
            uint target = 0;
            if (opened?.TryGetValue(key, out target) != true && skin?.TryGetValue(key, out target) != true) continue;
            map ??= system.ResourceMap == null ? new() : new(system.ResourceMap);
            map.TryAdd(key, target);
        }
        return map == null ? system : system with { ResourceMap = map };
    }
}
