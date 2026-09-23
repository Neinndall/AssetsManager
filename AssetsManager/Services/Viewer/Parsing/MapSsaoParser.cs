using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace AssetsManager.Services.Viewer.Parsing
{
    /// <summary>
    /// Reads MapSSAO/MapSSAORenderer/MapSSAOSettings using current LTK Manager MAIN defaults.
    /// </summary>
    internal static class MapSsaoParser
    {
        internal const uint MapSsaoClass = 0x1717869f;
        internal const uint RendererField = 0xce8f4190;
        internal const uint SettingsField = 0x68067b08;
        internal const uint SampleQualityField = 0x1fd670cc;
        internal const uint SampleRadiusField = 0x9a8c9615;
        internal const uint BiasField = 0xba467ec4;
        internal const uint PowerField = 0xf54f2346;
        internal const uint IntensityField = 0x8563e50a;
        internal const uint BufferScaleField = 0xe94ae473;
        internal const uint EdgeAwareBlurField = 0x6509d993;

        internal static readonly MapSsaoData Defaults = new(
            0u,
            25f,
            3f,
            1f,
            1f,
            0.5f,
            true);

        internal static MapSsaoData Parse(BinTree materials, MapPath map)
        {
            BinTreeStruct component = MapPostEffectsParser.FindComponent(materials, map, MapSsaoClass);
            if (component == null)
                return null;

            BinTreeStruct settings = null;
            if (component.Properties.TryGetValue(RendererField, out BinTreeProperty rendererProperty) &&
                rendererProperty is BinTreeStruct renderer &&
                renderer.Properties.TryGetValue(SettingsField, out BinTreeProperty settingsProperty))
            {
                settings = settingsProperty as BinTreeStruct;
            }

            if (settings == null)
                return Defaults;

            return new MapSsaoData(
                ReadUInt(settings, SampleQualityField) ?? Defaults.SampleQuality,
                MapPostEffectsParser.ReadFloat(settings, SampleRadiusField) ?? Defaults.SampleRadius,
                MapPostEffectsParser.ReadFloat(settings, BiasField) ?? Defaults.Bias,
                MapPostEffectsParser.ReadFloat(settings, PowerField) ?? Defaults.Power,
                MapPostEffectsParser.ReadFloat(settings, IntensityField) ?? Defaults.Intensity,
                MapPostEffectsParser.ReadFloat(settings, BufferScaleField) ?? Defaults.BufferScale,
                MapPostEffectsParser.ReadBool(settings, EdgeAwareBlurField) ?? Defaults.EdgeAwareBlur);
        }

        private static uint? ReadUInt(BinTreeStruct holder, uint field)
        {
            if (holder == null || !holder.Properties.TryGetValue(field, out BinTreeProperty property))
                return null;

            return property switch
            {
                BinTreeU8 value => value.Value,
                BinTreeU16 value => value.Value,
                BinTreeU32 value => value.Value,
                BinTreeU64 value when value.Value <= uint.MaxValue => (uint)value.Value,
                BinTreeI8 value when value.Value >= 0 => (uint)value.Value,
                BinTreeI16 value when value.Value >= 0 => (uint)value.Value,
                BinTreeI32 value when value.Value >= 0 => (uint)value.Value,
                BinTreeI64 value when value.Value >= 0 && value.Value <= uint.MaxValue => (uint)value.Value,
                _ => null
            };
        }
    }
}
