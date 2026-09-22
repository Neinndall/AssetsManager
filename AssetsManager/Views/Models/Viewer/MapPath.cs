using System;
using System.IO;
using AssetsManager.Utils;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Identifies one map by the entry path stored in Riot map containers.
    /// </summary>
    internal sealed class MapPath : IEquatable<MapPath>
    {
        private const string DataPrefix = "data/";
        private const string GeometrySuffix = ".mapgeo";
        private const string MaterialsSuffix = ".materials.bin";

        public string Value { get; }
        public string GeometryVirtualPath => BuildFilePath(GeometrySuffix);
        public string MaterialsVirtualPath => BuildFilePath(MaterialsSuffix);

        private MapPath(string value)
        {
            Value = value;
        }

        public static MapPath FromEntryPath(string entryPath)
        {
            if (!TryFromEntryPath(entryPath, out MapPath mapPath))
                throw new ArgumentException("Map entry path is invalid.", nameof(entryPath));

            return mapPath;
        }

        public static bool TryFromEntryPath(string entryPath, out MapPath mapPath)
        {
            mapPath = null;
            if (string.IsNullOrWhiteSpace(entryPath))
                return false;

            string normalized = PathUtils.NormalizeSeparators(entryPath.Trim()).Trim('/');
            if (normalized.StartsWith(DataPrefix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized[DataPrefix.Length..];

            if (normalized.EndsWith(MaterialsSuffix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized[..^MaterialsSuffix.Length];
            else if (normalized.EndsWith(GeometrySuffix, StringComparison.OrdinalIgnoreCase))
                normalized = normalized[..^GeometrySuffix.Length];

            if (string.IsNullOrWhiteSpace(normalized) || ContainsTraversal(normalized))
                return false;

            mapPath = new MapPath(normalized);
            return true;
        }

        public static bool TryFromGeometryFile(
            string geometryFilePath,
            string projectRoot,
            out MapPath mapPath)
        {
            mapPath = null;
            if (string.IsNullOrWhiteSpace(geometryFilePath) ||
                !geometryFilePath.EndsWith(GeometrySuffix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string fullPath = Path.GetFullPath(geometryFilePath);
            string normalized = PathUtils.NormalizeSeparators(fullPath);
            int dataIndex = normalized.LastIndexOf("/data/", StringComparison.OrdinalIgnoreCase);
            if (dataIndex >= 0)
                return TryFromEntryPath(normalized[(dataIndex + "/data/".Length)..], out mapPath);

            if (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
            {
                string root = Path.GetFullPath(projectRoot);
                string relative = Path.GetRelativePath(root, fullPath);
                if (!relative.StartsWith("..", StringComparison.Ordinal) &&
                    !Path.IsPathRooted(relative) &&
                    TryFromEntryPath(relative, out mapPath))
                {
                    return true;
                }
            }

            int mapsIndex = normalized.LastIndexOf("/maps/", StringComparison.OrdinalIgnoreCase);
            return mapsIndex >= 0 &&
                   TryFromEntryPath(normalized[(mapsIndex + 1)..], out mapPath);
        }

        public bool Equals(MapPath other) =>
            other != null && string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object obj) => Equals(obj as MapPath);

        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

        public override string ToString() => Value;

        private string BuildFilePath(string suffix) =>
            $"{DataPrefix}{Value.ToLowerInvariant()}{suffix}";

        private static bool ContainsTraversal(string path)
        {
            foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                if (segment is "." or "..")
                    return true;

            return false;
        }
    }
}
