using System;
using System.IO;
using AssetsManager.Utils;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Identifies one map by the entry path stored in Riot map containers.
    /// </summary>
    public sealed class MapPath : IEquatable<MapPath>
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

        public static bool TryFromMapFile(
            string mapFilePath,
            out MapPath mapPath)
        {
            mapPath = null;
            if (string.IsNullOrWhiteSpace(mapFilePath) ||
                (!mapFilePath.EndsWith(GeometrySuffix, StringComparison.OrdinalIgnoreCase) &&
                 !mapFilePath.EndsWith(MaterialsSuffix, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            string normalized = PathUtils.NormalizeSeparators(Path.GetFullPath(mapFilePath));
            int dataIndex = normalized.LastIndexOf("/data/", StringComparison.OrdinalIgnoreCase);
            if (dataIndex < 0)
                return false;

            return TryFromEntryPath(normalized[(dataIndex + "/data/".Length)..], out mapPath);
        }

        public static bool TryFromGeometryFile(
            string geometryFilePath,
            out MapPath mapPath)
        {
            mapPath = null;
            return !string.IsNullOrWhiteSpace(geometryFilePath) &&
                   geometryFilePath.EndsWith(GeometrySuffix, StringComparison.OrdinalIgnoreCase) &&
                   TryFromMapFile(geometryFilePath, out mapPath);
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
