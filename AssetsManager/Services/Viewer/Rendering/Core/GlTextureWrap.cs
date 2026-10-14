using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.Core
{
    /// <summary>OpenGL wrap mode of an authored texture address mode; GL has no border colour here, so Border clamps.</summary>
    internal static class GlTextureWrap
    {
        internal static TextureWrapMode Of(ModelMaterialWrapMode wrap) =>
            wrap switch
            {
                ModelMaterialWrapMode.Clamp => TextureWrapMode.ClampToEdge,
                ModelMaterialWrapMode.Mirror => TextureWrapMode.MirroredRepeat,
                ModelMaterialWrapMode.Border => TextureWrapMode.ClampToEdge,
                _ => TextureWrapMode.Repeat
            };

        internal static TextureWrapMode Of(MapTextureWrap wrap) =>
            wrap switch
            {
                MapTextureWrap.Clamp => TextureWrapMode.ClampToEdge,
                MapTextureWrap.Mirror => TextureWrapMode.MirroredRepeat,
                MapTextureWrap.Border => TextureWrapMode.ClampToEdge,
                _ => TextureWrapMode.Repeat
            };
    }
}
