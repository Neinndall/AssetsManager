using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Describes the authored render state of a champion material independently from optional shader-specific effects.
    /// </summary>
    public sealed record ModelMaterialDefinition(
        string BaseTextureName,
        Vector4 Color,
        float AlphaCutoff,
        Vector2 UvRepeat,
        Vector2 UvScroll,
        ModelMaterialWrapMode WrapU,
        ModelMaterialWrapMode WrapV,
        ModelMaterialRenderState RenderState,
        bool IsAnimated,
        string ShaderPath,
        ModelMaterialEffectDefinition Effect)
    {
        public static ModelMaterialDefinition Default { get; } = new(
            null,
            Vector4.One,
            0f,
            Vector2.One,
            Vector2.Zero,
            ModelMaterialWrapMode.Repeat,
            ModelMaterialWrapMode.Repeat,
            ModelMaterialRenderState.Default,
            false,
            null,
            ModelMaterialEffectDefinition.None);
    }

    public sealed record ModelMaterialRenderState(
        ModelMaterialBlendMode Blending,
        bool PremultipliedAlpha,
        bool DoubleSided,
        bool Inverted,
        bool DepthWrite,
        bool DepthTest)
    {
        public static ModelMaterialRenderState Default { get; } = new(
            ModelMaterialBlendMode.Opaque,
            false,
            false,
            false,
            true,
            true);
    }

    public enum ModelMaterialBlendMode
    {
        Opaque,
        Normal,
        Additive
    }

    public enum ModelMaterialWrapMode
    {
        Repeat,
        Clamp,
        Mirror,
        Border
    }

    public enum ModelMaterialBaseRule
    {
        None,
        SwitchOverride,
        Exact,
        ColorMapOverPlaceholder,
        ExactPlaceholder,
        NameLike,
        ColorMapPath,
        ColorMapPathAnyName
    }
}
