using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media.Media3D;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Mesh;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Immutable per-part skinning attributes prepared once for the OpenGL vertex stage.
    /// </summary>
    internal sealed class GpuSkinningData
    {
        internal const int MaxBones = 512;

        internal sealed class PartData
        {
            internal PartData(float[] boneIndices, float[] boneWeights)
            {
                BoneIndices = boneIndices;
                BoneWeights = boneWeights;
            }

            internal float[] BoneIndices { get; }
            internal float[] BoneWeights { get; }
            internal int VertexCount => BoneIndices.Length / 4;
        }

        private readonly Dictionary<ModelPart, PartData> _parts;

        private GpuSkinningData(Dictionary<ModelPart, PartData> parts)
        {
            _parts = parts;
        }

        internal bool TryGetPart(ModelPart part, out PartData data) =>
            _parts.TryGetValue(part, out data);

        internal static GpuSkinningData TryCreate(
            RigResource skeleton,
            SkinnedMesh skin,
            IList<ModelPart> modelParts,
            out string failureReason)
        {
            failureReason = null;
            if (skeleton == null || skin == null || modelParts == null ||
                skeleton.Joints.Count == 0 || skeleton.Joints.Count > MaxBones ||
                skeleton.Influences.Count == 0)
            {
                return Fail(out failureReason, "Skeleton or skin data is missing or outside GPU limits.");
            }

            try
            {
                var blendIndices = skin.VerticesView.TryGetAccessor(
                        VertexElement.BLEND_INDEX.Name,
                        out VertexElementAccessor blendIndexAccessor)
                    ? blendIndexAccessor.AsXyzwU8Array().ToArray()
                    : null;
                var blendWeights = skin.VerticesView.TryGetAccessor(
                        VertexElement.BLEND_WEIGHT.Name,
                        out VertexElementAccessor blendWeightAccessor)
                    ? blendWeightAccessor.AsVector4Array().ToArray()
                    : null;
                int vertexCount = skin.VerticesView.VertexCount;
                if ((blendIndices != null && blendIndices.Length != vertexCount) ||
                    (blendWeights != null && blendWeights.Length != vertexCount))
                {
                    return Fail(out failureReason, "Blend index or weight data is mismatched.");
                }
                if (blendIndices == null || blendWeights == null)
                {
                    blendIndices = null;
                    blendWeights = null;
                }

                var parts = new Dictionary<ModelPart, PartData>(modelParts.Count);
                foreach (ModelPart part in modelParts)
                {
                    int[] sourceVertexIndices = part?.SourceVertexIndices;
                    if (part == null || sourceVertexIndices == null || sourceVertexIndices.Length == 0)
                        return Fail(out failureReason, $"Submesh '{part?.Name ?? "Unknown"}' has no source vertex mapping.");
                    if (part.Geometry?.Geometry is not MeshGeometry3D mesh ||
                        mesh.Positions == null ||
                        sourceVertexIndices.Length != mesh.Positions.Count)
                    {
                        return Fail(out failureReason, $"Submesh '{part.Name}' geometry does not match its source vertex mapping.");
                    }

                    var directBoneIndices = new float[sourceVertexIndices.Length * 4];
                    var weights = new float[sourceVertexIndices.Length * 4];

                    for (int localVertex = 0; localVertex < sourceVertexIndices.Length; localVertex++)
                    {
                        int sourceVertex = sourceVertexIndices[localVertex];
                        if ((uint)sourceVertex >= (uint)vertexCount)
                            return Fail(out failureReason, $"Submesh '{part.Name}' references an out-of-range source vertex.");

                        var sourceIndices = blendIndices == null
                            ? default
                            : blendIndices[sourceVertex];
                        var sourceWeights = blendWeights == null
                            ? new System.Numerics.Vector4(1f, 0f, 0f, 0f)
                            : blendWeights[sourceVertex];

                        int destination = localVertex * 4;

                        if (!TryResolveJoint(sourceIndices.x, sourceWeights.X, skeleton, out directBoneIndices[destination]) ||
                            !TryResolveJoint(sourceIndices.y, sourceWeights.Y, skeleton, out directBoneIndices[destination + 1]) ||
                            !TryResolveJoint(sourceIndices.z, sourceWeights.Z, skeleton, out directBoneIndices[destination + 2]) ||
                            !TryResolveJoint(sourceIndices.w, sourceWeights.W, skeleton, out directBoneIndices[destination + 3]))
                        {
                            return Fail(out failureReason, $"Submesh '{part.Name}' contains an invalid bone influence.");
                        }

                        weights[destination] = sourceWeights.X;
                        weights[destination + 1] = sourceWeights.Y;
                        weights[destination + 2] = sourceWeights.Z;
                        weights[destination + 3] = sourceWeights.W;
                    }

                    parts[part] = new PartData(directBoneIndices, weights);
                }

                return new GpuSkinningData(parts);
            }
            catch (Exception ex)
            {
                failureReason = $"Could not read skin data: {ex.Message}";
                return null;
            }
        }

        private static GpuSkinningData Fail(out string failureReason, string reason)
        {
            failureReason = reason;
            return null;
        }

        private static bool TryResolveJoint(
            byte influenceIndex,
            float weight,
            RigResource skeleton,
            out float jointIndex)
        {
            if (weight == 0f)
            {
                jointIndex = 0;
                return true;
            }

            if (influenceIndex >= skeleton.Influences.Count)
                influenceIndex = 0;

            short resolvedJoint = skeleton.Influences[influenceIndex];
            if (resolvedJoint < 0 || resolvedJoint >= skeleton.Joints.Count || resolvedJoint >= MaxBones)
            {
                jointIndex = 0;
                return false;
            }

            jointIndex = resolvedJoint;
            return true;
        }
    }
}
