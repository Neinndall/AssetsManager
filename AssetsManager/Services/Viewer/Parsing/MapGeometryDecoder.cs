using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Memory;

namespace AssetsManager.Services.Viewer.Parsing
{
    /// <summary>
    /// Decodes Riot MAPGEO into one flat world-space geometry set for the map renderer.
    /// </summary>
    internal sealed class MapGeometryDecoder
    {
        private static readonly Vector3 DefaultNormal = Vector3.UnitY;

        public MapGeometryData Decode(ReadOnlyMemory<byte> bytes)
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            return Decode(stream);
        }

        public MapGeometryData Decode(Stream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            using var asset = new EnvironmentAsset(stream);
            return Decode(asset);
        }

        internal static MapGeometryData Decode(EnvironmentAsset asset)
        {
            ArgumentNullException.ThrowIfNull(asset);

            int vertexCount = 0;
            int indexCount = 0;
            int submeshCount = 0;
            bool hasUv1 = false;
            foreach (EnvironmentAssetMesh mesh in asset.Meshes)
            {
                vertexCount = checked(vertexCount + mesh.VerticesView.VertexCount);
                foreach (EnvironmentAssetMeshPrimitive submesh in mesh.Submeshes)
                    indexCount = checked(indexCount + submesh.IndexCount);
                submeshCount = checked(submeshCount + mesh.Submeshes.Count);
                hasUv1 |= mesh.VerticesView.TryGetAccessor(ElementName.Texcoord7, out _);
            }

            var positions = new Vector3[vertexCount];
            var normals = new Vector3[vertexCount];
            var uv0 = new Vector2[vertexCount];
            Vector2[] uv1 = hasUv1 ? new Vector2[vertexCount] : null;
            var indices = new uint[indexCount];
            var meshes = new List<MapGeometryMeshData>(asset.Meshes.Count);
            var submeshes = new List<MapGeometrySubmeshData>(submeshCount);
            var materials = new List<string>();
            var materialIndices = new Dictionary<string, int>(StringComparer.Ordinal);

            int vertexBase = 0;
            int indexBase = 0;
            foreach (EnvironmentAssetMesh mesh in asset.Meshes)
            {
                VertexElementAccessor positionAccessor = GetRequiredAccessor(mesh, ElementName.Position);
                bool hasNormals = mesh.VerticesView.TryGetAccessor(ElementName.Normal, out VertexElementAccessor normalAccessor);
                bool hasUv0 = mesh.VerticesView.TryGetAccessor(ElementName.Texcoord0, out VertexElementAccessor uv0Accessor);
                bool meshHasUv1 = mesh.VerticesView.TryGetAccessor(ElementName.Texcoord7, out VertexElementAccessor uv1Accessor);

                Matrix4x4 transform = mesh.Transform;
                Matrix4x4 normalTransform = CreateNormalTransform(transform);
                Vector3 min = new(float.PositiveInfinity);
                Vector3 max = new(float.NegativeInfinity);
                int meshVertexCount = mesh.VerticesView.VertexCount;

                for (int vertex = 0; vertex < meshVertexCount; vertex++)
                {
                    Vector3 localPosition = ReadVector3(positionAccessor, vertex, required: true);
                    Vector3 worldPosition = Vector3.Transform(localPosition, transform);
                    positions[vertexBase + vertex] = worldPosition;
                    min = Vector3.Min(min, worldPosition);
                    max = Vector3.Max(max, worldPosition);

                    Vector3 authoredNormal = hasNormals
                        ? ReadVector3(normalAccessor, vertex, required: false)
                        : DefaultNormal;
                    normals[vertexBase + vertex] = UnitOrDefault(
                        Vector3.TransformNormal(authoredNormal, normalTransform));

                    uv0[vertexBase + vertex] = hasUv0
                        ? ReadVector2(uv0Accessor, vertex)
                        : Vector2.Zero;
                    if (uv1 != null)
                    {
                        uv1[vertexBase + vertex] = meshHasUv1
                            ? ReadVector2(uv1Accessor, vertex)
                            : Vector2.Zero;
                    }
                }

                int firstSubmesh = submeshes.Count;
                foreach (EnvironmentAssetMeshPrimitive submesh in mesh.Submeshes)
                {
                    ValidateSubmesh(mesh, submesh);
                    int startIndex = indexBase;
                    int end = checked(submesh.StartIndex + submesh.IndexCount);
                    for (int at = submesh.StartIndex; at < end; at++)
                    {
                        uint localIndex = mesh.Indices[at];
                        if (localIndex >= meshVertexCount)
                        {
                            throw new InvalidDataException(
                                $"MAPGEO submesh index {localIndex} exceeds mesh vertex count {meshVertexCount}.");
                        }

                        indices[indexBase++] = checked((uint)vertexBase + localIndex);
                    }

                    string material = CanonicalMaterialPath(
                        submesh.Material ?? EnvironmentAssetMeshPrimitive.MISSING_MATERIAL);
                    if (!materialIndices.TryGetValue(material, out int materialIndex))
                    {
                        materialIndex = materials.Count;
                        materialIndices.Add(material, materialIndex);
                        materials.Add(material);
                    }

                    submeshes.Add(new MapGeometrySubmeshData(
                        startIndex,
                        submesh.IndexCount,
                        materialIndex));
                }

                MapGeometryMeshFlags flags = MapGeometryMeshFlags.None;
                if (mesh.DisableBackfaceCulling)
                    flags |= MapGeometryMeshFlags.CullDisabled;
                if (mesh.RegionHash != 0)
                    flags |= MapGeometryMeshFlags.RegionAnchored;

                meshes.Add(new MapGeometryMeshData(
                    meshVertexCount == 0 ? Vector3.Zero : min,
                    meshVertexCount == 0 ? Vector3.Zero : max,
                    (byte)mesh.VisibilityFlags,
                    (byte)mesh.EnvironmentQualityFilter,
                    flags,
                    firstSubmesh,
                    submeshes.Count - firstSubmesh,
                    mesh.RenderFlags,
                    mesh.VisibilityControllerPathHash,
                    mesh.RegionHash,
                    MapGeometryLightChannelData.From(mesh.BakedLight),
                    MapGeometryLightChannelData.From(mesh.StationaryLight)));

                vertexBase = checked(vertexBase + meshVertexCount);
            }

            return new MapGeometryData(
                positions,
                normals,
                uv0,
                uv1,
                indices,
                meshes,
                submeshes,
                materials);
        }

        /// <summary>
        /// MAPGEO material fields are fixed-size strings in some asset versions and may retain
        /// trailing NUL padding. Material BIN lookup, texture binding and the renderer must all
        /// address the same canonical path, matching LTK's decoded map string table.
        /// </summary>
        internal static string CanonicalMaterialPath(string material)
            => (material ?? string.Empty).TrimEnd('\0');

        private static VertexElementAccessor GetRequiredAccessor(
            EnvironmentAssetMesh mesh,
            ElementName element)
        {
            if (mesh.VerticesView.TryGetAccessor(element, out VertexElementAccessor accessor))
                return accessor;

            throw new InvalidDataException($"MAPGEO mesh does not contain required vertex element '{element}'.");
        }

        private static void ValidateSubmesh(
            EnvironmentAssetMesh mesh,
            EnvironmentAssetMeshPrimitive submesh)
        {
            if (submesh.StartIndex < 0 || submesh.IndexCount < 0 ||
                submesh.StartIndex > mesh.Indices.Count - submesh.IndexCount)
            {
                throw new InvalidDataException("MAPGEO submesh index range exceeds its index buffer.");
            }
        }

        private static Matrix4x4 CreateNormalTransform(Matrix4x4 transform)
        {
            if (!Matrix4x4.Invert(transform, out Matrix4x4 inverse))
                return Matrix4x4.Identity;

            return Matrix4x4.Transpose(inverse);
        }

        private static Vector3 ReadVector3(
            VertexElementAccessor accessor,
            int index,
            bool required)
        {
            return accessor.Element.Format switch
            {
                ElementFormat.XYZ_Float32 => accessor.AsVector3Array()[index],
                ElementFormat.XYZ_Packed161616 => ReadHalfVector3(accessor, index),
                _ when !required => DefaultNormal,
                _ => throw new InvalidDataException(
                    $"Unsupported MAPGEO {accessor.Element.Name} format '{accessor.Element.Format}'.")
            };
        }

        private static Vector2 ReadVector2(VertexElementAccessor accessor, int index)
        {
            return accessor.Element.Format switch
            {
                ElementFormat.XY_Float32 => accessor.AsVector2Array()[index],
                ElementFormat.XY_Packed1616 => ReadHalfVector2(accessor, index),
                _ => Vector2.Zero
            };
        }

        private static Vector2 ReadHalfVector2(VertexElementAccessor accessor, int index)
        {
            (Half x, Half y) = accessor.AsXyF16Array()[index];
            return new Vector2((float)x, (float)y);
        }

        private static Vector3 ReadHalfVector3(VertexElementAccessor accessor, int index)
        {
            (Half x, Half y, Half z) = accessor.AsXyzF16Array()[index];
            return new Vector3((float)x, (float)y, (float)z);
        }

        private static Vector3 UnitOrDefault(Vector3 normal)
        {
            float lengthSquared = normal.LengthSquared();
            if (!float.IsFinite(normal.X) ||
                !float.IsFinite(normal.Y) ||
                !float.IsFinite(normal.Z) ||
                !float.IsFinite(lengthSquared) ||
                lengthSquared <= 0f)
            {
                return DefaultNormal;
            }

            return normal / MathF.Sqrt(lengthSquared);
        }
    }
}
