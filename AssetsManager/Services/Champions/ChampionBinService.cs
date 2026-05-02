using System;
using System.Collections.Generic;
using System.Linq;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Champions
{
    public static class ChampionBinService
    {
        public static uint GetBinHash(string name)
        {
            if (uint.TryParse(name, System.Globalization.NumberStyles.HexNumber, null, out uint h)) return h;
            // TRY MULTIPLE HASHES: Original and Lowercase (Riot is inconsistent)
            return Fnv1a.HashLower(name);
        }

        public static uint GetBinHashSensitive(string name)
        {
            // For case-sensitive properties like "BaseMR"
            return Fnv1a.Hash(name);
        }

        public static float GetStatValue(BinTreeObject root, string name, string fallback = null)
        {
            string[] variants = { name, name + "Modifiable", name + "Mod", fallback };
            foreach (var v in variants)
            {
                if (v == null) continue;
                // Try original and lowercase
                if (root.Properties.TryGetValue(Fnv1a.Hash(v), out var p)) return GetFloatFromProperty(p);
                if (root.Properties.TryGetValue(Fnv1a.HashLower(v), out var p2)) return GetFloatFromProperty(p2);
                if (uint.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out uint h) && root.Properties.TryGetValue(h, out var p3)) return GetFloatFromProperty(p3);
            }
            return 0;
        }

        public static float GetStatValue(BinTreeStruct root, string name, string fallback = null)
        {
            string[] variants = { name, name + "Modifiable", name + "Mod", fallback };
            foreach (var v in variants)
            {
                if (v == null) continue;
                if (root.Properties.TryGetValue(Fnv1a.Hash(v), out var p)) return GetFloatFromProperty(p);
                if (root.Properties.TryGetValue(Fnv1a.HashLower(v), out var p2)) return GetFloatFromProperty(p2);
                if (uint.TryParse(v, System.Globalization.NumberStyles.HexNumber, null, out uint h) && root.Properties.TryGetValue(h, out var p3)) return GetFloatFromProperty(p3);
            }
            return 0;
        }

        public static float GetFloatFromProperty(BinTreeProperty p)
        {
            if (p is BinTreeF32 f) return f.Value;
            if (p is BinTreeI32 i) return (float)i.Value;
            if (p is BinTreeU32 u) return (float)u.Value;
            if (p is BinTreeStruct s)
            {
                // Recursive search for BaseValue
                if (s.Properties.TryGetValue(Fnv1a.Hash("BaseValue"), out var bp)) return GetFloatFromProperty(bp);
                if (s.Properties.TryGetValue(Fnv1a.HashLower("BaseValue"), out var bp2)) return GetFloatFromProperty(bp2);
            }
            return 0;
        }

        public static T GetPropValue<T>(BinTreeObject obj, string propName, T @default = default)
        {
            if (obj.Properties.TryGetValue(Fnv1a.Hash(propName), out var p)) return CastProperty<T>(p);
            if (obj.Properties.TryGetValue(Fnv1a.HashLower(propName), out var p2)) return CastProperty<T>(p2);
            return @default;
        }

        public static T GetPropValue<T>(BinTreeStruct str, string propName, T @default = default)
        {
            if (str.Properties.TryGetValue(Fnv1a.Hash(propName), out var p)) return CastProperty<T>(p);
            if (str.Properties.TryGetValue(Fnv1a.HashLower(propName), out var p2)) return CastProperty<T>(p2);
            return @default;
        }

        public static T CastProperty<T>(BinTreeProperty p)
        {
            if (p == null) return default;
            if (typeof(T) == typeof(string) && p is BinTreeString s) return (T)(object)s.Value;
            if (typeof(T) == typeof(float) && p is BinTreeF32 f) return (T)(object)f.Value;
            if (typeof(T) == typeof(int) && p is BinTreeI32 i) return (T)(object)i.Value;
            if (typeof(T) == typeof(uint) && p is BinTreeU32 u) return (T)(object)u.Value;
            if (typeof(T) == typeof(ulong) && p is BinTreeObjectLink ol) return (T)(object)ol.Value;
            if (typeof(T) == typeof(bool) && p is BinTreeBool b) return (T)(object)b.Value;
            if (typeof(T) == typeof(List<float>) && p is BinTreeContainer c) 
                return (T)(object)c.Elements.Select(GetFloatFromProperty).ToList();
            if (typeof(T) == typeof(List<string>) && p is BinTreeContainer c2) 
                return (T)(object)c2.Elements.Select(e => {
                    if (e is BinTreeString s) return s.Value;
                    if (e is BinTreeObjectLink ol) return ol.Value.ToString("x8");
                    return "";
                }).ToList();
            if (typeof(T) == typeof(BinTreeStruct) && p is BinTreeStruct st) return (T)(object)st;
            return default;
        }
    }
}
