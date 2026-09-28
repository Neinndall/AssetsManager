using System;
using System.Collections.Generic;
using System.Linq;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// One map a Map, MapSkin, or MapContainer object draws, paired with the skin that names it.
    /// </summary>
    public sealed record MapVariantData(string Skin, MapPath Map)
    {
        public string Label
        {
            get
            {
                if (Skin != null)
                    return Skin;

                return ContainerName;
            }
        }

        /// <summary>Container a skin draws; several skins may share one (Default, AprilFools2019... on Base_SRX).</summary>
        public string ContainerName
        {
            get
            {
                string path = Map?.Value ?? string.Empty;
                int slash = path.LastIndexOf('/');
                return slash >= 0 ? path[(slash + 1)..] : path;
            }
        }

        public string Detail => Skin != null ? ContainerName : null;

        /// <summary>
        /// Variant shown for a loaded map: the current choice when it already draws that map,
        /// otherwise the opening rule applied to the skins sharing its container.
        /// </summary>
        internal static MapVariantData ForMap(
            IReadOnlyList<MapVariantData> variants,
            MapPath map,
            MapVariantData current)
        {
            if (map == null || variants == null)
                return null;
            if (current != null && Draws(current, map))
                return current;
            return Opening(variants.Where(variant => Draws(variant, map)).ToArray());
        }

        internal static bool Draws(MapVariantData variant, MapPath map) =>
            variant?.Map != null && map != null &&
            (variant.Map.Equals(map) ||
             string.Equals(variant.Map.Value, map.Value, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// LTK opens the skin named Default first, case-insensitively, then falls back to authored order.
        /// </summary>
        internal static MapVariantData Opening(IReadOnlyList<MapVariantData> variants)
        {
            if (variants == null || variants.Count == 0)
                return null;

            return variants.FirstOrDefault(variant =>
                       variant?.Skin?.Equals("default", StringComparison.OrdinalIgnoreCase) == true) ??
                   variants[0];
        }
    }
}
