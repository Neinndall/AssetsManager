using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Vfx.Rendering;

public sealed partial class VfxOpenGlRenderer
{
    private uint _projectionProgram;
    private VfxShaderUniforms _projectionUniforms;
    private readonly VfxProjectionGeometry _projectionGeometry = new();

    private void RenderProjection(VfxPlaybackRuntime.EmitterState emitter, ReadOnlySpan<float> instances,
        int count, Matrix4x4 viewProj, bool wireframe, float wireOpacity)
    {
        if (_projectionProgram == 0)
        {
            _projectionProgram = GlShaderCompiler.CreateProgram(_gl, _gles,
                VfxProjectionShaderSource.Vertex, VfxProjectionShaderSource.Fragment);
            _projectionUniforms = new VfxShaderUniforms(_gl, _projectionProgram);
        }
        var definition = emitter.Def;
        var state = definition.RenderState ?? VfxEmitterRenderState.Default;
        var uniforms = _projectionUniforms;
        _gl.UseProgram(_projectionProgram);
        _gl.UniformMatrix4(uniforms.ViewProj, 1, false, in viewProj.M11);
        _gl.Uniform1(uniforms.Tex, 0);
        _gl.Uniform1(uniforms.ColorMap, 7);
        _gl.Uniform1(uniforms.HasTex, emitter.Texture != 0 ? 1 : 0);
        _gl.Uniform1(uniforms.HasColor, UsesProjectionColorRamp(definition, emitter.ColorGradientTexture != 0) ? 1 : 0);
        _gl.Uniform1(uniforms.TexMult, 1);
        _gl.Uniform1(uniforms.HasTexMult, emitter.TextureMult != 0 ? 1 : 0);
        _gl.Uniform1(uniforms.AddressModeMult, definition.TextureMultAddressMode);
        _gl.Uniform2(uniforms.UvTransformCenterMult, definition.TextureMultTransformCenter.X, definition.TextureMultTransformCenter.Y);
        Vector2 emitterUvOffsetMult = VfxUvSemantics.Periodic(
            definition.TextureMultEmitterUvScrollRate * emitter.RenderTime, definition.TextureMultAddressMode);
        _gl.Uniform2(uniforms.UvScrollRateMult, emitterUvOffsetMult.X, emitterUvOffsetMult.Y);
        _gl.Uniform1(uniforms.FlipUMult, definition.TextureMultFlipU ? 1 : 0);
        _gl.Uniform1(uniforms.FlipVMult, definition.TextureMultFlipV ? 1 : 0);
        VfxAlphaErosionDefinition erosion = definition.AlphaErosion;
        Vector4 erosionDefault = erosion is not null && string.IsNullOrWhiteSpace(erosion.TexturePath) ? Vector4.One : Vector4.Zero;
        _gl.Uniform1(uniforms.ErosionTex, 4);
        _gl.Uniform1(uniforms.HasErosion, erosion is not null ? 1 : 0);
        _gl.Uniform1(uniforms.HasErosionMap, erosion is not null && emitter.ErosionTexture != 0 ? 1 : 0);
        _gl.Uniform1(uniforms.ErosionAddressMode, erosion?.AddressMode ?? 0);
        _gl.Uniform4(uniforms.ErosionDefault, erosionDefault.X, erosionDefault.Y, erosionDefault.Z, erosionDefault.W);
        _gl.Uniform1(uniforms.ErosionFeatherIn, erosion?.FeatherIn ?? 0f);
        _gl.Uniform1(uniforms.ErosionFeatherOut, erosion?.FeatherOut ?? 0f);
        _gl.Uniform1(uniforms.ErosionSliceWidth, erosion?.SliceWidth ?? 1.5f);
        _gl.Uniform1(uniforms.AlphaCutoff,
            VfxBlendModes.ShouldAlphaTest(definition.BlendMode, state.AlphaReference) ? state.AlphaCutoff : 0f);
        _gl.Uniform1(uniforms.WireframePass, wireframe ? 1 : 0);
        _gl.Uniform4(uniforms.WireframeColor, PreviewWireColor.X, PreviewWireColor.Y, PreviewWireColor.Z, wireOpacity);
        _gl.Uniform1(uniforms.AddressMode, state.TextureAddressMode);
        _gl.Disable(EnableCap.CullFace);
        if (VfxBlendModes.ShouldTestDepth(definition.MiscRenderFlags)) _gl.Enable(EnableCap.DepthTest);
        else _gl.Disable(EnableCap.DepthTest);
        _gl.DepthFunc(DepthFunction.Lequal);
        _gl.DepthMask(!wireframe && VfxBlendModes.ShouldWriteDepth(definition.BlendMode, state.AlphaReference));
        Vector2 bias = definition.DepthBiasFactors is { } authored && authored != Vector2.Zero
            ? authored : new Vector2(-1f, -1f);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(bias.X, bias.Y);
        if (wireframe) ApplyWireframeBlend();
        else ApplyBlendMode(definition.BlendMode, false);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, emitter.Texture != 0 ? emitter.Texture : _textures.FallbackTransparentTexture);
        ApplyAddressMode(state.TextureAddressMode);
        ApplyTextureSampling();
        _gl.ActiveTexture(TextureUnit.Texture7);
        _gl.BindTexture(TextureTarget.Texture2D, emitter.ColorGradientTexture != 0
            ? emitter.ColorGradientTexture : _textures.FallbackTransparentTexture);
        ApplyAddressMode(2);
        ApplyTextureSampling();
        if (emitter.TextureMult != 0)
        {
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, emitter.TextureMult);
            ApplyAddressMode(2);
        }
        if (emitter.ErosionTexture != 0)
        {
            _gl.ActiveTexture(TextureUnit.Texture4);
            _gl.BindTexture(TextureTarget.Texture2D, emitter.ErosionTexture);
            ApplyAddressMode(2);
        }
        _gl.ActiveTexture(TextureUnit.Texture0);
        ReadOnlySpan<float> decals = _projectionGeometry.Prepare(definition, instances);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _instVbo);
        if (decals.Length > _instCapFloats)
        {
            _gl.BufferData(BufferTargetARB.ArrayBuffer, decals, BufferUsageARB.DynamicDraw);
            _instCapFloats = decals.Length;
        }
        else _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, decals);
        _gl.DrawArraysInstanced(PrimitiveType.TriangleFan, 0, 4, (uint)count);
        _gl.UseProgram(_program);
    }

    /// <summary>
    /// UNLIT_DECAL reads PARTICLE_COLOR_TEXTURE only in its permutations without ALPHA_EROSION and MULT_PASS, which an
    /// emitter authoring alphaErosionDefinition or textureMult selects.
    /// </summary>
    internal static bool UsesProjectionColorRamp(VfxEmitterDefinition definition, bool hasRamp) =>
        hasRamp && definition.AlphaErosion is null && !HasTextureMultLayer(definition);
}
