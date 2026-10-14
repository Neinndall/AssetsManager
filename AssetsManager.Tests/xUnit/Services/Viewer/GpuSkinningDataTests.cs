using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Media3D;
using CommunityToolkit.HighPerformance.Buffers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Animation;
using LeagueToolkit.Core.Animation.Builders;
using LeagueToolkit.Core.Memory;
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
        public void TryCreate_ClampsInvalidShaderJointToFirstInfluenceLikeLtk()
        {
            var rigBuilder = new RigResourceBuilder();
            rigBuilder.CreateJoint("Root");
            rigBuilder.CreateJoint("Influence").WithInfluence(true);
            RigResource rig = rigBuilder.Build();

            VertexBufferDescription description = SkinnedMeshVertex.BASIC;
            MemoryOwner<byte> vertexOwner = VertexBuffer.AllocateForElements(description.Elements, 3);
            Span<byte> vertices = vertexOwner.Span;
            const int stride = 52;
            for (int vertex = 0; vertex < 3; vertex++)
            {
                int at = vertex * stride;
                BitConverter.TryWriteBytes(vertices.Slice(at, 4), (float)vertex);
                vertices[at + 12] = byte.MaxValue;
                BitConverter.TryWriteBytes(vertices.Slice(at + 16, 4), 1f);
                BitConverter.TryWriteBytes(vertices.Slice(at + 36, 4), 1f);
            }

            VertexBuffer vertexBuffer = VertexBuffer.Create(description.Usage, description.Elements, vertexOwner);
            MemoryOwner<byte> indexOwner = MemoryOwner<byte>.Allocate(3 * sizeof(ushort));
            Span<byte> indices = indexOwner.Span;
            for (ushort index = 0; index < 3; index++)
                BitConverter.TryWriteBytes(indices.Slice(index * sizeof(ushort), sizeof(ushort)), index);
            IndexBuffer indexBuffer = IndexBuffer.Create(IndexFormat.U16, indexOwner);

            using var skin = new SkinnedMesh(
                new[] { new SkinnedMeshRange("body", 0, 3, 0, 3) },
                vertexBuffer,
                indexBuffer);
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection
                {
                    new(0, 0, 0),
                    new(1, 0, 0),
                    new(0, 1, 0)
                }
            };
            var part = new ModelPart("body", new GeometryModel3D(mesh, null))
            {
                SourceVertexIndices = new[] { 0, 1, 2 }
            };

            GpuSkinningData result = GpuSkinningData.TryCreate(
                rig,
                skin,
                new[] { part },
                out string failureReason);

            Assert.NotNull(result);
            Assert.Null(failureReason);
            Assert.True(result.TryGetPart(part, out GpuSkinningData.PartData data));
            for (int vertex = 0; vertex < 3; vertex++)
                Assert.Equal(1f, data.BoneIndices[vertex * 4]);
        }

        [Fact]
        public void TryCreate_MissingOneSkinChannelFallsBackToFirstInfluenceLikeLtk()
        {
            var rigBuilder = new RigResourceBuilder();
            rigBuilder.CreateJoint("Root");
            rigBuilder.CreateJoint("Influence").WithInfluence(true);
            RigResource rig = rigBuilder.Build();

            var description = new VertexBufferDescription(
                VertexBufferUsage.Static,
                new[]
                {
                    VertexElement.POSITION,
                    VertexElement.BLEND_INDEX,
                    VertexElement.NORMAL,
                    VertexElement.TEXCOORD_0
                });
            MemoryOwner<byte> vertexOwner = VertexBuffer.AllocateForElements(description.Elements, 3);
            Span<byte> vertices = vertexOwner.Span;
            const int stride = 36;
            for (int vertex = 0; vertex < 3; vertex++)
            {
                int at = vertex * stride;
                BitConverter.TryWriteBytes(vertices.Slice(at, 4), (float)vertex);
            }

            VertexBuffer vertexBuffer = VertexBuffer.Create(description.Usage, description.Elements, vertexOwner);
            MemoryOwner<byte> indexOwner = MemoryOwner<byte>.Allocate(3 * sizeof(ushort));
            Span<byte> indices = indexOwner.Span;
            for (ushort index = 0; index < 3; index++)
                BitConverter.TryWriteBytes(indices.Slice(index * sizeof(ushort), sizeof(ushort)), index);
            IndexBuffer indexBuffer = IndexBuffer.Create(IndexFormat.U16, indexOwner);

            using var skin = new SkinnedMesh(
                new[] { new SkinnedMeshRange("body", 0, 3, 0, 3) },
                vertexBuffer,
                indexBuffer);
            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection
                {
                    new(0, 0, 0),
                    new(1, 0, 0),
                    new(0, 1, 0)
                }
            };
            var part = new ModelPart("body", new GeometryModel3D(mesh, null))
            {
                SourceVertexIndices = new[] { 0, 1, 2 }
            };

            GpuSkinningData result = GpuSkinningData.TryCreate(
                rig,
                skin,
                new[] { part },
                out string failureReason);

            Assert.NotNull(result);
            Assert.Null(failureReason);
            Assert.True(result.TryGetPart(part, out GpuSkinningData.PartData data));
            for (int vertex = 0; vertex < 3; vertex++)
            {
                int at = vertex * 4;
                Assert.Equal(1f, data.BoneIndices[at]);
                Assert.Equal(1f, data.BoneWeights[at]);
                Assert.Equal(0f, data.BoneWeights[at + 1]);
                Assert.Equal(0f, data.BoneWeights[at + 2]);
                Assert.Equal(0f, data.BoneWeights[at + 3]);
            }
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
