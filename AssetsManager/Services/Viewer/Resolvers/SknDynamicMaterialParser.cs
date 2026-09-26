using System;
using System.Collections.Generic;
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
            if (driver.ClassHash == Hash("NotMaterialDriver") &&
                driver.Properties.TryGetValue(Hash("mDriver"), out var inner))
                return new(GameMaterialBoolKind.Not, Children: new[] { ReadCondition(inner, depth + 1) });
            return new(GameMaterialBoolKind.Unsupported);
        }
    }
}
