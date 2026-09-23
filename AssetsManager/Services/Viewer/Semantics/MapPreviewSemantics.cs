using System;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Semantics
{
    internal readonly record struct MapSunPreviewOverride(
        Vector3 Direction,
        Vector4 Color,
        float Strength,
        Vector4 SkyColor,
        Vector4 GroundColor,
        float Ambient);

    internal readonly record struct MapSsaoPreviewOverride(
        bool Enabled,
        MapSsaoData Settings);

    /// <summary>
    /// Preview-only MAP overrides. Authored scene data remains immutable; these helpers mirror
    /// LTK's global preview stores and only compose an effective value for rendering.
    /// </summary>
    internal static class MapPreviewSemantics
    {
        internal static readonly MapSunData DefaultSun = new(
            Vector3.Normalize(new Vector3(-0.25f, 0.75f, -0.05f)),
            Vector4.One,
            0.8f,
            Vector4.One,
            Vector4.One,
            Vector4.One,
            1.2f,
            1f,
            false,
            Vector4.One,
            Vector4.One,
            Vector2.Zero,
            0f);

        internal static readonly MapPostEffectsData NoPostEffects = new(
            new MapFogData(false, new Vector4(0f, 0f, 0f, 1f), 5000f, 8000f, 1f),
            new MapFogData(false, new Vector4(0f, 0f, 0f, 1f), 300f, -100f, 1f),
            new MapDepthOfFieldData(false, 2000f, 800f, 10f));

        internal static readonly MapSsaoData DefaultSsaoSettings = new(
            0,
            25f,
            3f,
            1f,
            1f,
            0.5f,
            true);

        internal static MapSunPreviewOverride OwnSun(MapSunData authored)
        {
            MapSunData own = authored ?? DefaultSun;
            float direct = MathF.Max(own.Intensity, 0f);
            float ambient = MathF.Max(own.SkyScale, 0f);
            float total = direct + ambient;
            float strength = total > 0f ? direct / total : 0.5f;
            float ambientShare = total > 0f ? ambient / total : 0.5f;
            Vector3 direction = IsFinite(own.Direction) && own.Direction.LengthSquared() > 1e-12f
                ? Vector3.Normalize(own.Direction)
                : DefaultSun.Direction;
            return new MapSunPreviewOverride(
                direction,
                own.Color,
                strength,
                own.SkyColor,
                own.GroundColor,
                ambientShare);
        }

        internal static MapSunData EffectiveSun(
            MapSunData authored,
            MapSunPreviewOverride? previewOverride)
        {
            if (!previewOverride.HasValue)
                return authored;

            MapSunData own = authored ?? DefaultSun;
            MapSunPreviewOverride custom = previewOverride.Value;
            float total = MathF.Max(own.Intensity, 0f) + MathF.Max(own.SkyScale, 0f);
            return own with
            {
                Direction = NormalizeOrDefault(custom.Direction),
                Color = custom.Color,
                Intensity = MathF.Max(custom.Strength, 0f) * total,
                SkyColor = custom.SkyColor,
                GroundColor = custom.GroundColor,
                SkyScale = MathF.Max(custom.Ambient, 0f) * total
            };
        }

        internal static MapPostEffectsData OwnPostEffects(MapPostEffectsData authored) =>
            authored ?? NoPostEffects;

        internal static MapPostEffectsData EffectivePostEffects(
            MapPostEffectsData authored,
            MapPostEffectsData previewOverride,
            bool hasOverride) =>
            hasOverride ? previewOverride ?? NoPostEffects : authored;

        internal static MapSsaoPreviewOverride OwnSsao(MapSsaoData authored) =>
            new(authored != null, authored ?? DefaultSsaoSettings);

        internal static MapSsaoData EffectiveSsao(
            MapSsaoData authored,
            MapSsaoPreviewOverride? previewOverride) =>
            previewOverride.HasValue
                ? previewOverride.Value.Enabled ? previewOverride.Value.Settings : null
                : authored;

        internal static (float Azimuth, float Elevation) SunAngles(Vector3 direction)
        {
            Vector3 unit = NormalizeOrDefault(direction);
            float azimuth = RadiansToDegrees(MathF.Atan2(unit.X, unit.Z));
            float elevation = RadiansToDegrees(MathF.Asin(Math.Clamp(unit.Y, -1f, 1f)));
            return (azimuth, elevation);
        }

        internal static Vector3 SunDirection(float azimuth, float elevation)
        {
            float bearing = DegreesToRadians(azimuth);
            float height = DegreesToRadians(elevation);
            return new Vector3(
                MathF.Cos(height) * MathF.Sin(bearing),
                MathF.Sin(height),
                MathF.Cos(height) * MathF.Cos(bearing));
        }

        private static Vector3 NormalizeOrDefault(Vector3 direction) =>
            IsFinite(direction) && direction.LengthSquared() > 1e-12f
                ? Vector3.Normalize(direction)
                : DefaultSun.Direction;

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

        private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);
        private static float RadiansToDegrees(float radians) => radians * (180f / MathF.PI);
    }
}
