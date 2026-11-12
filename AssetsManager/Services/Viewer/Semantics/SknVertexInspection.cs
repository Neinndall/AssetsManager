using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;

namespace AssetsManager.Services.Viewer.Semantics;

internal sealed record SknVertexInspectionPart(string Name, Vector3[] Positions, Vector3[] Normals);

/// <summary>Selects authored SKN vertices for the diff's attribute inspection overlay.</summary>
internal static class SknVertexInspection
{
    internal static IReadOnlyList<SknVertexInspectionPart> Build(SkinnedMesh mesh,
        IReadOnlyDictionary<string, SknVertexPartDiff> differences, string attribute, bool changedOnly)
    {
        var result = new List<SknVertexInspectionPart>();
        if (mesh == null)
            return result;

        var positions = mesh.VerticesView.GetAccessor(ElementName.Position).AsVector3Array();
        bool hasNormals = mesh.VerticesView.TryGetAccessor(ElementName.Normal, out var normalAccessor);
        var normals = hasNormals ? normalAccessor.AsVector3Array() : default;
        foreach (var group in mesh.Ranges.GroupBy(range => range.Material.TrimEnd('\0'), StringComparer.OrdinalIgnoreCase))
        {
            differences.TryGetValue(group.Key, out var diff);
            // An entirely added/removed part has position changes, not changed normals or UV on a matched part.
            if (changedOnly && diff == null && attribute is not ("All attributes" or "Positions"))
                continue;

            var changed = diff?.PositionsFor(attribute);
            var selectedPositions = new List<Vector3>();
            var selectedNormals = new List<Vector3>();
            foreach (var range in group)
            {
                for (int index = range.StartVertex; index < range.StartVertex + range.VertexCount; index++)
                {
                    Vector3 point = positions[index];
                    if (!float.IsFinite(point.X) || !float.IsFinite(point.Y) || !float.IsFinite(point.Z) ||
                        changedOnly && diff != null && !changed.Contains(point))
                        continue;

                    selectedPositions.Add(point);
                    Vector3 normal = hasNormals ? normals[index] : Vector3.Zero;
                    selectedNormals.Add(float.IsFinite(normal.LengthSquared()) ? normal : Vector3.Zero);
                }
            }
            if (selectedPositions.Count > 0)
                result.Add(new(group.Key, selectedPositions.ToArray(), selectedNormals.ToArray()));
        }
        return result;
    }
}
