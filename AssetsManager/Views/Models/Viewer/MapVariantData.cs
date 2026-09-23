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

                string path = Map?.Value ?? string.Empty;
                int slash = path.LastIndexOf('/');
                return slash >= 0 ? path[(slash + 1)..] : path;
            }
        }

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
