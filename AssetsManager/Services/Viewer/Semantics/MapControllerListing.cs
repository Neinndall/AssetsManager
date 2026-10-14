using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Semantics;

internal sealed record MapControllerRow(MapVisibilityControllerData Controller, int Depth,
    string Relation, int MeshCount, IReadOnlyList<string> Materials);

/// <summary>Controllers not covered by map-state controls, their dependencies and geometry uses.</summary>
internal static class MapControllerListing
{
    private static bool CoveredByMapState(MapVisibilityControllerData controller)
        => controller.TerrainMask != 0 || controller.StageMask != 0 || controller.Kind switch
        {
            MapVisibilityControllerKind.Terrain or MapVisibilityControllerKind.PrimaryFlags or
                MapVisibilityControllerKind.SecondaryFlags => controller.Mask != 0,
            MapVisibilityControllerKind.Mutator => true,
            _ => false
        };

    internal static IReadOnlyList<MapControllerRow> Build(MapSceneVisibility scene, MapGeometryData geometry)
    {
        if (scene == null || scene.Controllers.Count == 0) return Array.Empty<MapControllerRow>();
        var controllers = scene.Controllers.Values.ToArray();
        var roots = controllers.Where(controller => controller.Kind != MapVisibilityControllerKind.Child ||
            !(controller.Parents ?? Array.Empty<uint>()).Any(scene.Controllers.ContainsKey)).ToList();
        var reached = new HashSet<uint>();
        var rows = new List<MapControllerRow>();
        var dependents = controllers.Where(controller => controller.Kind == MapVisibilityControllerKind.Child)
            .SelectMany(controller => (controller.Parents ?? Array.Empty<uint>()).Distinct().Select(parent => (parent, controller)))
            .ToLookup(pair => pair.parent, pair => pair.controller);
        void MarkCovered(MapVisibilityControllerData controller)
        {
            if (!reached.Add(controller.PathHash)) return;
            foreach (var child in dependents[controller.PathHash]) MarkCovered(child);
        }
        foreach (var root in roots.Where(CoveredByMapState)) MarkCovered(root);
        var meshes = (geometry?.Meshes ?? Array.Empty<MapGeometryMeshData>())
            .Where(mesh => mesh.VisibilityControllerPathHash != 0).ToLookup(mesh => mesh.VisibilityControllerPathHash);

        void Append(MapVisibilityControllerData controller, int depth, string relation,
            HashSet<uint> above, List<MapControllerRow> rows)
        {
            if (!above.Add(controller.PathHash)) return;
            reached.Add(controller.PathHash);
            var materials = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            foreach (var mesh in meshes[controller.PathHash])
            {
                count++;
                int end = Math.Min(mesh.FirstSubmesh + mesh.SubmeshCount, geometry.Submeshes.Count);
                for (int at = Math.Max(0, mesh.FirstSubmesh); at < end; at++)
                {
                    int index = geometry.Submeshes[at].MaterialIndex;
                    if (index >= 0 && index < geometry.Materials.Count) materials.Add(geometry.Materials[index]);
                }
            }
            rows.Add(new(controller, depth, relation, count, materials.ToArray()));
            foreach (var child in dependents[controller.PathHash])
                Append(child, depth + 1, child.ParentMode == MapVisibilityParentMode.None ? "−" : "+", above, rows);
            above.Remove(controller.PathHash);
        }

        foreach (var root in roots.Where(controller => !CoveredByMapState(controller)))
            Append(root, 0, string.Empty, new HashSet<uint>(), rows);
        // Keep disconnected cycles inspectable without exposing descendants of the existing presets.
        foreach (var controller in controllers.Where(controller => !reached.Contains(controller.PathHash)))
            Append(controller, 0, string.Empty, new HashSet<uint>(), rows);
        return rows;
    }
}
