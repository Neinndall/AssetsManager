using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;


namespace AssetsManager.Services.Viewer.Vfx.Parsing
{
    internal static class VfxValueParser
    {
        // Value* / dynamics inner fields
        private static readonly uint F_constantValue = VfxParsingHash.Fnv1a("constantValue");
        private static readonly uint F_dynamics      = VfxParsingHash.Fnv1a("dynamics");
        private static readonly uint F_times         = VfxParsingHash.Fnv1a("times");
        private static readonly uint F_values        = VfxParsingHash.Fnv1a("values");
        private static readonly uint F_probTables    = VfxParsingHash.Fnv1a("probabilityTables");
        private static readonly uint F_keyTimes      = VfxParsingHash.Fnv1a("keyTimes");
        private static readonly uint F_keyValues     = VfxParsingHash.Fnv1a("keyValues");
        private static readonly uint F_singleValue   = VfxParsingHash.Fnv1a("singleValue");
        private static readonly uint C_valueColorRgb = VfxParsingHash.Fnv1a("ValueColorRgb");

        internal static VfxCurveF? ReadCurveF(
            IReadOnlyDictionary<uint, BinTreeProperty> p,
            uint field,
            float structFallback = 0f)
        {
            return p.TryGetValue(field, out var prop) ? ReadCurveFProperty(prop, structFallback) : null;
        }

        internal static VfxCurveF? ReadCurveFProperty(BinTreeProperty prop, float structFallback = 0f)
        {
            if (prop is BinTreeStruct v)
            {
                float c = AsF32(Get(v.Properties, F_constantValue)) ?? structFallback;
                var (times, vals) = ReadDynamics(v.Properties, AsF32);
                return new VfxCurveF(c, times, vals, ReadNestedProbTables(v.Properties));
            }
            return AsF32(prop) is { } scalar ? VfxCurveF.Const(scalar) : null;
        }

        internal static VfxCurve3? ReadCurve3(
            IReadOnlyDictionary<uint, BinTreeProperty> p,
            uint field,
            Vector3? structFallback = null)
        {
            return p.TryGetValue(field, out var prop) ? ReadCurve3Property(prop, structFallback) : null;
        }

        internal static VfxCurve2? ReadCurve2(
            IReadOnlyDictionary<uint, BinTreeProperty> p,
            uint field,
            Vector2? structFallback = null)
        {
            if (!p.TryGetValue(field, out var prop)) return null;
            if (prop is BinTreeStruct value)
            {
                AuthoredValue<Vector2>? held = ReadCurveVector2(Get(value.Properties, F_constantValue));
                Vector2 constant = held?.Value ?? structFallback ?? Vector2.Zero;
                byte constantWidth = held?.Width ?? 2;
                var (times, values, widths) = ReadDynamicsWithWidths(value.Properties, ReadCurveVector2);
                return new VfxCurve2(
                    constant,
                    times,
                    values,
                    ReadNestedProbTables(value.Properties),
                    constantWidth,
                    widths);
            }
            if (ReadCurveVector2(prop) is { } direct)
                return new VfxCurve2(direct.Value, null, null, null, direct.Width);
            return null;
        }

        internal static VfxCurve3? ReadCurve3Property(BinTreeProperty prop, Vector3? structFallback = null)
        {
            if (prop is BinTreeStruct v)
            {
                AuthoredValue<Vector3>? held = ReadCurveVector3(Get(v.Properties, F_constantValue));
                Vector3 constant = held?.Value ?? structFallback ?? Vector3.Zero;
                byte constantWidth = held?.Width ?? 3;
                var (times, vals, widths) = ReadDynamicsWithWidths(v.Properties, ReadCurveVector3);
                return new VfxCurve3(
                    constant,
                    times,
                    vals,
                    ReadNestedProbTables(v.Properties),
                    constantWidth,
                    widths);
            }
            if (ReadCurveVector3(prop) is { } direct)
                return new VfxCurve3(direct.Value, null, null, null, direct.Width);
            return null;
        }

        internal static VfxCurve4? ReadCurve4(
            IReadOnlyDictionary<uint, BinTreeProperty> p,
            uint field,
            Vector4? structFallback = null)
        {
            if (!p.TryGetValue(field, out var prop)) return null;
            if (prop is BinTreeStruct v)
            {
                Func<BinTreeProperty, AuthoredValue<Vector4>?> reader =
                    v.ClassHash == C_valueColorRgb ? ReadCurveColorRgb : ReadCurveVector4;
                AuthoredValue<Vector4>? held = reader(Get(v.Properties, F_constantValue));
                Vector4 constant = held?.Value ?? structFallback ?? Vector4.One;
                byte constantWidth = held?.Width ?? 4;
                var (times, vals, widths) = ReadDynamicsWithWidths(v.Properties, reader);
                return new VfxCurve4(
                    constant,
                    times,
                    vals,
                    ReadNestedProbTables(v.Properties),
                    constantWidth,
                    widths);
            }
            if (ReadCurveVector4(prop) is { } direct)
                return new VfxCurve4(direct.Value, null, null, null, direct.Width);
            return null;
        }

        internal static VfxProbTable[] ReadProbTables(IReadOnlyDictionary<uint, BinTreeProperty> valueProps)
        {
            if (Get(valueProps, F_probTables) is not BinTreeContainer pc || pc.Elements.Count == 0) return null;
            var tables = new VfxProbTable[pc.Elements.Count];
            bool any = false;
            for (int tableIndex = 0; tableIndex < pc.Elements.Count; tableIndex++)
            {
                if (pc.Elements[tableIndex] is not BinTreeStruct s) continue;

                // LTK treats the table itself as present even with no keys. In that case
                // singleValue (schema default 1) is the multiplier for the whole channel.
                float single = GetF32(s.Properties, F_singleValue) ?? 1f;
                var tc = Get(s.Properties, F_keyTimes) as BinTreeContainer;
                var vc = Get(s.Properties, F_keyValues) as BinTreeContainer;

                if (tc is not null && vc is not null && tc.Elements.Count > 0 && tc.Elements.Count != vc.Elements.Count)
                {
                    // The engine reads mismatched keyed tables as zero instead of truncating
                    // to the shorter list.
                    tables[tableIndex] = new VfxProbTable(null, null, 0f, IsPresent: true);
                    any = true;
                    continue;
                }

                int n = tc is not null && vc is not null ? Math.Min(tc.Elements.Count, vc.Elements.Count) : 0;
                var times = new List<float>(n);
                var vals = new List<float>(n);
                for (int i = 0; i < n; i++)
                {
                    float? time = AsF32(tc.Elements[i]);
                    float? value = AsF32(vc.Elements[i]);
                    if (!time.HasValue || !value.HasValue) continue;
                    times.Add(time.Value);
                    vals.Add(value.Value);
                }

                tables[tableIndex] = new VfxProbTable(
                    times.Count > 0 ? times.ToArray() : null,
                    vals.Count > 0 ? vals.ToArray() : null,
                    single,
                    IsPresent: true);
                any = true;
            }
            return any ? tables : null;
        }

        internal static VfxProbTable[] ReadNestedProbTables(IReadOnlyDictionary<uint, BinTreeProperty> valueProps)
        {
            if (Get(valueProps, F_dynamics) is not BinTreeStruct dynamics) return null;
            return ReadProbTables(dynamics.Properties);
        }

        internal static (float[], T[]) ReadDynamics<T>(IReadOnlyDictionary<uint, BinTreeProperty> valueProps, Func<BinTreeProperty, T?> conv)
            where T : struct
        {
            if (Get(valueProps, F_dynamics) is not BinTreeStruct dyn) return (null, null);
            if (Get(dyn.Properties, F_times) is not BinTreeContainer tc) return (null, null);
            if (Get(dyn.Properties, F_values) is not BinTreeContainer vc) return (null, null);

            int n = Math.Min(tc.Elements.Count, vc.Elements.Count);
            if (n == 0) return (null, null);
            var times = new List<float>(n);
            var vals = new List<T>(n);
            for (int i = 0; i < n; i++)
            {
                float? time = AsF32(tc.Elements[i]);
                T? value = conv(vc.Elements[i]);
                if (!time.HasValue || !value.HasValue) continue;
                times.Add(time.Value);
                vals.Add(value.Value);
            }
            return times.Count > 0 ? (times.ToArray(), vals.ToArray()) : (null, null);
        }

        private readonly record struct AuthoredValue<T>(T Value, byte Width) where T : struct;

        private static (float[], T[], byte[]) ReadDynamicsWithWidths<T>(
            IReadOnlyDictionary<uint, BinTreeProperty> valueProps,
            Func<BinTreeProperty, AuthoredValue<T>?> conv)
            where T : struct
        {
            if (Get(valueProps, F_dynamics) is not BinTreeStruct dyn) return (null, null, null);
            if (Get(dyn.Properties, F_times) is not BinTreeContainer tc) return (null, null, null);
            if (Get(dyn.Properties, F_values) is not BinTreeContainer vc) return (null, null, null);

            int n = Math.Min(tc.Elements.Count, vc.Elements.Count);
            if (n == 0) return (null, null, null);
            var times = new List<float>(n);
            var values = new List<T>(n);
            var widths = new List<byte>(n);
            for (int i = 0; i < n; i++)
            {
                float? time = AsF32(tc.Elements[i]);
                AuthoredValue<T>? value = conv(vc.Elements[i]);
                if (!time.HasValue || !value.HasValue) continue;
                times.Add(time.Value);
                values.Add(value.Value.Value);
                widths.Add(value.Value.Width);
            }
            return times.Count > 0
                ? (times.ToArray(), values.ToArray(), widths.ToArray())
                : (null, null, null);
        }

        private static AuthoredValue<Vector2>? ReadCurveVector2(BinTreeProperty property)
        {
            if (property is BinTreeVector2 v2) return new AuthoredValue<Vector2>(v2.Value, 2);
            if (property is BinTreeVector3 v3) return new AuthoredValue<Vector2>(new Vector2(v3.Value.X, v3.Value.Y), 2);
            return AsF32(property) is { } scalar
                ? new AuthoredValue<Vector2>(new Vector2(scalar, 0f), 1)
                : null;
        }

        private static AuthoredValue<Vector3>? ReadCurveVector3(BinTreeProperty property)
        {
            if (property is BinTreeVector3 v3) return new AuthoredValue<Vector3>(v3.Value, 3);
            if (property is BinTreeVector2 v2) return new AuthoredValue<Vector3>(new Vector3(v2.Value, 0f), 2);
            return AsF32(property) is { } scalar
                ? new AuthoredValue<Vector3>(new Vector3(scalar, 0f, 0f), 1)
                : null;
        }

        private static AuthoredValue<Vector4>? ReadCurveVector4(BinTreeProperty property)
        {
            if (property is BinTreeVector4 v4) return new AuthoredValue<Vector4>(v4.Value, 4);
            if (property is BinTreeColor color) return new AuthoredValue<Vector4>(color.Value, 4);
            if (property is BinTreeVector3 v3) return new AuthoredValue<Vector4>(new Vector4(v3.Value, 0f), 3);
            if (property is BinTreeVector2 v2) return new AuthoredValue<Vector4>(new Vector4(v2.Value, 0f, 0f), 2);
            return AsF32(property) is { } scalar
                ? new AuthoredValue<Vector4>(new Vector4(scalar, 0f, 0f, 0f), 1)
                : null;
        }

        private static AuthoredValue<Vector4>? ReadCurveColorRgb(BinTreeProperty property)
        {
            AuthoredValue<Vector4>? held = ReadCurveVector4(property);
            if (held is not { } value) return null;
            if (value.Width != 3) return value;

            // ValueColorRgb is authored as RGB but is semantically a colour, not a generic Vec3.
            // LTK reads its missing alpha as fully opaque while keeping only three authored/table slots.
            return new AuthoredValue<Vector4>(
                new Vector4(value.Value.X, value.Value.Y, value.Value.Z, 1f),
                value.Width);
        }

        internal static BinTreeProperty Get(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => p.TryGetValue(hash, out var v) ? v : null;

        internal static string GetString(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeString s ? s.Value : null;

        internal static IReadOnlyList<uint> ReadSubmeshNameHashes(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<uint>();
            return value
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(token => token.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(VfxParsingHash.Fnv1a)
                .Distinct()
                .ToArray();
        }

        internal static string ReadAsset(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash, string extension)
            => ReadAsset(Get(p, hash), extension);

        internal static string ReadAsset(BinTreeProperty prop, string extension)
        {
            if (prop is BinTreeOptional opt)
                prop = opt.Value;

            return prop switch
            {
                BinTreeString text => string.IsNullOrWhiteSpace(text.Value) ? null : text.Value,
                BinTreeWadChunkLink link when link.Value != 0 => $"{link.Value:x16}{extension}",
                BinTreeU64 u64 when u64.Value != 0 => $"{u64.Value:x16}{extension}",
                BinTreeHash h when h.Value != 0 => $"{h.Value:x8}{extension}",
                BinTreeObjectLink ol when ol.Value != 0 => $"{ol.Value:x8}{extension}",
                BinTreeU32 u32 when u32.Value != 0 => $"{u32.Value:x8}{extension}",
                _ => null
            };
        }

        internal static bool IsNoMeshPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return true;
            string fileName = Path.GetFileName(path.Replace('\\', '/'));
            return fileName.StartsWith("doesnotexist.", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsSupportedSimpleMeshPath(string path)
            => VfxMeshFormatSemantics.IsAuthoredSimplePath(path);

        internal static byte NormalizeEnumByte(int? value, int maxInclusive, byte fallback)
            => value is >= 0 && value <= maxInclusive ? (byte)value.Value : fallback;

        internal static float? GetF32(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash) => AsF32(Get(p, hash));

        internal static float? GetOptionalF32(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeOptional o ? AsF32(o.Value) : AsF32(Get(p, hash));

        internal static int? GetU8(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
        {
            long? value = AsInteger(Get(p, hash));
            return value is >= byte.MinValue and <= byte.MaxValue ? (int)value.Value : null;
        }

        internal static int? GetU16(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
        {
            long? value = AsInteger(Get(p, hash));
            return value is >= ushort.MinValue and <= ushort.MaxValue ? (int)value.Value : null;
        }

        internal static Vector2? GetVec2(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => Get(p, hash) is BinTreeVector2 v ? v.Value : null;

        internal static Vector4? GetVec4(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
            => AsVec4(Get(p, hash));

        internal static Vector2? ReadValueVec2(BinTreeProperty p) => p switch
        {
            BinTreeStruct value => AsVec2(Get(value.Properties, F_constantValue)),
            _ => AsVec2(p),
        };

        internal static bool GetBool(
            IReadOnlyDictionary<uint, BinTreeProperty> p,
            uint hash,
            bool defaultValue = false) => Get(p, hash) switch
        {
            BinTreeBool b => b.Value,
            BinTreeBitBool bb => bb.Value,
            _ => defaultValue
        };

        internal static float? AsF32(BinTreeProperty p) => p switch
        {
            BinTreeF32 f => f.Value,
            BinTreeI8 i => i.Value,
            BinTreeU8 u => u.Value,
            BinTreeI16 i => i.Value,
            BinTreeU16 u => u.Value,
            BinTreeI32 i => i.Value,
            BinTreeU32 u => u.Value,
            BinTreeI64 i => i.Value,
            BinTreeU64 u => u.Value,
            _ => null
        };

        internal static long? AsInteger(BinTreeProperty p) => p switch
        {
            BinTreeI8 i => i.Value,
            BinTreeU8 u => u.Value,
            BinTreeI16 i => i.Value,
            BinTreeU16 u => u.Value,
            BinTreeI32 i => i.Value,
            BinTreeU32 u => u.Value,
            BinTreeI64 i => i.Value,
            BinTreeU64 u when u.Value <= long.MaxValue => (long)u.Value,
            _ => null
        };

        internal static Vector3? AsVec3(BinTreeProperty p) => p switch
        {
            BinTreeVector3 v => v.Value,
            BinTreeVector2 v => new Vector3(v.Value, 0f),
            BinTreeF32 f => new Vector3(f.Value),
            _ => null
        };

        internal static Vector2? AsVec2(BinTreeProperty p) => p switch
        {
            BinTreeVector2 v => v.Value,
            BinTreeVector3 v => new Vector2(v.Value.X, v.Value.Y),
            BinTreeF32 f => new Vector2(f.Value),
            _ => null
        };

        internal static Vector4? AsVec4(BinTreeProperty p) => p switch
        {
            BinTreeVector4 v => v.Value,
            BinTreeColor c => c.Value,
            BinTreeVector3 v => new Vector4(v.Value, 1f),
            _ => null
        };

        internal static uint? AsU32(BinTreeProperty p) => p switch
        {
            BinTreeU32 u => u.Value,
            BinTreeHash h => h.Value,
            BinTreeObjectLink ol => ol.Value,
            BinTreeString text => VfxParsingHash.Fnv1a(text.Value),
            _ => null
        };

        internal static int? GetI16(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
        {
            long? value = AsInteger(Get(p, hash));
            return value is >= short.MinValue and <= short.MaxValue ? (int)value.Value : null;
        }

        internal static int? GetI32(IReadOnlyDictionary<uint, BinTreeProperty> p, uint hash)
        {
            long? value = AsInteger(Get(p, hash));
            return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
        }

    }
}
