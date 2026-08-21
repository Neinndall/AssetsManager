using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Viewer.Composition;
using AssetsManager.Services.Viewer.Resolvers;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Composition
{
    public class MapGeometryLayeredTextureComposerTests
    {
        [Fact]
        public void Compose_UsesMaskRgbForMiddleTopAndExtrasInAuthoredOrder()
        {
            var samplers = new[]
            {
                Sampler("Mask_Texture", "mask.tex"),
                Sampler("Bottom_Texture", "bottom.tex"),
                Sampler("Middle_Texture", "middle.tex"),
                Sampler("Top_Texture", "top.tex"),
                Sampler("Extras_Texture", "extras.tex")
            };
            var material = new MapGeometryMaterialDefinition(
                "Terrain",
                samplers,
                null,
                new Dictionary<string, Vector4>(),
                0);
            var textures = new Dictionary<string, BitmapSource>
            {
                ["mask.tex"] = Bitmap(
                    2,
                    2,
                    0, 0, 0,
                    255, 0, 0,
                    0, 255, 0,
                    0, 0, 255),
                ["bottom.tex"] = Bitmap(1, 1, 255, 0, 0),
                ["middle.tex"] = Bitmap(1, 1, 0, 255, 0),
                ["top.tex"] = Bitmap(1, 1, 0, 0, 255),
                ["extras.tex"] = Bitmap(1, 1, 255, 255, 255)
            };

            BitmapSource result = MapGeometryLayeredTextureComposer.Compose(
                material,
                new MapGeometryUvWorldMapping(1, 0, 1, 0),
                textures,
                CancellationToken.None);

            var pixels = new byte[16];
            result.CopyPixels(pixels, 8, 0);
            Assert.Equal(new byte[]
            {
                0, 0, 255, 255,
                0, 255, 0, 255,
                255, 0, 0, 255,
                255, 255, 255, 255
            }, pixels);
        }

        [Fact]
        public void MappingBuilder_RecoversWorldPlanarCoordinatesFromMeshUv()
        {
            var builder = new MapGeometryUvWorldMappingBuilder();
            builder.Add(0, 0, new Vector3(-250, 0, 16000), Matrix4x4.Identity);
            builder.Add(1, 0, new Vector3(16250, 0, 16000), Matrix4x4.Identity);
            builder.Add(0, 1, new Vector3(-250, 0, -500), Matrix4x4.Identity);
            builder.Add(1, 1, new Vector3(16250, 0, -500), Matrix4x4.Identity);

            MapGeometryUvWorldMapping mapping = builder.Build();

            Vector2 world = mapping.Transform(0.25f, 0.75f);
            Assert.Equal(3875f, world.X, 2);
            Assert.Equal(3625f, world.Y, 2);
        }

        private static MapGeometryTextureSampler Sampler(string name, string path) =>
            new(name, string.Empty, path, 0, 0);

        private static BitmapSource Bitmap(int width, int height, params byte[] rgb)
        {
            var bgra = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                bgra[i * 4] = rgb[i * 3 + 2];
                bgra[i * 4 + 1] = rgb[i * 3 + 1];
                bgra[i * 4 + 2] = rgb[i * 3];
                bgra[i * 4 + 3] = byte.MaxValue;
            }

            BitmapSource result = BitmapSource.Create(
                width,
                height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                bgra,
                width * 4);
            result.Freeze();
            return result;
        }
    }
}
