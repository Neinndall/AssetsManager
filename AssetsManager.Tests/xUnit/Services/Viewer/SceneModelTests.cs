using System.Windows.Media;
using System.Windows.Media.Media3D;
using AssetsManager.Views.Models.Viewer;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer
{
    public class SceneModelTests
    {
        [Fact]
        public void ModelPart_KeepsVisualSynchronizedWithGeometryAndVisibility()
        {
            var firstGeometry = CreateGeometry();
            var secondGeometry = CreateGeometry();
            var part = new ModelPart("Part", firstGeometry);

            Assert.Same(firstGeometry, part.Visual.Content);

            part.Geometry = secondGeometry;
            Assert.Same(secondGeometry, part.Visual.Content);

            part.IsVisible = false;
            Assert.Null(part.Visual.Content);

            part.IsVisible = true;
            Assert.Same(secondGeometry, part.Visual.Content);
        }

        [Fact]
        public void ModelPart_HidesVfxNamedPartsByDefault()
        {
            var geometry = CreateGeometry();
            var part = new ModelPart("Pyke_VFX_Dagger", geometry);

            Assert.False(part.IsVisible);
            Assert.Null(part.Visual.Content);

            part.IsVisible = true;

            Assert.Same(geometry, part.Visual.Content);
        }

        [Fact]
        public void SceneModel_AddAndRemovePartOwnsVisualTreeConsistently()
        {
            var scene = new SceneModel();
            var first = new ModelPart("First", CreateGeometry());
            var second = new ModelPart("Second", CreateGeometry());

            int added = scene.AddParts(new[] { first, second, first });

            Assert.Equal(2, added);
            Assert.Equal(2, scene.Parts.Count);
            Assert.Contains(first.Visual, scene.RootVisual.Children);
            Assert.Contains(second.Visual, scene.RootVisual.Children);

            Assert.True(scene.RemovePart(first));
            Assert.DoesNotContain(first, scene.Parts);
            Assert.DoesNotContain(first.Visual, scene.RootVisual.Children);

            scene.Parts.Clear();
            Assert.Empty(scene.RootVisual.Children);
        }

        private static GeometryModel3D CreateGeometry() =>
            new(
                new MeshGeometry3D(),
                new DiffuseMaterial(new SolidColorBrush(Colors.White)));
    }
}
