using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;

namespace AssetsManager.Services.Viewer.Parsing;

internal sealed record SknAttributeChange(string Name, int OldOnly, int NewOnly)
{
    internal string Summary => OldOnly == NewOnly
        ? $"{Name}: {NewOnly:N0}"
        : $"{Name}: {OldOnly:N0} OLD / {NewOnly:N0} NEW";
}

internal sealed record SknVertexPartDiff(
    int OldVertexCount,
    int NewVertexCount,
    IReadOnlyList<SknAttributeChange> Changes,
    IReadOnlySet<Vector3> ChangedPositions)
{
    internal bool HasChanges => Changes.Count > 0;
    internal string Summary => string.Join(" · ", Changes.Select(change => change.Summary));
}

/// <summary>Compares authored vertex values as multisets at equal positions, independent of vertex ordering.</summary>
internal static class SknVertexDiffAnalyzer
{
    private static readonly (string Label, ElementName Element)[] Attributes =
    {
        ("Normals", ElementName.Normal),
        ("UV", ElementName.Texcoord0),
        ("Colors", ElementName.PrimaryColor),
        ("Bone indices", ElementName.BlendIndex),
        ("Bone weights", ElementName.BlendWeight),
        ("Tangents", ElementName.Tangent)
    };

    internal static IReadOnlyDictionary<string, SknVertexPartDiff> Compare(SkinnedMesh oldMesh, SkinnedMesh newMesh)
    {
        var result = new Dictionary<string, SknVertexPartDiff>(StringComparer.OrdinalIgnoreCase);
        if (oldMesh == null || newMesh == null)
            return result;

        var oldPositions = oldMesh.VerticesView.GetAccessor(ElementName.Position).AsVector3Array();
        var newPositions = newMesh.VerticesView.GetAccessor(ElementName.Position).AsVector3Array();
        var oldRanges = oldMesh.Ranges.GroupBy(range => range.Material.TrimEnd('\0'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in newMesh.Ranges.GroupBy(range => range.Material.TrimEnd('\0'), StringComparer.OrdinalIgnoreCase))
        {
            if (!oldRanges.TryGetValue(group.Key, out var previous))
                continue;

            var oldVertices = previous.SelectMany(range => Enumerable.Range(range.StartVertex, range.VertexCount)).ToArray();
            var newVertices = group.SelectMany(range => Enumerable.Range(range.StartVertex, range.VertexCount)).ToArray();
            var oldCounts = PositionCounts(oldVertices, index => oldPositions[index]);
            var newCounts = PositionCounts(newVertices, index => newPositions[index]);
            var affected = new HashSet<Vector3>();
            var changes = new List<SknAttributeChange>();
            int removed = 0, added = 0;
            foreach (Vector3 position in oldCounts.Keys.Union(newCounts.Keys))
            {
                int difference = newCounts.GetValueOrDefault(position) - oldCounts.GetValueOrDefault(position);
                if (difference == 0)
                    continue;

                removed += Math.Max(0, -difference);
                added += Math.Max(0, difference);
                affected.Add(position);
            }
            if (removed > 0 || added > 0)
                changes.Add(new("Positions", removed, added));

            var sharedPositions = oldCounts.Keys.Intersect(newCounts.Keys).ToHashSet();
            foreach (var attribute in Attributes)
            {
                oldMesh.VerticesView.TryGetAccessor(attribute.Element, out var oldAccessor);
                newMesh.VerticesView.TryGetAccessor(attribute.Element, out var newAccessor);
                CompareValues(attribute.Label,
                    Values(oldVertices, index => oldPositions[index], sharedPositions, oldAccessor),
                    Values(newVertices, index => newPositions[index], sharedPositions, newAccessor),
                    changes, affected);
            }

            // Separate attribute histograms can match while their combinations at a UV seam differ.
            var combinations = new List<SknAttributeChange>();
            CompareValues("Attribute combinations",
                CombinedValues(oldMesh, oldVertices, index => oldPositions[index], sharedPositions),
                CombinedValues(newMesh, newVertices, index => newPositions[index], sharedPositions),
                combinations, affected);
            if (changes.Count == 0)
                changes.AddRange(combinations);

            result[group.Key] = new(oldVertices.Length, newVertices.Length, changes, affected);
        }
        return result;
    }

    private static Dictionary<Vector3, int> PositionCounts(int[] indices, Func<int, Vector3> position)
    {
        var result = new Dictionary<Vector3, int>();
        foreach (int index in indices)
        {
            Vector3 key = position(index);
            result[key] = result.GetValueOrDefault(key) + 1;
        }
        return result;
    }

    private static Dictionary<(Vector3 Position, string Value), int> Values(
        int[] indices, Func<int, Vector3> position, IReadOnlySet<Vector3> shared, VertexElementAccessor accessor)
    {
        var result = new Dictionary<(Vector3, string), int>();
        foreach (int index in indices)
        {
            Vector3 point = position(index);
            if (!shared.Contains(point))
                continue;

            var key = (point, Value(accessor, index));
            result[key] = result.GetValueOrDefault(key) + 1;
        }
        return result;
    }

    private static string Value(VertexElementAccessor accessor, int index) =>
        accessor.ElementSize == 0 ? "absent" : Convert.ToHexString(accessor.DecodeAt(index));

    private static Dictionary<(Vector3 Position, string Value), int> CombinedValues(
        SkinnedMesh mesh, int[] indices, Func<int, Vector3> position, IReadOnlySet<Vector3> shared)
    {
        var accessors = Attributes.Select(attribute =>
            mesh.VerticesView.TryGetAccessor(attribute.Element, out var accessor) ? accessor : default).ToArray();
        var result = new Dictionary<(Vector3, string), int>();
        foreach (int index in indices)
        {
            Vector3 point = position(index);
            if (!shared.Contains(point))
                continue;

            var key = (point, string.Join("/", accessors.Select(accessor => Value(accessor, index))));
            result[key] = result.GetValueOrDefault(key) + 1;
        }
        return result;
    }

    private static void CompareValues(string label,
        Dictionary<(Vector3 Position, string Value), int> oldValues,
        Dictionary<(Vector3 Position, string Value), int> newValues,
        List<SknAttributeChange> changes, HashSet<Vector3> affected)
    {
        int removed = 0, added = 0;
        foreach (var key in oldValues.Keys.Union(newValues.Keys))
        {
            int difference = newValues.GetValueOrDefault(key) - oldValues.GetValueOrDefault(key);
            if (difference == 0)
                continue;

            removed += Math.Max(0, -difference);
            added += Math.Max(0, difference);
            affected.Add(key.Position);
        }
        if (removed > 0 || added > 0)
            changes.Add(new(label, removed, added));
    }
}
