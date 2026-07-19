using System;
using System.Collections.Generic;
using System.Reflection;
using AssetsManager.Utils;

namespace AssetsManager.Services.Hashes
{
    public static class BinResolutionExtensions
    {
        private static readonly FieldInfo BinCachesField = typeof(HashResolverService).GetField("_binCaches", BindingFlags.NonPublic | BindingFlags.Instance);

        public static string ResolveBinHash(this HashResolverService resolver, uint hash) => ResolveDomain(resolver, hash, 0);
        public static string ResolveBinEntry(this HashResolverService resolver, uint hash) => ResolveDomain(resolver, hash, 1);
        public static string ResolveBinField(this HashResolverService resolver, uint hash) => ResolveDomain(resolver, hash, 2);
        public static string ResolveBinType(this HashResolverService resolver, uint hash) => ResolveDomain(resolver, hash, 3);

        private static string ResolveDomain(HashResolverService resolver, uint hash, int index)
        {
            var caches = (List<BinaryHashCache>)BinCachesField?.GetValue(resolver);
            if (caches != null && index >= 0 && index < caches.Count)
            {
                var result = caches[index].Resolve(hash);
                if (result != null) return result;
            }
            return hash.ToString("x8");
        }
    }
}
