using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    /// <summary>
    /// Describes the authored render state of a champion material.
    /// </summary>
    public sealed record ModelMaterialDefinition(
        string BaseTextureName,
        ModelMaterialBaseRule BaseRule,
        Vector4 Color,
        float AlphaCutoff,
        Vector2 UvRepeat,
        Vector2 UvScroll,
        ModelMaterialWrapMode WrapU,
        ModelMaterialWrapMode WrapV,
        ModelMaterialRenderState RenderState,
        ModelMaterialBindingKind BindingKind,
        bool IsAnimated,
        string ShaderPath)
    {
        internal bool HasAuthoredTint { get; init; }
        internal GameMaterialProgram Program { get; init; }

        public bool IsLit =>
            BindingKind != ModelMaterialBindingKind.Missing &&
            RenderState.Blending != ModelMaterialBlendMode.Additive;

        // Character color maps frequently store masks/data in alpha. Match the material
        // preview contract: texture alpha is coverage only for authored blending or alpha test.
        public bool UsesTextureAlpha =>
            RenderState.Blending != ModelMaterialBlendMode.Opaque ||
            AlphaCutoff > 0f;

        public static ModelMaterialDefinition Default { get; } = TextureOnly(null);

        public static ModelMaterialDefinition TextureOnly(string baseTextureName) =>
            TextureOnly(baseTextureName, null);

        internal static ModelMaterialDefinition TextureOnly(string baseTextureName, GameMaterialProgram program) =>
            new(
                baseTextureName,
                ModelMaterialBaseRule.None,
                Vector4.One,
                0f,
                Vector2.One,
                Vector2.Zero,
                ModelMaterialWrapMode.Clamp,
                ModelMaterialWrapMode.Clamp,
                ModelMaterialRenderState.TextureOnly,
                ModelMaterialBindingKind.TextureOnly,
                false,
                program?.Passes?.Count > 0 ? program.Passes[0].ShaderPath : null)
            {
                Program = program
            };

        public static ModelMaterialDefinition Missing { get; } = new(
            null,
            ModelMaterialBaseRule.None,
            Vector4.One,
            0f,
            Vector2.One,
            Vector2.Zero,
            ModelMaterialWrapMode.Clamp,
            ModelMaterialWrapMode.Clamp,
            ModelMaterialRenderState.TextureOnly,
            ModelMaterialBindingKind.Missing,
            false,
            null);
    }

    public sealed record ModelMaterialRenderState(
        ModelMaterialBlendMode Blending,
        bool PremultipliedAlpha,
        bool Cutout,
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
            false,
            true,
            true);

        // A skin with no StaticMaterialDef is drawn from its texture alone and keeps both faces.
        public static ModelMaterialRenderState TextureOnly { get; } = new(
            ModelMaterialBlendMode.Opaque,
            false,
            false,
            true,
            false,
            true,
            true);
    }

    public enum ModelMaterialBindingKind
    {
        TextureOnly,
        Authored,
        Missing
    }

    public enum ModelMaterialBlendMode
    {
        Opaque,
        Normal,
        Additive,
        Modulate
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
