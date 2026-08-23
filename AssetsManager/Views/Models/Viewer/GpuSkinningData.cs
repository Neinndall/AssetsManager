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
        internal const int MaxBones = 256;

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
            IList<ModelPart> modelParts)
        {
            if (skeleton == null || skin == null || modelParts == null ||
                skeleton.Joints.Count == 0 || skeleton.Joints.Count > MaxBones ||
                skeleton.Influences.Count == 0)
            {
                return null;
            }

            try
            {
                var blendIndices = skin.VerticesView
                    .GetAccessor(VertexElement.BLEND_INDEX.Name)
                    .AsXyzwU8Array()
                    .ToArray();
                var blendWeights = skin.VerticesView
                    .GetAccessor(VertexElement.BLEND_WEIGHT.Name)
                    .AsVector4Array()
                    .ToArray();

                if (blendIndices.Length == 0 || blendIndices.Length != blendWeights.Length)
                    return null;

                var parts = new Dictionary<ModelPart, PartData>(modelParts.Count);
                foreach (ModelPart part in modelParts)
                {
                    int[] sourceVertexIndices = part?.SourceVertexIndices;
                    if (part == null || sourceVertexIndices == null || sourceVertexIndices.Length == 0)
                        return null;
                    if (part.Geometry?.Geometry is not MeshGeometry3D mesh ||
                        mesh.Positions == null ||
                        sourceVertexIndices.Length != mesh.Positions.Count)
                    {
                        return null;
                    }

                    var directBoneIndices = new float[sourceVertexIndices.Length * 4];
                    var weights = new float[sourceVertexIndices.Length * 4];

                    for (int localVertex = 0; localVertex < sourceVertexIndices.Length; localVertex++)
                    {
                        int sourceVertex = sourceVertexIndices[localVertex];
                        if ((uint)sourceVertex >= (uint)blendIndices.Length)
                            return null;

                        var sourceIndices = blendIndices[sourceVertex];
                        var sourceWeights = blendWeights[sourceVertex];
                        int destination = localVertex * 4;

                        if (!TryResolveJoint(sourceIndices.x, skeleton, out directBoneIndices[destination]) ||
                            !TryResolveJoint(sourceIndices.y, skeleton, out directBoneIndices[destination + 1]) ||
                            !TryResolveJoint(sourceIndices.z, skeleton, out directBoneIndices[destination + 2]) ||
                            !TryResolveJoint(sourceIndices.w, skeleton, out directBoneIndices[destination + 3]))
                        {
                            return null;
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
            catch (Exception)
            {
                return null;
            }
        }

        private static bool TryResolveJoint(
            byte influenceIndex,
            RigResource skeleton,
            out float jointIndex)
        {
            if (influenceIndex >= skeleton.Influences.Count)
            {
                jointIndex = 0;
                return false;
            }

            short resolvedJoint = skeleton.Influences[influenceIndex];
            if (resolvedJoint < 0 || resolvedJoint >= skeleton.Joints.Count)
            {
                jointIndex = 0;
                return false;
            }

            jointIndex = resolvedJoint;
            return true;
        }
    }
}
