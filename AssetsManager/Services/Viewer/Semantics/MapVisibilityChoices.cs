using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Semantics
{
    /// <summary>One map state the user can pick: a primary transformation or a secondary-domain state.</summary>
    internal sealed record MapVisibilityChoice(string Label, int Flags);

    /// <summary>A geometry layer with the label its primary-domain flag gives it, if any.</summary>
    internal sealed record MapVisibilityLayerChoice(MapGeometryLayerData Layer, string Label);

    /// <summary>Every map-state choice one scene offers.</summary>
    internal sealed record MapVisibilityChoices(
        IReadOnlyList<MapVisibilityLayerChoice> Layers,
        IReadOnlyList<MapVisibilityChoice> Transformations,
        IReadOnlyList<MapVisibilityChoice> SecondaryStates,
        IReadOnlyList<string> Mutators)
    {
        internal static readonly MapVisibilityChoices Empty = new(
            Array.Empty<MapVisibilityLayerChoice>(),
            Array.Empty<MapVisibilityChoice>(),
            Array.Empty<MapVisibilityChoice>(),
            Array.Empty<string>());

        private static readonly IReadOnlyDictionary<string, string> MutatorLabels =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SR_Hall_Of_Legends"] = "Hall of Legends",
                ["MSITrophy"] = "MSI Trophy",
                ["MapObjectESportSponsorBanners"] = "Esports Banners"
            };

        /// <summary>
        /// Named transformations and secondary states come from the Map object's domains, mutators from
        /// the controller graph; without named domains the raw layer and mask bits stand in for them.
        /// </summary>
        internal static MapVisibilityChoices Of(MapSceneVisibility visibility, IEnumerable<MapGeometryLayerData> layers)
        {
            if (visibility == null)
                return Empty;

            MapVisibilityDomainData primary = visibility.Definitions.Primary;
            MapGeometryLayerData[] layerList = (layers ?? Array.Empty<MapGeometryLayerData>()).ToArray();
            var layerChoices = layerList
                .Select(layer => new MapVisibilityLayerChoice(layer, primary?.Find(layer.Index)?.Label))
                .ToArray();

            var transformations = new List<MapVisibilityChoice>();
            if (primary is { IsEmpty: false })
            {
                transformations.Add(new MapVisibilityChoice(primary.Find(0)?.PublicName ?? "Base", primary.InitialMask));
                foreach (MapVisibilityFlagData flag in primary.Flags)
                {
                    if ((primary.InitialMask & flag.Flag) != 0)
                        continue;
                    transformations.Add(new MapVisibilityChoice(
                        flag.Label,
                        MapVisibilitySemantics.TransformationFlags(primary, flag.BitIndex)));
                }
            }
            else
            {
                foreach (MapGeometryLayerData layer in layerList)
                    transformations.Add(new MapVisibilityChoice($"Layer {layer.Index + 1}", layer.Flag));
            }

            var secondaryStates = new List<MapVisibilityChoice>();
            MapVisibilityDomainData secondary = visibility.Definitions.Secondary;
            if (secondary is { IsEmpty: false })
            {
                foreach (MapVisibilityFlagData flag in secondary.Flags)
                    secondaryStates.Add(new MapVisibilityChoice(flag.Label, flag.Flag));
            }
            else
            {
                int mask = visibility.SecondaryMaskInUse;
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((mask & (1 << bit)) != 0)
                        secondaryStates.Add(new MapVisibilityChoice($"State {bit + 1}", 1 << bit));
                }
            }

            return new MapVisibilityChoices(layerChoices, transformations, secondaryStates, visibility.MutatorNames.ToArray());
        }

        /// <summary>The readable name of a mutator the map's controllers read.</summary>
        internal static string MutatorLabel(string name) =>
            MutatorLabels.TryGetValue(name ?? string.Empty, out string label) ? label : name;
    }
}
