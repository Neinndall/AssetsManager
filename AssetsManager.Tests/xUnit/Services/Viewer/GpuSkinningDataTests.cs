using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Media3D;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Animation.Builders;
using LeagueToolkit.Core.Mesh;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer
{
    public sealed class GpuSkinningDataTests
    {
        [Fact]
        public void MaxBones_IsConfiguredForModernLimits_512()
        {
            Assert.Equal(512, GpuSkinningData.MaxBones);
        }

        [Fact]
        public void TryCreate_FailsWhenSkeletonJointCountExceedsMaxBones()
        {
            var rigBuilder = new RigResourceBuilder();
            for (int i = 0; i < GpuSkinningData.MaxBones + 1; i++)
            {
                rigBuilder.CreateJoint($"Joint_{i}");
            }
            RigResource rig = rigBuilder.Build();

            var result = GpuSkinningData.TryCreate(rig, null, Array.Empty<ModelPart>(), out string failureReason);

            Assert.Null(result);
            Assert.Equal("Skeleton or skin data is missing or outside GPU limits.", failureReason);
        }

        [Fact]
        public void TryCreate_SucceedsForJannaSkin67_WhenFilesPresent()
        {
            string sknPath = @"C:\Users\danielpriego\Desktop\Janna.wad.client\assets\characters\janna\skins\skin67\janna_skin67.skn";
            string sklPath = @"C:\Users\danielpriego\Desktop\Janna.wad.client\assets\characters\janna\skins\skin67\janna_skin67.skl";

            if (!File.Exists(sknPath) || !File.Exists(sklPath))
            {
                return;
            }

            using var sknStream = File.OpenRead(sknPath);
            var skn = SkinnedMesh.ReadFromSimpleSkin(sknStream);

            using var sklStream = File.OpenRead(sklPath);
            var skl = new RigResource(sklStream);

            Assert.True(skl.Joints.Count > 256, "Janna skin67 is expected to have > 256 joints.");
            Assert.True(skl.Joints.Count <= GpuSkinningData.MaxBones, "Janna skin67 joints must fit within MaxBones.");

            var modelParts = new List<ModelPart>();
            foreach (var range in skn.Ranges)
            {
                var mesh = new MeshGeometry3D();
                var sourceVertexIndices = new int[range.VertexCount];
                for (int i = 0; i < range.VertexCount; i++)
                {
                    mesh.Positions.Add(new Point3D(0, 0, 0));
                    sourceVertexIndices[i] = range.StartVertex + i;
                }
                modelParts.Add(new ModelPart(range.Material, new GeometryModel3D(mesh, null))
                {
                    SourceVertexIndices = sourceVertexIndices
                });
            }

            var gpuData = GpuSkinningData.TryCreate(skl, skn, modelParts, out string failureReason);

            Assert.NotNull(gpuData);
            Assert.Null(failureReason);
        }
    }
}
