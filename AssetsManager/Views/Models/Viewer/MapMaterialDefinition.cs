using System.Collections.Generic;
using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    internal sealed record MapTextureReference(
        string VirtualPath,
        ulong PathHash)
    {
        public bool IsEmpty => string.IsNullOrWhiteSpace(VirtualPath) && PathHash == 0;
    }

    internal sealed record MapBaseTexture(
        string Name,
        MapTextureReference Texture,
        MapMaterialBaseRule Rule,
        MapTextureWrap WrapU,
        MapTextureWrap WrapV);

    internal sealed record MapMaterialDefinition(
        string Name,
        uint PathHash,
        bool Missing,
        bool Animated,
        string ShaderPath,
        MapBaseTexture BaseTexture,
        Vector3? Tint,
        float? Opacity,
        float? AlphaTest,
        Vector2? UvRepeat,
        Vector2? UvScroll,
        MapMaterialRenderState RenderState,
        IReadOnlyList<string> Warnings)
    {
        /// <summary>
        /// Full normal-technique program contract matching current LTK MAIN. The legacy preview
        /// fields above remain the first-pass fallback until translated game shaders are available.
        /// </summary>
        public MapResolvedMaterialProgramData Program { get; init; }
    }

    internal sealed record MapMaterialRenderState(
        MapMaterialBlendMode Blending,
        MapBlendFactor SourceFactor,
        MapBlendFactor DestinationFactor,
        bool PremultipliedAlpha,
        bool Cutout,
        bool DoubleSided,
        bool Inverted,
        bool DepthWrite,
        bool DepthTest)
    {
        public static MapMaterialRenderState Default { get; } = new(
            MapMaterialBlendMode.Opaque,
            MapBlendFactor.One,
            MapBlendFactor.Zero,
            false,
            false,
            false,
            false,
            true,
            true);
    }

    internal enum MapMaterialBlendMode
    {
        Opaque,
        Normal,
        Additive,
        Modulate
    }

    internal enum MapBlendFactor
    {
        Zero,
        One,
        SourceColor,
        OneMinusSourceColor,
        DestinationColor,
        OneMinusDestinationColor,
        SourceAlpha,
        OneMinusSourceAlpha
    }

    internal enum MapTextureWrap
    {
        Repeat,
        Clamp,
        Mirror,
        Border
    }

    internal enum MapMaterialBaseRule
    {
        SwitchOverride,
        Exact,
        ColorMapOverPlaceholder,
        ExactPlaceholder,
        NameLike,
        ColorMapPath,
        ColorMapPathAnyName
    }
}
