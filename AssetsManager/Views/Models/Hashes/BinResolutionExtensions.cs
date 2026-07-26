using System;
namespace AssetsManager.Services.Hashes
{
    public static class BinResolutionExtensions
    {
        public static string ResolveBinHash(this HashResolverService resolver, uint hash) => resolver.ResolveBinDomain(hash, 0);
        public static string ResolveBinEntry(this HashResolverService resolver, uint hash) => resolver.ResolveBinDomain(hash, 1);
        public static string ResolveBinField(this HashResolverService resolver, uint hash) => resolver.ResolveBinDomain(hash, 2);
        public static string ResolveBinType(this HashResolverService resolver, uint hash) => resolver.ResolveBinDomain(hash, 3);
    }
}
