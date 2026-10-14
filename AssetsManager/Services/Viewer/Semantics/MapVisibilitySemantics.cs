using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Semantics
{
    /// <summary>
    /// Controllers replace layer masks. Undeclared links fall back to layers for meshes only.
    /// </summary>
    internal static class MapVisibilitySemantics
    {
        // Weak keys release old scenes and temporary states; shared graph evaluation keeps cycles consistent.
        private static readonly ConditionalWeakTable<MapSceneVisibility, ControllerGraph> Graphs = new();

        private sealed class ControllerGraph
        {
            private readonly IReadOnlyDictionary<uint, MapVisibilityControllerData> _controllers;
            private readonly ConditionalWeakTable<MapVisibilityState, Dictionary<uint, bool>> _states = new();
            private readonly ConditionalWeakTable<MapVisibilityState, Dictionary<uint, bool>>.CreateValueCallback _compute;

            internal ControllerGraph(MapSceneVisibility scene)
            {
                _controllers = scene.Controllers;
                _compute = Compute;
            }

            internal bool Visible(MapVisibilityState state, uint hash)
                => _states.GetValue(state, _compute).TryGetValue(hash, out bool visible) && visible;

            private Dictionary<uint, bool> Compute(MapVisibilityState state)
            {
                var known = new Dictionary<uint, bool>();
                var open = new HashSet<uint>();
                foreach (uint hash in _controllers.Keys) Evaluate(_controllers, hash, state, open, known);
                return known;
            }
        }

        internal static bool IsVisible(
            MapSceneVisibility visibility,
            MapVisibilityState state,
            byte mask,
            uint? controller)
        {
            if (state == null) return false;
            return controller is not uint hash || hash == 0
                ? LayerVisible(mask, state.Flags)
                : IsControllerVisible(visibility, state, hash);
        }

        internal static bool IsMeshVisible(
            MapSceneVisibility visibility, MapVisibilityState state, byte mask, uint controller)
            => state != null && (controller == 0 || visibility?.Controllers.ContainsKey(controller) != true
                ? LayerVisible(mask, state.Flags)
                : IsControllerVisible(visibility, state, controller));

        internal static bool LayerVisible(byte mask, int flags) => mask == 0xff || (mask & flags) != 0;

        internal static bool IsControllerVisible(
            MapSceneVisibility visibility,
            MapVisibilityState state,
            uint controller) =>
            state != null && (controller == 0 || visibility != null &&
                Graphs.GetValue(visibility, static scene => new ControllerGraph(scene)).Visible(state, controller));

        private static bool Evaluate(
            IReadOnlyDictionary<uint, MapVisibilityControllerData> controllers,
            uint hash,
            MapVisibilityState state,
            HashSet<uint> open,
            Dictionary<uint, bool> known)
        {
            if (known.TryGetValue(hash, out bool cached)) return cached;
            if (controllers == null || !controllers.TryGetValue(hash, out MapVisibilityControllerData controller) || !open.Add(hash))
                return false;

            bool visible = state.ControllerOverrides.TryGetValue(hash, out bool replacement) ? replacement : controller.Kind switch
            {
                MapVisibilityControllerKind.Mutator => state.HasMutator(controller.MutatorName),
                MapVisibilityControllerKind.PrimaryFlags => (state.Flags & controller.Mask) != 0,
                MapVisibilityControllerKind.Terrain => controller.DefaultVisible || (state.Flags & controller.Mask) != 0,
                // Stage bits select an authored preview state only when the user explicitly changes it.
                MapVisibilityControllerKind.SecondaryFlags => state.HasSecondaryOverride
                    ? (state.SecondaryFlags & controller.Mask) != 0
                    : controller.DefaultVisible || (state.Flags & controller.TerrainMask) != 0,
                MapVisibilityControllerKind.Named => controller.DefaultVisible || (state.Flags & controller.TerrainMask) != 0,
                MapVisibilityControllerKind.Child => EvaluateChild(controllers, controller, state, open, known),
                _ => false
            };
            open.Remove(hash);
            known[hash] = visible;
            return visible;
        }

        private static bool EvaluateChild(
            IReadOnlyDictionary<uint, MapVisibilityControllerData> controllers,
            MapVisibilityControllerData controller,
            MapVisibilityState state,
            HashSet<uint> open,
            Dictionary<uint, bool> known)
        {
            IReadOnlyList<uint> parents = controller.Parents ?? Array.Empty<uint>();
            int visible = parents.Count(parent => Evaluate(controllers, parent, state, open, known));
            return controller.ParentMode switch
            {
                MapVisibilityParentMode.Any => visible > 0,
                MapVisibilityParentMode.One => visible == 1,
                MapVisibilityParentMode.None => visible == 0,
                MapVisibilityParentMode.All => visible == parents.Count,
                _ => false
            };
        }

        internal static MapVisibilityState WithControllerState(
            MapSceneVisibility visibility, MapVisibilityState state, uint hash, bool visible)
        {
            MapVisibilityState computed = state.WithControllerOverride(hash, null);
            return IsControllerVisible(visibility, computed, hash) == visible
                ? computed : computed.WithControllerOverride(hash, visible);
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
