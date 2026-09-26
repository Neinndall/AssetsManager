using System;
using System.Collections.Generic;
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
                var evaluator = ReadValueDriver(driver, 0);
                if (evaluator != null) result.Add(new(name.Value, evaluator));
            }
            return result;
        }

        private static Func<int, Vector4?> ReadValueDriver(BinTreeProperty property, int depth)
        {
            if (depth >= 32 || property is not BinTreeStruct driver) return null;
            var fields = driver.Properties;
            if (driver.ClassHash == Hash("Float4LiteralMaterialDriver"))
            {
                Vector4 value = Vector(fields, "value", new(1, 0, 0, 0));
                return _ => value;
            }
            if (driver.ClassHash == Hash("LerpMaterialDriver") || driver.ClassHash == Hash("LerpVec4LogicDriver"))
            {
                bool vector = driver.ClassHash == Hash("LerpVec4LogicDriver");
                fields.TryGetValue(Hash(vector ? "BoolDriver" : "mBoolDriver"), out var conditionProperty);
                var condition = ReadCondition(conditionProperty, depth + 1);
                Vector4 on = vector ? Vector(fields, "OnValue", Vector4.One) :
                    new(Scalar(fields, "mOnValue", 1), 0, 0, 0);
                Vector4 off = vector ? Vector(fields, "OffValue", new(0, 0, 0, 1)) :
                    new(Scalar(fields, "mOffValue", 0), 0, 0, 0);
                // No gameplay buff transitions occur in the preview; use the settled endpoint.
                return gear => condition.Evaluate(gear) is bool active ? active ? on : off : null;
            }
            if (driver.ClassHash == Hash("SwitchMaterialDriver"))
            {
                var options = new List<(GameMaterialBoolCondition Condition, Func<int, Vector4?> Value)>();
                if (fields.TryGetValue(Hash("mElements"), out var elementsProperty) && elementsProperty is BinTreeContainer elements)
                    foreach (var entry in elements.Elements)
                        if (entry is BinTreeStruct element)
                        {
                            element.Properties.TryGetValue(Hash("mCondition"), out var condition);
                            element.Properties.TryGetValue(Hash("mValue"), out var value);
                            options.Add((ReadCondition(condition, depth + 1), ReadValueDriver(value, depth + 1)));
                        }
                fields.TryGetValue(Hash("mDefaultValue"), out var defaultProperty);
                var fallback = ReadValueDriver(defaultProperty, depth + 1);
                return gear =>
                {
                    foreach (var option in options)
                    {
                        bool? active = option.Condition.Evaluate(gear);
                        if (!active.HasValue) return null;
                        if (active.Value) return option.Value?.Invoke(gear);
                    }
                    return fallback?.Invoke(gear);
                };
            }
            return null;
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
                return new(GameMaterialBoolKind.Buff);
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
