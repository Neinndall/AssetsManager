using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Parsing;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Vfx.Resources
{
    /// <summary>Decodes authored VFX mesh geometry, owner skinning data and submesh ranges.</summary>
    internal static class VfxMeshDecoder
    {
        internal static VfxMeshData? DecodeMesh(
            string path,
            IReadOnlyList<uint> submeshesToDraw,
            IReadOnlyList<uint> submeshesToDrawAlways)
        {
            if (path.EndsWith(".skn", StringComparison.OrdinalIgnoreCase))
                return DecodeSkinnedMesh(path, submeshesToDraw, submeshesToDrawAlways);

            using var stream = File.OpenRead(path);
            var source = path.EndsWith(".sco", StringComparison.OrdinalIgnoreCase)
                ? LeagueToolkit.Core.Mesh.StaticMesh.ReadAscii(stream)
                : LeagueToolkit.Core.Mesh.StaticMesh.ReadBinary(stream);
            if (source.Faces.Count == 0) return null;

            // LTK groups interleaved SCB/SCO faces by material before exposing submesh ranges.
            // The first occurrence of a material fixes that range's order, and the authored
            // mSubmeshesToDraw / mSubmeshesToDrawAlways lists then filter those ranges.
            var groupedFaces = new List<(string Material, List<StaticMeshFace> Faces)>();
            var groupByMaterial = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (StaticMeshFace face in source.Faces)
            {
                string material = face.Material ?? string.Empty;
                if (!groupByMaterial.TryGetValue(material, out int groupIndex))
                {
                    groupIndex = groupedFaces.Count;
                    groupByMaterial.Add(material, groupIndex);
                    groupedFaces.Add((material, new List<StaticMeshFace>()));
                }
                groupedFaces[groupIndex].Faces.Add(face);
            }

            uint[] rangeHashes = groupedFaces
                .Select(group => Fnv1a.HashLower(group.Material.TrimEnd('\0')))
                .ToArray();
            bool[] selected = SelectMeshSubmeshRanges(
                rangeHashes,
                submeshesToDraw,
                submeshesToDrawAlways);
            int selectedFaceCount = 0;
            for (int groupIndex = 0; groupIndex < groupedFaces.Count; groupIndex++)
            {
                if (selected[groupIndex]) selectedFaceCount += groupedFaces[groupIndex].Faces.Count;
            }
            if (selectedFaceCount == 0) return null;

            int vertexCount = selectedFaceCount * 3;
            var positions = new float[vertexCount * 3];
            var uvs = new float[vertexCount * 2];
            var colors = new float[vertexCount * 4];
            var indices = new uint[vertexCount];
            int positionOffset = 0;
            int uvOffset = 0;
            int colorOffset = 0;

            for (int groupIndex = 0; groupIndex < groupedFaces.Count; groupIndex++)
            {
                if (!selected[groupIndex]) continue;
                foreach (StaticMeshFace face in groupedFaces[groupIndex].Faces)
                {
                    int[] vertexIds = { face.VertexId0, face.VertexId1, face.VertexId2 };
                    System.Numerics.Vector2[] faceUvs = { face.UV0, face.UV1, face.UV2 };
                    for (int corner = 0; corner < 3; corner++)
                    {
                        var position = source.Vertices[vertexIds[corner]];
                        positions[positionOffset++] = position.X;
                        positions[positionOffset++] = position.Y;
                        positions[positionOffset++] = position.Z;
                        uvs[uvOffset++] = faceUvs[corner].X;
                        uvs[uvOffset++] = faceUvs[corner].Y;
                        var color = source.HasVertexColors && vertexIds[corner] < source.VertexColors.Count
                            ? source.VertexColors[vertexIds[corner]]
                            : LeagueToolkit.Core.Primitives.Color.One;
                        colors[colorOffset++] = color.R;
                        colors[colorOffset++] = color.G;
                        colors[colorOffset++] = color.B;
                        colors[colorOffset++] = color.A;
                        indices[(positionOffset / 3) - 1] = (uint)((positionOffset / 3) - 1);
                    }
                }
            }

            return new VfxMeshData(positions, ComputeNormals(positions, indices), uvs, colors, indices);
        }

        private static VfxMeshData? DecodeSkinnedMesh(
            string path,
            IReadOnlyList<uint> submeshesToDraw,
            IReadOnlyList<uint> submeshesToDrawAlways)
        {
            using var mesh = LeagueToolkit.Core.Mesh.SkinnedMesh.ReadFromSimpleSkin(path);
            var sourcePositions = mesh.VerticesView
                .GetAccessor(LeagueToolkit.Core.Memory.VertexElement.POSITION.Name)
                .AsVector3Array();
            var sourceUvs = mesh.VerticesView
                .GetAccessor(LeagueToolkit.Core.Memory.VertexElement.TEXCOORD_0.Name)
                .AsVector2Array();

            var positions = new float[sourcePositions.Count * 3];
            var uvs = new float[sourceUvs.Count * 2];
            var colors = new float[sourcePositions.Count * 4];
            for (int index = 0; index < sourcePositions.Count; index++)
            {
                var position = sourcePositions[index];
                positions[index * 3] = position.X;
                positions[index * 3 + 1] = position.Y;
                positions[index * 3 + 2] = position.Z;
                colors[index * 4] = 1f;
                colors[index * 4 + 1] = 1f;
                colors[index * 4 + 2] = 1f;
                colors[index * 4 + 3] = 1f;
            }
            for (int index = 0; index < sourceUvs.Count; index++)
            {
                var uv = sourceUvs[index];
                uvs[index * 2] = uv.X;
                uvs[index * 2 + 1] = uv.Y;
            }

            uint[] rangeHashes = mesh.Ranges
                .Select(range => Fnv1a.HashLower(range.Material.TrimEnd('\0')))
                .ToArray();
            bool[] selected = SelectMeshSubmeshRanges(
                rangeHashes,
                submeshesToDraw,
                submeshesToDrawAlways);
            // LeagueToolkit C# exposes the SKN index buffer exactly as stored on disk: classic
            // files use absolute indices while NORMALIZED_INDICES files use range-local indices.
            // Convert both forms to the same absolute flat buffer exposed by current LTK preview.
            uint[] indices = FilterSkinnedMeshIndices(mesh, selected);
            if (indices.Length == 0) return null;
            float[] normals = ReadSkinnedNormals(mesh, positions, indices);
            return new VfxMeshData(positions, normals, uvs, colors, indices);
        }

        internal static bool[] SelectMeshSubmeshRanges(
            IReadOnlyList<uint> rangeHashes,
            IReadOnlyList<uint> submeshesToDraw,
            IReadOnlyList<uint> submeshesToDrawAlways)
        {
            rangeHashes ??= Array.Empty<uint>();
            var draw = (submeshesToDraw ?? Array.Empty<uint>()).ToHashSet();
            var always = (submeshesToDrawAlways ?? Array.Empty<uint>()).ToHashSet();
            bool narrowed = rangeHashes.Any(draw.Contains);
            if (!narrowed) return Enumerable.Repeat(true, rangeHashes.Count).ToArray();

            var selected = new bool[rangeHashes.Count];
            for (int index = 0; index < selected.Length; index++)
            {
                uint rangeHash = rangeHashes[index];
                selected[index] = draw.Contains(rangeHash) || always.Contains(rangeHash);
            }
            return selected;
        }

        internal static bool[] SelectAttachedSubmeshRanges(
            IReadOnlyList<uint> rangeHashes,
            IReadOnlyList<uint> submeshesToDraw,
            IReadOnlyList<uint> submeshesToDrawAlways,
            IReadOnlyList<uint> hiddenSubmeshes)
        {
            rangeHashes ??= Array.Empty<uint>();
            var draw = (submeshesToDraw ?? Array.Empty<uint>()).ToHashSet();
            var always = (submeshesToDrawAlways ?? Array.Empty<uint>()).ToHashSet();
            var hidden = (hiddenSubmeshes ?? Array.Empty<uint>()).ToHashSet();
            bool narrowed = rangeHashes.Any(draw.Contains);
            var selected = new bool[rangeHashes.Count];
            for (int index = 0; index < selected.Length; index++)
            {
                uint rangeHash = rangeHashes[index];
                selected[index] = ((!narrowed || draw.Contains(rangeHash)) && !hidden.Contains(rangeHash)) ||
                                  always.Contains(rangeHash);
            }
            return selected;
        }

        private static uint[] FilterSkinnedMeshIndices(SkinnedMesh mesh, IReadOnlyList<bool> selected)
        {
            var filtered = new List<uint>();
            if (selected is null || selected.Count != mesh.Ranges.Count)
                throw new InvalidDataException("SKN submesh selection does not match the authored range count.");

            for (int rangeIndex = 0; rangeIndex < mesh.Ranges.Count; rangeIndex++)
            {
                SkinnedMeshRange range = mesh.Ranges[rangeIndex];
                uint[] absoluteIndices = SkinnedMeshIndexSemantics.AbsoluteIndices(mesh, range);
                if (!selected[rangeIndex] || range.IndexCount == 0) continue;

                filtered.AddRange(absoluteIndices);
            }
            return filtered.ToArray();
        }

        private static uint[] BuildSkinnedMeshRanges(
            SkinnedMesh mesh,
            IReadOnlyList<uint> rangeHashes,
            out VfxMeshRangeData[] ranges)
        {
            if (rangeHashes is null || rangeHashes.Count != mesh.Ranges.Count)
                throw new InvalidDataException("SKN submesh hashes do not match the authored range count.");

            var flattened = new List<uint>();
            var builtRanges = new List<VfxMeshRangeData>();
            for (int rangeIndex = 0; rangeIndex < mesh.Ranges.Count; rangeIndex++)
            {
                SkinnedMeshRange range = mesh.Ranges[rangeIndex];
                uint[] absoluteIndices = SkinnedMeshIndexSemantics.AbsoluteIndices(mesh, range);
                int start = flattened.Count;
                flattened.AddRange(absoluteIndices);
                builtRanges.Add(new VfxMeshRangeData(rangeHashes[rangeIndex], start, range.IndexCount));
            }

            ranges = builtRanges.ToArray();
            return flattened.ToArray();
        }

        internal static VfxMeshData? DecodeAttachedSkinnedMesh(
            string path,
            float skinScale,
            string skeletonPath)
        {
            using var mesh = LeagueToolkit.Core.Mesh.SkinnedMesh.ReadFromSimpleSkin(path);
            uint[] rangeHashes = mesh.Ranges
                .Select(range => Fnv1a.HashLower(range.Material.TrimEnd('\0')))
                .ToArray();
            // Keep every valid owner range in the GPU index buffer. LTK evaluates
            // CharacterSkin.hidden again whenever clip visibility changes, so baking the
            // initial hidden set into this EBO would make an authored show event impossible.
            uint[] indices = BuildSkinnedMeshRanges(mesh, rangeHashes, out VfxMeshRangeData[] ranges);
            if (indices.Length == 0) return null;

            var sourcePositions = mesh.VerticesView
                .GetAccessor(LeagueToolkit.Core.Memory.VertexElement.POSITION.Name)
                .AsVector3Array();
            var sourceUvs = mesh.VerticesView
                .GetAccessor(LeagueToolkit.Core.Memory.VertexElement.TEXCOORD_0.Name)
                .AsVector2Array();
            var positions = new float[sourcePositions.Count * 3];
            var uvs = new float[sourceUvs.Count * 2];
            var colors = new float[sourcePositions.Count * 4];
            for (int index = 0; index < sourcePositions.Count; index++)
            {
                Vector3 position = sourcePositions[index];
                // Keep bind-pose vertices in authored skeleton space. The owner skinScale is
                // applied after skinning, matching the champion model's world transform.
                positions[index * 3] = position.X;
                positions[index * 3 + 1] = position.Y;
                positions[index * 3 + 2] = position.Z;
                // ATTACHED_VERTEX receives particleTint directly in LTK; owner vertex colours do
                // not tint the VFX material, so keep this geometry neutral.
                colors[index * 4] = colors[index * 4 + 1] = colors[index * 4 + 2] = colors[index * 4 + 3] = 1f;
            }
            for (int index = 0; index < sourceUvs.Count; index++)
            {
                Vector2 uv = sourceUvs[index];
                uvs[index * 2] = uv.X;
                uvs[index * 2 + 1] = uv.Y;
            }

            float[] normals = ReadSkinnedNormals(mesh, positions, indices);
            float[] boneIndices = null;
            float[] boneWeights = null;
            if (!string.IsNullOrWhiteSpace(skeletonPath))
                TryReadOwnerSkinning(mesh, skeletonPath, sourcePositions.Count, out boneIndices, out boneWeights);

            return new VfxMeshData(
                positions,
                normals,
                uvs,
                colors,
                indices,
                boneIndices,
                boneWeights,
                skinScale,
                ranges);
        }

        private static bool TryReadOwnerSkinning(
            SkinnedMesh mesh,
            string skeletonPath,
            int vertexCount,
            out float[] boneIndices,
            out float[] boneWeights)
        {
            boneIndices = null;
            boneWeights = null;
            try
            {
                if (!mesh.VerticesView.TryGetAccessor(VertexElement.BLEND_INDEX.Name, out VertexElementAccessor indexAccessor) ||
                    !mesh.VerticesView.TryGetAccessor(VertexElement.BLEND_WEIGHT.Name, out VertexElementAccessor weightAccessor))
                {
                    return false;
                }

                var sourceIndices = indexAccessor.AsXyzwU8Array().ToArray();
                var sourceWeights = weightAccessor.AsVector4Array().ToArray();
                if (sourceIndices.Length != vertexCount || sourceWeights.Length != vertexCount)
                    return false;

                RigResource skeleton;
                using (FileStream stream = File.OpenRead(skeletonPath))
                    skeleton = new RigResource(stream);
                if (skeleton.Joints.Count == 0 || skeleton.Joints.Count > 512 || skeleton.Influences.Count == 0)
                    return false;

                var directIndices = new float[vertexCount * 4];
                var weights = new float[vertexCount * 4];
                for (int vertex = 0; vertex < vertexCount; vertex++)
                {
                    var authoredIndices = sourceIndices[vertex];
                    Vector4 authoredWeights = sourceWeights[vertex];
                    int target = vertex * 4;
                    if (!TryResolveOwnerJoint(authoredIndices.x, authoredWeights.X, skeleton, out directIndices[target]) ||
                        !TryResolveOwnerJoint(authoredIndices.y, authoredWeights.Y, skeleton, out directIndices[target + 1]) ||
                        !TryResolveOwnerJoint(authoredIndices.z, authoredWeights.Z, skeleton, out directIndices[target + 2]) ||
                        !TryResolveOwnerJoint(authoredIndices.w, authoredWeights.W, skeleton, out directIndices[target + 3]))
                    {
                        return false;
                    }

                    weights[target] = authoredWeights.X;
                    weights[target + 1] = authoredWeights.Y;
                    weights[target + 2] = authoredWeights.Z;
                    weights[target + 3] = authoredWeights.W;
                }

                boneIndices = directIndices;
                boneWeights = weights;
                return true;
            }
            catch
            {
                boneIndices = null;
                boneWeights = null;
                return false;
            }
        }

        private static bool TryResolveOwnerJoint(byte influenceIndex, float weight, RigResource skeleton, out float jointIndex)
        {
            if (weight == 0f)
            {
                jointIndex = 0f;
                return true;
            }
            if (!float.IsFinite(weight) || weight < 0f || influenceIndex >= skeleton.Influences.Count)
            {
                jointIndex = 0f;
                return false;
            }

            short joint = skeleton.Influences[influenceIndex];
            if (joint < 0 || joint >= skeleton.Joints.Count || joint >= 512)
            {
                jointIndex = 0f;
                return false;
            }

            jointIndex = joint;
            return true;
        }

        private static float[] ReadSkinnedNormals(SkinnedMesh mesh, float[] positions, uint[] indices)
        {
            if (mesh.VerticesView.TryGetAccessor(VertexElement.NORMAL.Name, out VertexElementAccessor accessor))
            {
                var sourceNormals = accessor.AsVector3Array();
                var normals = new float[(positions.Length / 3) * 3];
                int count = Math.Min(sourceNormals.Count, normals.Length / 3);
                for (int index = 0; index < count; index++)
                {
                    Vector3 normal = sourceNormals[index];
                    if (normal.LengthSquared() > 1e-12f) normal = Vector3.Normalize(normal);
                    normals[index * 3] = normal.X;
                    normals[index * 3 + 1] = normal.Y;
                    normals[index * 3 + 2] = normal.Z;
                }
                return normals;
            }
            return ComputeNormals(positions, indices);
        }

        internal static float[] ComputeNormals(float[] positions, uint[] indices)
        {
            int vertexCount = positions?.Length / 3 ?? 0;
            var normals = new float[vertexCount * 3];
            if (vertexCount == 0 || indices is not { Length: >= 3 }) return normals;

            for (int index = 0; index + 2 < indices.Length; index += 3)
            {
                int i0 = checked((int)indices[index]);
                int i1 = checked((int)indices[index + 1]);
                int i2 = checked((int)indices[index + 2]);
                if ((uint)i0 >= vertexCount || (uint)i1 >= vertexCount || (uint)i2 >= vertexCount) continue;
                Vector3 p0 = ReadPosition(positions, i0);
                Vector3 p1 = ReadPosition(positions, i1);
                Vector3 p2 = ReadPosition(positions, i2);
                Vector3 normal = Vector3.Cross(p1 - p0, p2 - p0);
                if (normal.LengthSquared() <= 1e-12f) continue;
                AddNormal(normals, i0, normal);
                AddNormal(normals, i1, normal);
                AddNormal(normals, i2, normal);
            }

            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                Vector3 normal = ReadPosition(normals, vertex);
                if (normal.LengthSquared() <= 1e-12f) normal = Vector3.UnitZ;
                else normal = Vector3.Normalize(normal);
                normals[vertex * 3] = normal.X;
                normals[vertex * 3 + 1] = normal.Y;
                normals[vertex * 3 + 2] = normal.Z;
            }
            return normals;
        }

        private static Vector3 ReadPosition(float[] values, int index)
            => new(values[index * 3], values[index * 3 + 1], values[index * 3 + 2]);

        private static void AddNormal(float[] normals, int index, Vector3 normal)
        {
            normals[index * 3] += normal.X;
            normals[index * 3 + 1] += normal.Y;
            normals[index * 3 + 2] += normal.Z;
        }
    }
}