using System.Linq;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Parsing
{
    /// <summary>
    /// Reads the MapSunProperties component using the same container/component contract as current LTK MAIN.
    /// </summary>
    internal static class MapSunParser
    {
        internal const uint MapContainerClass = 0xdde8c114;
        internal const uint ComponentsField = 0x1bf51169;
        internal const uint SunPropertiesClass = 0x169a2f9c;
        internal const uint SunDirectionField = 0xe1907cf6;
        internal const uint SunColorField = 0x664a1f44;
        internal const uint SunIntensityField = 0x4620fe14;
        internal const uint SkyColorField = 0x0a65794d;
        internal const uint GroundColorField = 0x583befe1;
        internal const uint HorizonColorField = 0xfd3d43af;
        internal const uint SkyScaleField = 0xb39b0430;
        internal const uint LightMapColorScaleField = 0x986a4d5c;
        internal const uint FogEnabledField = 0x00849744;
        internal const uint FogColorField = 0x023b1fce;
        internal const uint FogAlternateColorField = 0x4896f2da;
        internal const uint FogStartEndField = 0x72a72173;
        internal const uint FogEmissiveRemapField = 0x27bdd641;

        private static readonly MapSunData Defaults = new(
            new Vector3(0f, 0.707f, 0.707f),
            Vector4.One,
            1f,
            new Vector4(0.705f, 0.88f, 1f, 1f),
            new Vector4(0.1f, 0.1f, 0.1f, 1f),
            new Vector4(0.4f, 0.4f, 0.4f, 1f),
            0.2f,
            1f,
            true,
            new Vector4(0.2f, 0.2f, 0.4f, 1f),
            new Vector4(0.1f, 0.1f, 0.2f, 1f),
            new Vector2(0f, -2000f),
            1.9f);

        internal static MapSunData Parse(BinTree materials, MapPath map)
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

            BinTreeStruct sun = components.Elements
                .OfType<BinTreeStruct>()
                .FirstOrDefault(component => component.ClassHash == SunPropertiesClass);
            if (sun == null)
                return null;

            return new MapSunData(
                ReadVector3(sun, SunDirectionField) ?? Defaults.Direction,
                ReadVector4(sun, SunColorField) ?? Defaults.Color,
                ReadFloat(sun, SunIntensityField) ?? Defaults.Intensity,
                ReadVector4(sun, SkyColorField) ?? Defaults.SkyColor,
                ReadVector4(sun, GroundColorField) ?? Defaults.GroundColor,
                ReadVector4(sun, HorizonColorField) ?? Defaults.HorizonColor,
                ReadFloat(sun, SkyScaleField) ?? Defaults.SkyScale,
                ReadFloat(sun, LightMapColorScaleField) ?? Defaults.LightMapColorScale,
                ReadBool(sun, FogEnabledField) ?? Defaults.FogEnabled,
                ReadVector4(sun, FogColorField) ?? Defaults.FogColor,
                ReadVector4(sun, FogAlternateColorField) ?? Defaults.FogAlternateColor,
                ReadVector2(sun, FogStartEndField) ?? Defaults.FogStartEnd,
                ReadFloat(sun, FogEmissiveRemapField) ?? Defaults.FogEmissiveRemap);
        }

        private static bool? ReadBool(BinTreeStruct holder, uint field) =>
            holder.Properties.TryGetValue(field, out BinTreeProperty property) && property is BinTreeBool value
                ? value.Value
                : null;

        private static Vector2? ReadVector2(BinTreeStruct holder, uint field) =>
            holder.Properties.TryGetValue(field, out BinTreeProperty property) && property is BinTreeVector2 value
                ? value.Value
                : null;

        private static Vector3? ReadVector3(BinTreeStruct holder, uint field) =>
            holder.Properties.TryGetValue(field, out BinTreeProperty property) && property is BinTreeVector3 value
                ? value.Value
                : null;

        private static Vector4? ReadVector4(BinTreeStruct holder, uint field) =>
            holder.Properties.TryGetValue(field, out BinTreeProperty property) && property is BinTreeVector4 value
                ? value.Value
                : null;

        private static float? ReadFloat(BinTreeStruct holder, uint field) =>
            holder.Properties.TryGetValue(field, out BinTreeProperty property) && property is BinTreeF32 value
                ? value.Value
                : null;
    }
}
