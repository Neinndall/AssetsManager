using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Resolvers
{
    internal static class SknDynamicMaterialParser
    {
        private static uint Hash(string name) => Fnv1a.HashLower(name);

        internal static IReadOnlyList<GameMaterialTextureSwap> Read(
            IReadOnlyDictionary<uint, BinTreeProperty> properties, Func<ulong, string> resolvePath)
        {
            var result = new List<GameMaterialTextureSwap>();
            if (!properties.TryGetValue(Hash("dynamicMaterial"), out var dynamicProperty) ||
                dynamicProperty is not BinTreeStruct dynamicMaterial ||
                !dynamicMaterial.Properties.TryGetValue(Hash("textures"), out var textureProperty) ||
                textureProperty is not BinTreeContainer textures)
                return result;

            foreach (var entry in textures.Elements)
            {
                if (entry is not BinTreeEmbedded swap ||
                    !swap.Properties.TryGetValue(Hash("name"), out var nameProperty) ||
                    nameProperty is not BinTreeString name || string.IsNullOrWhiteSpace(name.Value) ||
                    (swap.Properties.TryGetValue(Hash("Enabled"), out var enabled) &&
                     enabled is BinTreeBool { Value: false }) ||
                    !swap.Properties.TryGetValue(Hash("options"), out var optionsProperty) ||
                    optionsProperty is not BinTreeContainer options)
                    continue;

                var parsed = new List<GameMaterialTextureSwapOption>();
                foreach (var item in options.Elements)
                {
                    if (item is not BinTreeEmbedded option ||
                        !option.Properties.TryGetValue(Hash("TextureName"), out var texture))
                        continue;
                    string path = texture switch
                    {
                        BinTreeString text => text.Value,
                        BinTreeWadChunkLink link when link.Value != 0 =>
                            resolvePath?.Invoke(link.Value) is string resolved && !string.IsNullOrWhiteSpace(resolved)
                                ? resolved : $"{link.Value:x16}",
                        _ => null
                    };
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    option.Properties.TryGetValue(Hash("driver"), out var driver);
                    parsed.Add(new GameMaterialTextureSwapOption(PathUtils.ToVirtualPath(path), ReadCondition(driver, 0)));
                }
                if (parsed.Count > 0) result.Add(new GameMaterialTextureSwap(name.Value, parsed));
            }
            return result;
        }

        internal static IReadOnlyList<GameMaterialDynamicParameter> ReadParameters(
            IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            var result = new List<GameMaterialDynamicParameter>();
            if (!properties.TryGetValue(Hash("dynamicMaterial"), out var property) ||
                property is not BinTreeStruct dynamicMaterial ||
                !dynamicMaterial.Properties.TryGetValue(Hash("parameters"), out var parameterProperty) ||
                parameterProperty is not BinTreeContainer parameters)
                return result;
            foreach (var entry in parameters.Elements)
            {
                if (entry is not BinTreeStruct parameter ||
                    !parameter.Properties.TryGetValue(Hash("name"), out var nameProperty) ||
                    nameProperty is not BinTreeString name || string.IsNullOrWhiteSpace(name.Value) ||
                    parameter.Properties.TryGetValue(Hash("Enabled"), out var enabled) && enabled is BinTreeBool { Value: false } ||
                    !parameter.Properties.TryGetValue(Hash("driver"), out var driver))
                    continue;
                var conditions = new List<GameMaterialBoolCondition>();
                var evaluator = ReadValueDriver(driver, 0, conditions);
                if (evaluator != null)
                    result.Add(new(name.Value, evaluator,
                        conditions.SelectMany(condition => condition.Buffs()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
            }
            return result;
        }

        // Every condition the driver reads lands in `conditions`, so the caller can list the buffs it depends on.
        private static Func<GameMaterialState, Vector4?> ReadValueDriver(
            BinTreeProperty property,
            int depth,
            ICollection<GameMaterialBoolCondition> conditions)
        {
            if (depth >= 32 || property is not BinTreeStruct driver) return null;
            var fields = driver.Properties;
            if (driver.ClassHash == Hash("Float4LiteralMaterialDriver"))
            {
                Vector4 value = Vector(fields, "value", new(1, 0, 0, 0));
                return _ => value;
            }
            if (driver.ClassHash == Hash("FloatLiteralMaterialDriver"))
            {
                // mValue defaults to 0: Aatrox Skin33's body rests at Bloom_Intensity 0, not its static 10.
                Vector4 value = new(Scalar(fields, "mValue", 0f));
                return _ => value;
            }
            if (driver.ClassHash == Hash("LerpMaterialDriver") || driver.ClassHash == Hash("LerpVec4LogicDriver"))
            {
                bool vector = driver.ClassHash == Hash("LerpVec4LogicDriver");
                fields.TryGetValue(Hash(vector ? "BoolDriver" : "mBoolDriver"), out var conditionProperty);
                var condition = ReadCondition(conditionProperty, depth + 1);
                conditions.Add(condition);
                // A float driver fills every component, so it can drive a colour (TintColor 0 hides a form).
                Vector4 on = vector ? Vector(fields, "OnValue", Vector4.One) :
                    new(Scalar(fields, "mOnValue", 1));
                Vector4 off = vector ? Vector(fields, "OffValue", new(0, 0, 0, 1)) :
                    new(Scalar(fields, "mOffValue", 0));
                // The preview switches states instantly; the authored turn-on/off times are not replayed.
                return state => condition.Evaluate(state) is bool active ? active ? on : off : null;
            }
            if (driver.ClassHash == Hash("SwitchMaterialDriver"))
            {
                var options = new List<(GameMaterialBoolCondition Condition, Func<GameMaterialState, Vector4?> Value)>();
                if (fields.TryGetValue(Hash("mElements"), out var elementsProperty) && elementsProperty is BinTreeContainer elements)
                    foreach (var entry in elements.Elements)
                        if (entry is BinTreeStruct element)
                        {
                            element.Properties.TryGetValue(Hash("mCondition"), out var condition);
                            element.Properties.TryGetValue(Hash("mValue"), out var value);
                            GameMaterialBoolCondition read = ReadCondition(condition, depth + 1);
                            conditions.Add(read);
                            options.Add((read, ReadValueDriver(value, depth + 1, conditions)));
                        }
                fields.TryGetValue(Hash("mDefaultValue"), out var defaultProperty);
                var fallback = ReadValueDriver(defaultProperty, depth + 1, conditions);
                return state =>
                {
                    foreach (var option in options)
                    {
                        bool? active = option.Condition.Evaluate(state);
                        if (!active.HasValue) return null;
                        if (active.Value) return option.Value?.Invoke(state);
                    }
                    return fallback?.Invoke(state);
                };
            }
            if (driver.ClassHash == Hash("MaxMaterialDriver") || driver.ClassHash == Hash("MinMaterialDriver"))
            {
                bool max = driver.ClassHash == Hash("MaxMaterialDriver");
                var children = new List<Func<GameMaterialState, Vector4?>>();
                if (fields.TryGetValue(Hash("mDrivers"), out var driversProperty) && driversProperty is BinTreeContainer drivers)
                    foreach (var child in drivers.Elements)
                        children.Add(ReadValueDriver(child, depth + 1, conditions));
                if (children.Count == 0 || children.Contains(null))
                    return null;
                return state =>
                {
                    Vector4? result = null;
                    foreach (var child in children)
                    {
                        if (child(state) is not Vector4 value) return null;
                        result = result is Vector4 current ? max ? Vector4.Max(current, value) : Vector4.Min(current, value) : value;
                    }
                    return result;
                };
            }
            if (driver.ClassHash == Hash("FloatGraphMaterialDriver"))
            {
                // The inner driver picks the point on the authored curve (a 0..1 progress for lerp drivers).
                fields.TryGetValue(Hash("driver"), out var innerProperty);
                var inner = ReadValueDriver(innerProperty, depth + 1, conditions);
                if (inner == null ||
                    !fields.TryGetValue(Hash("graph"), out var graphProperty) || graphProperty is not BinTreeStruct graph ||
                    !TryReadFloats(graph.Properties, "times", out float[] times) ||
                    !TryReadFloats(graph.Properties, "values", out float[] values) ||
                    times.Length == 0 || times.Length != values.Length)
                    return null;
                return state => inner(state) is Vector4 at ? new Vector4(SampleCurve(times, values, at.X)) : null;
            }
            return null;
        }

        private static bool TryReadFloats(IReadOnlyDictionary<uint, BinTreeProperty> fields, string name, out float[] result)
        {
            result = null;
            if (!fields.TryGetValue(Hash(name), out var property) || property is not BinTreeContainer container)
                return false;
            var values = new List<float>();
            foreach (var element in container.Elements)
            {
                if (element is not BinTreeF32 scalar) return false;
                values.Add(scalar.Value);
            }
            result = values.ToArray();
            return true;
        }

        /// <summary>Piecewise-linear curve through (times, values), held flat before the first and after the last key.</summary>
        internal static float SampleCurve(IReadOnlyList<float> times, IReadOnlyList<float> values, float at)
        {
            if (at <= times[0]) return values[0];
            for (int key = 1; key < times.Count; key++)
            {
                if (at > times[key]) continue;
                float span = times[key] - times[key - 1];
                float t = span > 0f ? (at - times[key - 1]) / span : 1f;
                return values[key - 1] + (values[key] - values[key - 1]) * t;
            }
            return values[^1];
        }

        private static Vector4 Vector(IReadOnlyDictionary<uint, BinTreeProperty> fields, string name, Vector4 fallback) =>
            fields.TryGetValue(Hash(name), out var value) && value is BinTreeVector4 vector ? vector.Value : fallback;

        private static float Scalar(IReadOnlyDictionary<uint, BinTreeProperty> fields, string name, float fallback) =>
            fields.TryGetValue(Hash(name), out var value) && value is BinTreeF32 scalar ? scalar.Value : fallback;

        private static GameMaterialBoolCondition ReadCondition(BinTreeProperty property, int depth)
        {
            if (depth >= 32 || property is not BinTreeStruct driver)
                return new(GameMaterialBoolKind.Unsupported);
            if (driver.ClassHash == Hash("HasGearDynamicMaterialBoolDriver"))
            {
                if (driver.Properties.TryGetValue(Hash("mGearIndex"), out var index) && index is not BinTreeU8)
                    return new(GameMaterialBoolKind.Unsupported);
                int gear = index is BinTreeU8 value ? value.Value : 0;
                return new(GameMaterialBoolKind.Gear, gear);
            }
            if (driver.ClassHash == Hash("HasBuffDynamicMaterialBoolDriver"))
                return new(GameMaterialBoolKind.Buff,
                    Name: driver.Properties.TryGetValue(Hash("mScriptName"), out var script) && script is BinTreeString name ? name.Value : null);
            if (driver.ClassHash == Hash("IsDeadDynamicMaterialBoolDriver"))
                return new(GameMaterialBoolKind.Dead);
            if (driver.ClassHash == Hash("IsAnimationPlayingDynamicMaterialBoolDriver"))
            {
                var animations = new List<uint>();
                if (driver.Properties.TryGetValue(Hash("mAnimationNames"), out var names) && names is BinTreeContainer clips)
                    foreach (var clip in clips.Elements)
                        if (clip is BinTreeHash hash)
                            animations.Add(hash.Value);
                return new(GameMaterialBoolKind.Animation, Animations: animations);
            }
            if (driver.ClassHash == Hash("AllTrueMaterialDriver") &&
                driver.Properties.TryGetValue(Hash("mDrivers"), out var children) && children is BinTreeContainer list)
            {
                var conditions = new List<GameMaterialBoolCondition>();
                foreach (var child in list.Elements) conditions.Add(ReadCondition(child, depth + 1));
                return new(GameMaterialBoolKind.All, Children: conditions);
            }
            if (driver.ClassHash == Hash("DelayedBoolMaterialDriver") &&
                driver.Properties.TryGetValue(Hash("mBoolDriver"), out var delayed))
                return ReadCondition(delayed, depth + 1);
            if (driver.ClassHash == Hash("NotMaterialDriver") &&
                driver.Properties.TryGetValue(Hash("mDriver"), out var inner))
                return new(GameMaterialBoolKind.Not, Children: new[] { ReadCondition(inner, depth + 1) });
            return new(GameMaterialBoolKind.Unsupported);
        }
    }
}
