using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Runtime;

/// <summary>League's 64-entry running integral for keyed IntegratedValue definitions.</summary>
internal static class VfxIntegratedValue
{
    private const int Samples = 64;
    private sealed class Tables
    {
        internal float[] Times;
        internal Vector4[] First, Second;
    }
    private static readonly ConditionalWeakTable<object, Tables> Cache = new();
    [ThreadStatic] private static Array _lastValues;
    [ThreadStatic] private static float[] _lastTimes;
    [ThreadStatic] private static int _lastOrder;
    [ThreadStatic] private static Vector4[] _lastTable;
    private struct RecentTable
    {
        internal Array Values;
        internal float[] Times;
        internal int Order;
        internal Vector4[] Data;
    }
    [ThreadStatic] private static RecentTable[] _recent;
    [ThreadStatic] private static int _recentNext;

    internal static float Sample(VfxCurveF curve, float age, int order = 1)
        => curve.Times is not { Length: > 0 } || curve.Values is not { Length: > 0 }
            ? curve.Constant
            : Read(curve.Times, curve.Values, age, order).X;

    internal static Vector2 Sample(VfxCurve2 curve, float age)
    {
        if (curve.Times is not { Length: > 0 } || curve.Values is not { Length: > 0 }) return curve.Constant;
        Vector4 value = Read(curve.Times, curve.Values, age, 1);
        return new Vector2(value.X, value.Y);
    }

    internal static Vector3 Sample(VfxCurve3 curve, float age, int order = 1)
    {
        if (curve.Times is not { Length: > 0 } || curve.Values is not { Length: > 0 }) return curve.Constant;
        Vector4 value = Read(curve.Times, curve.Values, age, order);
        return new Vector3(value.X, value.Y, value.Z);
    }

    private static Vector4 Read(float[] times, Array values, float age, int order)
    {
        Vector4[] table = _lastTable;
        if (!ReferenceEquals(_lastValues, values) || !ReferenceEquals(_lastTimes, times) || _lastOrder != order)
        {
            table = RecentOrBaked(times, values, order);
            // Each worker owns its shortcut; unrelated scenes cannot replace a reader's table.
            _lastValues = values;
            _lastTimes = times;
            _lastOrder = order;
            _lastTable = table;
        }
        float placed = Math.Clamp(age, 0f, 1f) * (Samples - 1);
        int under = Math.Min((int)placed, Samples - 2);
        return Vector4.Lerp(table[under], table[under + 1], placed - under);
    }

    private static Vector4[] RecentOrBaked(float[] times, Array values, int order)
    {
        var recent = _recent ??= new RecentTable[16];
        for (int index = 0; index < recent.Length; index++)
        {
            ref readonly var item = ref recent[index];
            if (ReferenceEquals(item.Values, values) && ReferenceEquals(item.Times, times) && item.Order == order)
                return item.Data;
        }

        Tables held = Cache.GetOrCreateValue(values);
        Vector4[] table;
        lock (held)
        {
            if (!ReferenceEquals(held.Times, times))
            {
                held.Times = times;
                held.First = held.Second = null;
            }
            table = order == 2 ? held.Second ??= Bake(times, values, true) : held.First ??= Bake(times, values, false);
        }
        // A particle alternates acceleration, rotation and UV curves, so one recent table alone misses that reuse.
        recent[_recentNext] = new RecentTable { Values = values, Times = times, Order = order, Data = table };
        _recentNext = (_recentNext + 1) % recent.Length;
        return table;
    }

    private static Vector4[] Bake(float[] times, Array values, bool twice)
    {
        Func<int, Vector4> at = values switch
        {
            float[] scalar => i => new Vector4(scalar[i], 0, 0, 0),
            Vector2[] pair => i => new Vector4(pair[i], 0, 0),
            Vector3[] triple => i => new Vector4(triple[i], 0),
            _ => throw new ArgumentException("Unsupported integrated curve value type.", nameof(values))
        };
        var table = new Vector4[Samples];
        int count = Math.Min(values.Length, times.Length);
        Vector4 sum = Vector4.Zero;
        const float cut = 1f / ((Samples - 1) * 8);
        for (int entry = 0; entry < Samples; entry++)
        {
            float upTo = entry / (float)(Samples - 1);
            if (!twice) table[entry] = Integral(times, count, at, upTo);
            else if (entry > 0)
            {
                float from = (entry - 1f) / (Samples - 1);
                for (int piece = 0; piece < 8; piece++)
                {
                    float a = from + piece * cut;
                    sum += (Integral(times, count, at, a) + 4f * Integral(times, count, at, a + cut / 2f)
                        + Integral(times, count, at, a + cut)) * (cut / 6f);
                }
                table[entry] = sum;
            }
        }
        return table;
    }

    private static Vector4 Integral(float[] times, int count, Func<int, Vector4> at, float upTo)
    {
        Vector4 sum = at(0) * MathF.Min(upTo, MathF.Max(times[0], 0f));
        for (int index = 0; index + 1 < count; index++)
        {
            float from = MathF.Max(times[index], 0f), to = MathF.Min(times[index + 1], upTo);
            float span = times[index + 1] - times[index];
            if (to <= from || span <= 0f) continue;
            Vector4 start = Vector4.Lerp(at(index), at(index + 1), (from - times[index]) / span);
            Vector4 end = Vector4.Lerp(at(index), at(index + 1), (to - times[index]) / span);
            sum += (start + end) * ((to - from) / 2f);
        }
        if (upTo > times[count - 1]) sum += at(count - 1) * (upTo - MathF.Max(times[count - 1], 0f));
        return sum;
    }
}
