using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Mesh;
using LeagueToolkit.Hashing;
using LeagueToolkit.Core.Memory;
using AssetsManager.Views.Models;
using AssetsManager.Services;
using AssetsManager.Services.Core;
using Quaternion = System.Numerics.Quaternion;
using System.Linq;
using System.Threading.Tasks;

namespace AssetsManager.Services.Models
{
  public class AnimationPlayer
  {
    private readonly Dictionary<uint, (Quaternion Rotation, Vector3 Translation, Vector3 Scale)> _currentPose = new();
    private readonly LogService _logService;

    public AnimationPlayer(LogService logService)
    {
      _logService = logService;
    }

    public void Update(float totalSeconds, IAnimationAsset animation, RigResource skeleton, SkinnedMesh skin,
        List<ModelPart> modelParts, LinesVisual3D skeletonVisual, PointsVisual3D jointsVisual)
    {
      if (animation == null || skeleton == null || skin == null)
      {
        return;
      }

      var currentTime = totalSeconds % animation.Duration;
      animation.Evaluate(currentTime, _currentPose);

      var boneTransforms = new Matrix4x4[skeleton.Joints.Count];
      for (int i = 0; i < skeleton.Joints.Count; i++)
      {
        var joint = skeleton.Joints[i];
        var jointHash = Elf.HashLower(joint.Name);

        var localTransform = joint.LocalTransform;
        if (_currentPose.TryGetValue(jointHash, out var pose))
        {
          localTransform = Matrix4x4.CreateScale(pose.Scale) *
                           Matrix4x4.CreateFromQuaternion(pose.Rotation) *
                           Matrix4x4.CreateTranslation(pose.Translation);
        }

        if (joint.ParentId > -1)
        {
          boneTransforms[i] = localTransform * boneTransforms[joint.ParentId];
        }
        else
        {
          boneTransforms[i] = localTransform;
        }
      }

      var finalBoneTransforms = new Matrix4x4[skeleton.Joints.Count];
      for (int i = 0; i < skeleton.Joints.Count; i++)
      {
        finalBoneTransforms[i] = skeleton.Joints[i].InverseBindTransform * boneTransforms[i];
      }

      var positions = skin.VerticesView.GetAccessor(VertexElement.POSITION.Name).AsVector3Array();
      var blendIndices = skin.VerticesView.GetAccessor(VertexElement.BLEND_INDEX.Name).AsXyzwU8Array();
      var blendWeights = skin.VerticesView.GetAccessor(VertexElement.BLEND_WEIGHT.Name).AsVector4Array();

      if (positions.Count == 0)
      {
        return;
      }

      var skinnedVertices = new Vector3[positions.Count];

      try
      {
        var influencesCount = skeleton.Influences.Count;
        var boneCount = finalBoneTransforms.Length;

        Parallel.For(0, positions.Count, i =>
        {
          var pos = positions[i];
          var indices = blendIndices[i];
          var weights = blendWeights[i];

          var idx0 = indices.x < influencesCount ? skeleton.Influences[indices.x] : 0;
          var idx1 = indices.y < influencesCount ? skeleton.Influences[indices.y] : 0;
          var idx2 = indices.z < influencesCount ? skeleton.Influences[indices.z] : 0;
          var idx3 = indices.w < influencesCount ? skeleton.Influences[indices.w] : 0;

          var i0 = idx0 < boneCount ? idx0 : 0;
          var i1 = idx1 < boneCount ? idx1 : 0;
          var i2 = idx2 < boneCount ? idx2 : 0;
          var i3 = idx3 < boneCount ? idx3 : 0;

          Matrix4x4 skinningMatrix = finalBoneTransforms[i0] * weights.X +
                                           finalBoneTransforms[i1] * weights.Y +
                                           finalBoneTransforms[i2] * weights.Z +
                                           finalBoneTransforms[i3] * weights.W;

          skinnedVertices[i] = Vector3.Transform(pos, skinningMatrix);
        });

        for (int i = 0; i < modelParts.Count; i++)
        {
          var part = modelParts[i];
          var range = skin.Ranges[i];
          var geometry = (MeshGeometry3D)part.Geometry.Geometry;

          var partPositions = new Point3D[range.VertexCount];
          for (int j = 0; j < range.VertexCount; j++)
          {
            var vertexIndex = range.StartVertex + j;
            if (vertexIndex < 0 || vertexIndex >= skinnedVertices.Length)
            {
              _logService.LogError($"[ERROR] Invalid vertexIndex {vertexIndex} for skinnedVertices. Range: {range.StartVertex}-{range.StartVertex + range.VertexCount - 1}, SkinnedVerticesLength: {skinnedVertices.Length}");
              continue;
            }

            var skinnedPos = skinnedVertices[vertexIndex];
            partPositions[j] = new Point3D(skinnedPos.X, skinnedPos.Y, skinnedPos.Z);
          }

          geometry.Positions = new Point3DCollection(partPositions);
        }
      }
      catch (Exception ex)
      {
        _logService.LogError(ex, "CRASH: Exception during skinning!");
        animation = null;
        return;
      }
    }
  }
}
