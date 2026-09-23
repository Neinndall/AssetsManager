using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Parsing
{
    /// <summary>
    /// Reads the unnamed MapGraphicsFeature/PostEffectOptions component using the same hashes,
    /// defaults and MapContainer fallback as current LTK Manager MAIN.
    /// </summary>
    internal static class MapPostEffectsParser
    {
        internal const uint MapContainerClass = 0xdde8c114;
        internal const uint ComponentsField = 0x1bf51169;
        internal const uint PostEffectsClass = 0x50db156b;
        internal const uint OptionsField = 0xef286ca5;

        internal const uint DepthFogEnabled = 0xd23c9e02;
        internal const uint DepthFogColor = 0x4d87c515;
        internal const uint DepthFogStart = 0xa6ce8c9a;
        internal const uint DepthFogEnd = 0x456d362f;
        internal const uint DepthFogMaxIntensity = 0x749b3695;
        internal const uint HeightFogEnabled = 0x32006fba;
        internal const uint HeightFogColor = 0xb0d4508d;
        internal const uint HeightFogStart = 0x8e731442;
        internal const uint HeightFogEnd = 0x2188a687;
        internal const uint HeightFogMaxIntensity = 0x7a8d755d;
        internal const uint DofEnabled = 0xe568bb76;
        internal const uint FocalDistance = 0x88ab79b5;
        internal const uint InFocusWidth = 0x8e1fed5e;
        internal const uint Coc = 0xeb8dc96c;

        internal static readonly MapPostEffectsData Defaults = new(
            new MapFogData(false, new Vector4(0f, 0f, 0f, 1f), 5000f, 8000f, 1f),
            new MapFogData(false, new Vector4(0f, 0f, 0f, 1f), 300f, -100f, 1f),
            new MapDepthOfFieldData(false, 2000f, 800f, 10f));

        internal static MapPostEffectsData Parse(BinTree materials, MapPath map)
        {
            BinTreeStruct component = FindComponent(materials, map, PostEffectsClass);
            if (component == null)
                return null;
            if (!component.Properties.TryGetValue(OptionsField, out BinTreeProperty optionsProperty) ||
                optionsProperty is not BinTreeStruct options)
            {
                return Defaults;
            }

            return new MapPostEffectsData(
                ReadFog(
                    options,
                    Defaults.DepthFog,
                    DepthFogEnabled,
                    DepthFogColor,
                    DepthFogStart,
                    DepthFogEnd,
                    DepthFogMaxIntensity),
                ReadFog(
                    options,
                    Defaults.HeightFog,
                    HeightFogEnabled,
                    HeightFogColor,
                    HeightFogStart,
                    HeightFogEnd,
                    HeightFogMaxIntensity),
                new MapDepthOfFieldData(
                    ReadBool(options, DofEnabled) ?? Defaults.DepthOfField.Enabled,
                    ReadFloat(options, FocalDistance) ?? Defaults.DepthOfField.FocalDistance,
                    ReadFloat(options, InFocusWidth) ?? Defaults.DepthOfField.InFocusWidth,
                    ReadFloat(options, Coc) ?? Defaults.DepthOfField.Coc));
        }

        private static MapFogData ReadFog(
            BinTreeStruct options,
            MapFogData defaults,
            uint enabled,
            uint color,
            uint start,
            uint end,
            uint maxIntensity) =>
            new(
                ReadBool(options, enabled) ?? defaults.Enabled,
                ReadVector4(options, color) ?? defaults.Color,
                ReadFloat(options, start) ?? defaults.Start,
                ReadFloat(options, end) ?? defaults.End,
                ReadFloat(options, maxIntensity) ?? defaults.MaxIntensity);

        internal static BinTreeStruct FindComponent(BinTree materials, MapPath map, uint classHash)
        {
            if (materials?.Objects == null || materials.Objects.Count == 0 || map == null)
                return null;

            uint mapHash = Fnv1a.HashLower(map.Value);
            BinTreeObject container = materials.Objects.TryGetValue(mapHash, out BinTreeObject exact) &&
                                      exact.ClassHash == MapContainerClass
                ? exact
                : materials.Objects.Values.FirstOrDefault(entry => entry.ClassHash == MapContainerClass);
            if (container == null ||
                !container.Properties.TryGetValue(ComponentsField, out BinTreeProperty componentsProperty) ||
                componentsProperty is not BinTreeContainer components)
            {
                return null;
            }

            return components.Elements
                .OfType<BinTreeStruct>()
                .FirstOrDefault(component => component.ClassHash == classHash);
        }

        internal static bool? ReadBool(BinTreeStruct holder, uint field)
        {
            if (holder == null || !holder.Properties.TryGetValue(field, out BinTreeProperty property))
                return null;
            return property switch
            {
                BinTreeBool value => value.Value,
                BinTreeBitBool value => value.Value,
                BinTreeU8 value => value.Value != 0,
                _ => null
            };
        }

        internal static float? ReadFloat(BinTreeStruct holder, uint field) =>
            holder != null &&
            holder.Properties.TryGetValue(field, out BinTreeProperty property) &&
            property is BinTreeF32 value
                ? value.Value
                : null;

        internal static Vector4? ReadVector4(BinTreeStruct holder, uint field) =>
            holder != null &&
            holder.Properties.TryGetValue(field, out BinTreeProperty property) &&
            property is BinTreeVector4 value
                ? value.Value
                : null;
    }
}
