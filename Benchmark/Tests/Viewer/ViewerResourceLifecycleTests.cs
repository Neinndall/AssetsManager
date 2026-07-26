using System.Collections.Generic;
using System.Numerics;
using System.Windows.Media.Media3D;
using AssetsManager.Views.Controls.Viewer;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.BenchmarkTests.Services.Viewer
{
    public sealed class ViewerResourceLifecycleTests
    {
        [Fact]
        public void SceneDisposalReleasesOwnedVisualsAndPartResources()
        {
            var textures = new Dictionary<string, System.Windows.Media.Imaging.BitmapSource>
            {
                ["body"] = null
            };
            var part = new ModelPart
            {
                Name = "Body",
                Visual = new ModelVisual3D(),
                Geometry = new GeometryModel3D
                {
                    Geometry = new MeshGeometry3D(),
                    Material = new DiffuseMaterial()
                },
                AllTextures = textures,
                SourceVertexIndices = new[] { 0, 1, 2 }
            };
            var scene = new SceneModel();
            scene.Parts.Add(part);

            scene.Dispose();

            Assert.Null(scene.RootVisual);
            Assert.Null(scene.SkinnedMesh);
            Assert.Null(scene.Skeleton);
            Assert.Empty(scene.Parts);
            Assert.Empty(scene.Animations);
            Assert.Empty(textures);
            Assert.Null(part.Visual);
            Assert.Null(part.Geometry);
            Assert.Null(part.AllTextures);
            Assert.Null(part.SourceVertexIndices);
        }

        [Fact]
        public void SceneDisposalIsIdempotentAndDetachesPartEvents()
        {
            var part = new ModelPart { Name = "Eyes" };
            var scene = new SceneModel { IsMeshSyncEnabled = true };
            int visibilityChanges = 0;
            scene.MeshVisibilityChanged += _ => visibilityChanges++;
            scene.Parts.Add(part);

            scene.Dispose();
            scene.Dispose();
            part.IsVisible = true;

            Assert.Equal(0, visibilityChanges);
        }

        [Fact]
        public void ProjectionFarPlaneExpandsForMapScaleCameraDistances()
        {
            float farPlane = ViewerViewportControl.CalculateProjectionFarPlane(
                new Vector3(0f, -18000f, -15000f));

            Assert.True(farPlane > 90000f);
        }
    }
}
