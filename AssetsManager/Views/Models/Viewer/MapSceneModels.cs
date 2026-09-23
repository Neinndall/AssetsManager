using System;
using System.IO;
using LeagueToolkit.Hashing;

namespace AssetsManager.Views.Models.Viewer
{
    internal enum MapAssetOrigin
    {
        SelectedFile,
        ProjectFile,
        InstallationWad
    }

    internal sealed record MapSceneSource(
        MapPath Map,
        string SelectedMapFilePath,
        string ProjectRoot)
    {
        public string SelectedGeometryPath =>
            SelectedMapFilePath?.EndsWith(".mapgeo", StringComparison.OrdinalIgnoreCase) == true
                ? SelectedMapFilePath
                : null;

        public string SelectedMaterialsPath =>
            SelectedMapFilePath?.EndsWith(".materials.bin", StringComparison.OrdinalIgnoreCase) == true
                ? SelectedMapFilePath
                : null;

        public static MapSceneSource FromMapFile(string mapFilePath, string projectRoot)
        {
            if (!MapPath.TryFromMapFile(mapFilePath, out MapPath mapPath))
            {
                throw new InvalidDataException(
                    $"Could not derive a Riot MapPath from '{mapFilePath}'.");
            }

            return new MapSceneSource(
                mapPath,
                Path.GetFullPath(mapFilePath),
                string.IsNullOrWhiteSpace(projectRoot) ? null : Path.GetFullPath(projectRoot));
        }

        public static MapSceneSource FromGeometryFile(string geometryFilePath, string projectRoot)
        {
            if (!MapPath.TryFromGeometryFile(geometryFilePath, out _))
            {
                throw new InvalidDataException(
                    $"Could not derive a Riot MapPath from '{geometryFilePath}'.");
            }
            return FromMapFile(geometryFilePath, projectRoot);
        }
    }

    internal sealed record MapAssetReference(
        string VirtualPath,
        ulong PathHash)
    {
        public bool IsEmpty => string.IsNullOrWhiteSpace(VirtualPath) && PathHash == 0;
    }

    internal sealed record MapResolvedAsset(
        string VirtualPath,
        MapAssetOrigin Origin,
        string PhysicalPath,
        string WadPath,
        ulong WadPathHash)
    {
        public bool IsPhysicalFile => !string.IsNullOrWhiteSpace(PhysicalPath);

        public static MapResolvedAsset FromPhysical(
            string virtualPath,
            string physicalPath,
            MapAssetOrigin origin) =>
            new(
                virtualPath,
                origin,
                Path.GetFullPath(physicalPath),
                null,
                0);

        public static MapResolvedAsset FromWad(
            string virtualPath,
            string wadPath,
            ulong pathHash = 0) =>
            new(
                virtualPath,
                MapAssetOrigin.InstallationWad,
                null,
                Path.GetFullPath(wadPath),
                pathHash != 0 ? pathHash : XxHash64Ext.Hash(virtualPath));
    }

    internal sealed record MapSceneAssets(
        MapSceneSource Source,
        MapResolvedAsset Geometry,
        MapResolvedAsset Materials);
}
