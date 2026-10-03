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
    private uint _projectionTerrainProgram;
    private VfxShaderUniforms _projectionTerrainUniforms;
    private uint _terrainDepthTexture;
    private Vector2 _terrainDepthSize;

    /// <summary>
    /// The depth of the map geometry alone, captured before structures and characters draw. Planar projections land
    /// on it like the game's decals on map triangles; without it they lie on the flat ground.
    /// </summary>
    internal void SetTerrainDepth(uint texture, uint width, uint height)
    {
        _terrainDepthTexture = texture;
        _terrainDepthSize = new Vector2(width, height);
    }
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
        bool onTerrain = _terrainDepthTexture != 0 && !wireframe;
        if (onTerrain && _projectionTerrainProgram == 0)
        {
            _projectionTerrainProgram = GlShaderCompiler.CreateProgram(_gl, _gles,
                VfxProjectionShaderSource.Vertex, VfxProjectionShaderSource.TerrainFragment);
            _projectionTerrainUniforms = new VfxShaderUniforms(_gl, _projectionTerrainProgram);
        }
        var definition = emitter.Def;
        var state = definition.RenderState ?? VfxEmitterRenderState.Default;
        uint program = onTerrain ? _projectionTerrainProgram : _projectionProgram;
        var uniforms = onTerrain ? _projectionTerrainUniforms : _projectionUniforms;
        _drawBindings.UseProgram(program);
        VfxProjectionDefinition band = definition.Projection ?? new VfxProjectionDefinition();
        uniforms.Uniform1(uniforms.TerrainMode, onTerrain ? 1 : 0);
        uniforms.Uniform2(uniforms.ProjectionBand, band.YRange, band.Fading);
        if (onTerrain)
        {
            Matrix4x4.Invert(viewProj, out Matrix4x4 inverseViewProj);
            _gl.UniformMatrix4(uniforms.InverseViewProj, 1, false, in inverseViewProj.M11);
            uniforms.Uniform2(uniforms.ViewportSize, _terrainDepthSize.X, _terrainDepthSize.Y);
            uniforms.Uniform1(uniforms.TerrainDepth, 5);
            _gl.ActiveTexture(TextureUnit.Texture5);
            _gl.BindTexture(TextureTarget.Texture2D, _terrainDepthTexture);
            _gl.ActiveTexture(TextureUnit.Texture0);
        }
        _gl.UniformMatrix4(uniforms.ViewProj, 1, false, in viewProj.M11);
        uniforms.Uniform1(uniforms.Tex, 0);
        uniforms.Uniform1(uniforms.ColorMap, 7);
        uniforms.Uniform1(uniforms.HasTex, emitter.Texture != 0 ? 1 : 0);
        uniforms.Uniform1(uniforms.HasColor, UsesProjectionColorRamp(definition, emitter.ColorGradientTexture != 0) ? 1 : 0);
        uniforms.Uniform1(uniforms.TexMult, 1);
        uniforms.Uniform1(uniforms.HasTexMult, emitter.TextureMult != 0 ? 1 : 0);
        uniforms.Uniform1(uniforms.AddressModeMult, definition.TextureMultAddressMode);
        uniforms.Uniform2(uniforms.UvTransformCenterMult, definition.TextureMultTransformCenter.X, definition.TextureMultTransformCenter.Y);
        Vector2 emitterUvOffsetMult = VfxUvSemantics.Periodic(
            definition.TextureMultEmitterUvScrollRate * emitter.RenderTime, definition.TextureMultAddressMode);
        uniforms.Uniform2(uniforms.UvScrollRateMult, emitterUvOffsetMult.X, emitterUvOffsetMult.Y);
        uniforms.Uniform1(uniforms.FlipUMult, definition.TextureMultFlipU ? 1 : 0);
        uniforms.Uniform1(uniforms.FlipVMult, definition.TextureMultFlipV ? 1 : 0);
        VfxAlphaErosionDefinition erosion = definition.AlphaErosion;
        Vector4 erosionDefault = erosion is not null && string.IsNullOrWhiteSpace(erosion.TexturePath) ? Vector4.One : Vector4.Zero;
        uniforms.Uniform1(uniforms.ErosionTex, 4);
        uniforms.Uniform1(uniforms.HasErosion, erosion is not null ? 1 : 0);
        uniforms.Uniform1(uniforms.HasErosionMap, erosion is not null && emitter.ErosionTexture != 0 ? 1 : 0);
        uniforms.Uniform1(uniforms.ErosionAddressMode, erosion?.AddressMode ?? 0);
        uniforms.Uniform4(uniforms.ErosionDefault, erosionDefault.X, erosionDefault.Y, erosionDefault.Z, erosionDefault.W);
        uniforms.Uniform1(uniforms.ErosionFeatherIn, erosion?.FeatherIn ?? 0f);
        uniforms.Uniform1(uniforms.ErosionFeatherOut, erosion?.FeatherOut ?? 0f);
        uniforms.Uniform1(uniforms.ErosionSliceWidth, erosion?.SliceWidth ?? 1.5f);
        uniforms.Uniform1(uniforms.AlphaCutoff,
            VfxBlendModes.ShouldAlphaTest(definition.BlendMode, state.AlphaReference) ? state.AlphaCutoff : 0f);
        uniforms.Uniform1(uniforms.WireframePass, wireframe ? 1 : 0);
        uniforms.Uniform4(uniforms.WireframeColor, PreviewWireColor.X, PreviewWireColor.Y, PreviewWireColor.Z, wireOpacity);
        uniforms.Uniform1(uniforms.AddressMode, state.TextureAddressMode);
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
        _gl.BufferData(BufferTargetARB.ArrayBuffer, decals, BufferUsageARB.StreamDraw);
        _gl.DrawArraysInstanced(PrimitiveType.TriangleFan, 0, 4, (uint)count);
    }

    /// <summary>
    /// UNLIT_DECAL reads PARTICLE_COLOR_TEXTURE only in its permutations without ALPHA_EROSION and MULT_PASS, which an
    /// emitter authoring alphaErosionDefinition or textureMult selects.
    /// </summary>
    internal static bool UsesProjectionColorRamp(VfxEmitterDefinition definition, bool hasRamp) =>
        hasRamp && definition.AlphaErosion is null && !HasTextureMultLayer(definition);
}
