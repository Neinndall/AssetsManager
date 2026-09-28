using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Semantics
{
    /// <summary>
    /// Evaluates map visibility like the engine: the primary mask must share a bit with the item
    /// mask and its IMapVisibilityController graph must answer true for the previewed game state.
    /// </summary>
    internal static class MapVisibilitySemantics
    {
        private const int MaxDepth = 32;

        internal static bool IsVisible(
            MapSceneVisibility visibility,
            MapVisibilityState state,
            byte mask,
            uint? controller)
        {
            if (state == null || (mask & state.Flags) == 0)
                return false;
            return controller is not uint hash || hash == 0 ||
                   Evaluate(visibility?.Controllers, hash, state, 0);
        }

        internal static bool IsControllerVisible(
            MapSceneVisibility visibility,
            MapVisibilityState state,
            uint controller) =>
            controller == 0 || Evaluate(visibility?.Controllers, controller, state, 0);

        private static bool Evaluate(
            IReadOnlyDictionary<uint, MapVisibilityControllerData> controllers,
            uint hash,
            MapVisibilityState state,
            int depth)
        {
            // A link to an object this document does not define behaves as no controller.
            if (controllers == null || depth > MaxDepth || !controllers.TryGetValue(hash, out MapVisibilityControllerData controller))
                return true;

            return controller.Kind switch
            {
                MapVisibilityControllerKind.Mutator => state.HasMutator(controller.MutatorName),
                MapVisibilityControllerKind.PrimaryFlags => (state.Flags & controller.Mask) != 0,
                MapVisibilityControllerKind.SecondaryFlags => (state.SecondaryFlags & controller.Mask) != 0,
                MapVisibilityControllerKind.Named => controller.DefaultVisible,
                MapVisibilityControllerKind.Child => EvaluateChild(controllers, controller, state, depth),
                _ => true
            };
        }

        private static bool EvaluateChild(
            IReadOnlyDictionary<uint, MapVisibilityControllerData> controllers,
            MapVisibilityControllerData controller,
            MapVisibilityState state,
            int depth)
        {
            IReadOnlyList<uint> parents = controller.Parents ?? Array.Empty<uint>();
            if (parents.Count == 0)
                return true;

            int visible = parents.Count(parent => Evaluate(controllers, parent, state, depth + 1));
            return controller.ParentMode switch
            {
                MapVisibilityParentMode.Any => visible > 0,
                MapVisibilityParentMode.NotAll => visible < parents.Count,
                MapVisibilityParentMode.None => visible == 0,
                _ => visible == parents.Count
            };
        }

        /// <summary>
        /// Opening state: the Map object's initial masks when declared, otherwise the MAPGEO heuristic
        /// shared with LTK so boards without definitions still open on a whole variant.
        /// </summary>
        internal static MapVisibilityState Opening(
            MapVisibilityDefinitions definitions,
            int geometryOpeningFlags)
        {
            int flags = definitions?.Primary is { IsEmpty: false } primary
                ? primary.InitialMask
                : geometryOpeningFlags;
            int secondary = definitions?.Secondary?.InitialMask ?? 1;
            return new MapVisibilityState(flags == 0 ? geometryOpeningFlags : flags, secondary);
        }

        /// <summary>
        /// A transformation keeps the domain's initial bits and adds its own; base pieces a
        /// transformation replaces are removed by their NOR controllers, not by the mask.
        /// </summary>
        internal static int TransformationFlags(MapVisibilityDomainData primary, int bitIndex)
        {
            int initial = primary?.InitialMask ?? 1;
            return bitIndex is < 0 or > 7 ? initial : initial | (1 << bitIndex);
        }

        /// <summary>
        /// The transformation bit a primary mask represents beyond the initial bits, 0 for the
        /// untransformed map, or -1 for a custom combination.
        /// </summary>
        internal static int TransformationBit(MapVisibilityDomainData primary, int flags)
        {
            int initial = primary?.InitialMask ?? 1;
            if (flags == initial)
                return 0;

            int extra = flags & ~initial;
            if ((flags & initial) != initial || extra == 0 || (extra & (extra - 1)) != 0)
                return -1;
            return System.Numerics.BitOperations.TrailingZeroCount(extra);
        }
    }
}
