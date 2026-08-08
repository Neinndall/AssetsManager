using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Hashing;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Resolvers
{
    internal static class MapGeometryLightingResolver
    {
        private static readonly uint MapContainerHash = Fnv1a.HashLower("MapContainer");
        private static readonly uint MapSunPropertiesHash = Fnv1a.HashLower("MapSunProperties");
        private static readonly uint ComponentsHash = Fnv1a.HashLower("components");
        private static readonly uint SunDirectionHash = Fnv1a.HashLower("sunDirection");
        private static readonly uint SunColorHash = Fnv1a.HashLower("sunColor");
        private static readonly uint SunIntensityScaleHash = Fnv1a.HashLower("SunIntensityScale");
        private static readonly uint SkyLightColorHash = Fnv1a.HashLower("skyLightColor");
        private static readonly uint SkyLightScaleHash = Fnv1a.HashLower("skyLightScale");
        private static readonly uint LightMapColorScaleHash = Fnv1a.HashLower("lightMapColorScale");

        public static MapLightingProfile Resolve(BinTree materials)
        {
            if (materials == null)
                return null;

            BinTreeObject mapContainer = materials.Objects.Values.FirstOrDefault(
                x => x.ClassHash == MapContainerHash &&
                     x.Properties.ContainsKey(ComponentsHash));

            if (mapContainer?.Properties[ComponentsHash] is BinTreeContainer components)
            {
                foreach (BinTreeProperty component in components.Elements)
                {
                    if (component is BinTreeStruct sun && sun.ClassHash == MapSunPropertiesHash)
                        return CreateProfile(sun.Properties);
                }
            }

            BinTreeObject standaloneSun = materials.Objects.Values.FirstOrDefault(
                x => x.ClassHash == MapSunPropertiesHash);
            return standaloneSun == null ? null : CreateProfile(standaloneSun.Properties);
        }

        public static MapGeometryLightmapData ResolveLightmap(EnvironmentAssetMesh mesh)
        {
            EnvironmentAssetChannel bakedLight = mesh.BakedLight;
            string texturePath = PathUtils.ToVirtualPath(bakedLight.Texture);
            if (string.IsNullOrEmpty(texturePath) ||
                !mesh.VerticesView.TryGetAccessor(ElementName.Texcoord7, out var accessor))
            {
                return null;
            }

            float[] coordinates;
            if (accessor.Element.Format == ElementFormat.XY_Packed1616)
            {
                var packed = accessor.AsXyF16Array();
                coordinates = new float[packed.Count * 2];
                for (int i = 0; i < packed.Count; i++)
                {
                    var uv = packed[i];
                    WriteLightmapUv(coordinates, i, (float)uv.Item1, (float)uv.Item2, bakedLight.Scale, bakedLight.Bias);
                }
            }
            else if (accessor.Element.Format == ElementFormat.XY_Float32)
            {
                var values = accessor.AsVector2Array();
                coordinates = new float[values.Count * 2];
                for (int i = 0; i < values.Count; i++)
                    WriteLightmapUv(coordinates, i, values[i].X, values[i].Y, bakedLight.Scale, bakedLight.Bias);
            }
            else
            {
                return null;
            }

            return new MapGeometryLightmapData(
                texturePath,
                coordinates);
        }

        private static void WriteLightmapUv(
            float[] coordinates,
            int index,
            float x,
            float y,
            Vector2 scale,
            Vector2 bias)
        {
            int offset = index * 2;
            coordinates[offset] = x * scale.X + bias.X;
            coordinates[offset + 1] = y * scale.Y + bias.Y;
        }

        private static MapLightingProfile CreateProfile(
            IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            Vector3 sunDirection = ReadVector3(properties, SunDirectionHash, new(0f, 0.707f, 0.707f));
            sunDirection.Z = -sunDirection.Z;
            if (sunDirection.LengthSquared() <= 1e-6f)
                sunDirection = Vector3.UnitY;
            else
                sunDirection = Vector3.Normalize(sunDirection);

            Vector4 sunColor = ReadVector4(properties, SunColorHash, Vector4.One);
            // The viewer shader uses normalized light colors, unlike Toolkit's glTF lux conversion.
            float sunIntensity = MathF.Max(ReadFloat(properties, SunIntensityScaleHash, 1f), 0f);
            Vector3 skyColor = ReadVector4(properties, SkyLightColorHash, new(0.705f, 0.88f, 1f, 1f)).AsVector3();
            float skyScale = MathF.Max(ReadFloat(properties, SkyLightScaleHash, 0.2f), 0f);
            float lightMapScale = MathF.Max(ReadFloat(properties, LightMapColorScaleHash, 1f), 0f);

            return new MapLightingProfile(
                sunDirection,
                ClampColor(sunColor.AsVector3() * sunIntensity),
                ClampColor(skyColor * skyScale),
                lightMapScale);
        }

        private static Vector3 ReadVector3(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint nameHash,
            Vector3 fallback) =>
            properties.TryGetValue(nameHash, out BinTreeProperty property) &&
            property is BinTreeVector3 value
                ? value.Value
                : fallback;

        private static Vector4 ReadVector4(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint nameHash,
            Vector4 fallback) =>
            properties.TryGetValue(nameHash, out BinTreeProperty property) &&
            property is BinTreeVector4 value
                ? value.Value
                : fallback;

        private static float ReadFloat(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint nameHash,
            float fallback) =>
            properties.TryGetValue(nameHash, out BinTreeProperty property) &&
            property is BinTreeF32 value
                ? value.Value
                : fallback;

        private static Vector3 ClampColor(Vector3 value) => new(
            Math.Clamp(value.X, 0f, 1f),
            Math.Clamp(value.Y, 0f, 1f),
            Math.Clamp(value.Z, 0f, 1f));

        private static Vector3 AsVector3(this Vector4 value) => new(value.X, value.Y, value.Z);
    }

    internal sealed record MapGeometryLightmapData(
        string TexturePath,
        float[] Coordinates)
    {
        public float[] SliceCoordinates(int start, int count)
        {
            var result = new float[count * 2];
            Array.Copy(Coordinates, start * 2, result, 0, result.Length);
            return result;
        }
    }
}
