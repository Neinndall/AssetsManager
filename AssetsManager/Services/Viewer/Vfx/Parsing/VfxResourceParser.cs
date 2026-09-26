using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using static AssetsManager.Services.Viewer.Vfx.Parsing.VfxParsingSchema;
using static AssetsManager.Services.Viewer.Vfx.Parsing.VfxValueParser;

namespace AssetsManager.Services.Viewer.Vfx.Parsing
{
    internal static class VfxResourceParser
    {
        private static readonly uint ResolverClass = VfxParsingHash.Fnv1a("ResourceResolver");
        private static readonly uint F_resourceMap = VfxParsingHash.Fnv1a("resourceMap");
        private static readonly uint F_mResourceMap = VfxParsingHash.Fnv1a("mResourceMap");
        private static readonly uint F_mResourceResolver = VfxParsingHash.Fnv1a("mResourceResolver");


        internal static IReadOnlyDictionary<uint, uint> ExtractResourceMap(BinTree tree)
        {
            var map = new Dictionary<uint, uint>();
            foreach (BinTreeObject resolver in tree.Objects.Values)
            {
                if (resolver.ClassHash == ResolverClass)
                    AppendResolverEntries(resolver, map);
            }
            return map;
        }

        internal static IReadOnlyDictionary<uint, uint> ExtractResourceMap(
            IReadOnlyDictionary<uint, BinTreeProperty> resolverProperties)
        {
            var map = new Dictionary<uint, uint>();
            AppendResolverEntries(resolverProperties, map);
            return map;
        }

        internal static IReadOnlyDictionary<uint, uint> ExtractSkinResourceMap(BinTree tree)
        {
            foreach (BinTreeObject skin in tree.Objects.Values)
            {
                if (skin.ClassHash != SkinCharacterDataPropertiesClass) continue;
                uint resolverHash = AsU32(Get(skin.Properties, F_mResourceResolver)) ?? 0u;
                if (resolverHash == 0 ||
                    !tree.Objects.TryGetValue(resolverHash, out BinTreeObject resolver) ||
                    resolver.ClassHash != ResolverClass)
                {
                    continue;
                }

                var map = new Dictionary<uint, uint>();
                AppendResolverEntries(resolver, map);
                return map;
            }
            return new Dictionary<uint, uint>();
        }

        private static void AppendResolverEntries(BinTreeObject resolver, Dictionary<uint, uint> map)
            => AppendResolverEntries(resolver.Properties, map);

        private static void AppendResolverEntries(
            IReadOnlyDictionary<uint, BinTreeProperty> resolverProperties,
            Dictionary<uint, uint> map)
        {
            if (!resolverProperties.TryGetValue(F_resourceMap, out BinTreeProperty prop) &&
                !resolverProperties.TryGetValue(F_mResourceMap, out prop))
                return;
            if (prop is not BinTreeMap entries) return;

            foreach (KeyValuePair<BinTreeProperty, BinTreeProperty> entry in entries)
            {
                uint key = AsU32(entry.Key) ?? 0u;
                uint value = AsU32(entry.Value) ?? 0u;
                // A null resolver link is still an authored hit in LTK. Keep key -> 0 so
                // the first resolver can suppress the effect instead of letting a later
                // resolver or direct-hash fallback answer the same key.
                if (key != 0) map.TryAdd(key, value);
            }
        }
    }
}
