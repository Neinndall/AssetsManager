using System;
using System.Numerics;
using Silk.NET.OpenGL;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    public sealed partial class VfxOpenGlRenderer
    {
        private void ApplyAddressMode(int addressMode)
        {
            var wrap = addressMode switch
            {
                1 => TextureWrapMode.MirroredRepeat,
                2 => TextureWrapMode.ClampToEdge,
                3 => TextureWrapMode.ClampToBorder,
                _ => TextureWrapMode.Repeat,
            };
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrap);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrap);
        }

        private void ApplyEmitterDepthState(VfxEmitterDefinition definition, bool isDistortion)
        {
            ModelMaterialDefinition customMaterial = definition.HasResolvedCustomMaterial ? definition.CustomMaterial : null;
            if (customMaterial is not null)
            {
                _gl.DepthMask(customMaterial.RenderState.DepthWrite);
                if (customMaterial.RenderState.DepthTest) _gl.Enable(EnableCap.DepthTest);
                else _gl.Disable(EnableCap.DepthTest);
            }
            else
            {
                var renderState = definition.RenderState ?? VfxEmitterRenderState.Default;
                bool writeDepth = !isDistortion && VfxBlendModes.ShouldWriteDepth(
                    definition.BlendMode,
                    renderState.AlphaReference);
                _gl.DepthMask(writeDepth);
                if (VfxBlendModes.ShouldTestDepth(definition.MiscRenderFlags)) _gl.Enable(EnableCap.DepthTest);
                else _gl.Disable(EnableCap.DepthTest);
            }

            // Three.js ShaderMaterial defaults to LessEqualDepth, and LTK never overrides it
            // for VFX materials. Keep equal-depth fragments eligible instead of inheriting
            // OpenGL's default LESS from the surrounding viewer.
            _gl.DepthFunc(DepthFunction.Lequal);

            Vector2? bias = ResolvePolygonOffset(definition);
            if (bias is { } polygonBias)
            {
                _gl.Enable(EnableCap.PolygonOffsetFill);
                _gl.PolygonOffset(polygonBias.X, polygonBias.Y);
            }
            else
            {
                _gl.Disable(EnableCap.PolygonOffsetFill);
            }
        }

        private void EnsureInstanceSortCapacity(int instanceCount, int floatCount)
        {
            if (_groupedInstances.Length < floatCount)
                _groupedInstances = new float[Math.Max(floatCount, Stride * 64)];
            if (_sortedInstances.Length < floatCount)
                _sortedInstances = new float[Math.Max(floatCount, Stride * 64)];
            if (_instanceDepths.Length < instanceCount)
                _instanceDepths = new float[Math.Max(instanceCount, 64)];
            if (_instanceOrder.Length < instanceCount)
                _instanceOrder = new int[Math.Max(instanceCount, 64)];
        }

        private void ApplyTextureSampling()
        {
            // LTK's VFX texture loader always uses linear filtering. isTexturePixelated is
            // inspector metadata in 1.19.6 and does not change the particle material sampler.
            _gl.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMinFilter,
                (int)TextureMinFilter.Linear);
            _gl.TexParameter(
                TextureTarget.Texture2D,
                TextureParameterName.TextureMagFilter,
                (int)TextureMagFilter.Linear);
        }

        private void ApplyWireframeBlend()
        {
            _gl.Enable(EnableCap.Blend);
            _gl.BlendEquationSeparate(GLEnum.FuncAdd, GLEnum.FuncAdd);
            _gl.BlendFuncSeparate(
                BlendingFactor.SrcAlpha,
                BlendingFactor.OneMinusSrcAlpha,
                BlendingFactor.One,
                BlendingFactor.OneMinusSrcAlpha);
        }

        private void ApplyEmitterBlendState(VfxEmitterDefinition definition, bool distortion = false)
        {
            if (definition.HasResolvedCustomMaterial)
            {
                ApplyCustomMaterialBlend(definition);
                return;
            }
            ApplyBlendMode(definition.BlendMode, distortion);
        }

        private void ApplyCustomMaterialBlend(VfxEmitterDefinition definition)
        {
            ModelMaterialRenderState state = definition.CustomMaterial.RenderState;
            if (state.Blending == ModelMaterialBlendMode.Opaque)
            {
                _gl.Disable(EnableCap.Blend);
                return;
            }

            _gl.Enable(EnableCap.Blend);
            _gl.BlendEquation(GLEnum.FuncAdd);
            _gl.BlendFunc(
                ToOpenGl(definition.CustomMaterialSourceBlendFactor),
                ToOpenGl(definition.CustomMaterialDestinationBlendFactor));
        }

        private void ApplyParticleCullState(VfxEmitterDefinition definition)
        {
            if (!definition.HasResolvedCustomMaterial)
            {
                // LTK's ordinary quad/ribbon particle materials are DoubleSide.
                _gl.Disable(EnableCap.CullFace);
                return;
            }
            ApplyCustomMaterialCull(definition.CustomMaterial.RenderState);
        }

        private void ApplyCustomMaterialCull(ModelMaterialRenderState state)
        {
            if (state.DoubleSided)
            {
                _gl.Disable(EnableCap.CullFace);
                return;
            }
            _gl.Enable(EnableCap.CullFace);
            _gl.CullFace(state.Inverted ? TriangleFace.Front : TriangleFace.Back);
        }

        private void ApplyCustomMaterialUniforms(
            ModelMaterialDefinition material,
            VfxShaderUniforms uniforms)
        {
            bool enabled = material is not null;
            uniforms.Uniform1(uniforms.UseCustomMaterial, enabled ? 1 : 0);
            Vector4 tint = material?.Color ?? Vector4.One;
            Vector2 repeat = material?.UvRepeat ?? Vector2.One;
            uniforms.Uniform4(uniforms.MaterialTint, tint.X, tint.Y, tint.Z, tint.W);
            uniforms.Uniform2(uniforms.MaterialRepeat, repeat.X, repeat.Y);
            uniforms.Uniform1(uniforms.MaterialAddressU, enabled ? (int)material.WrapU : 0);
            uniforms.Uniform1(uniforms.MaterialAddressV, enabled ? (int)material.WrapV : 0);
            uniforms.Uniform1(uniforms.MaterialPremultiplied, enabled && material.RenderState.PremultipliedAlpha ? 1 : 0);
        }

        internal static BlendingFactor ToOpenGl(VfxCustomMaterialBlendFactor factor) => factor switch
        {
            VfxCustomMaterialBlendFactor.Zero => BlendingFactor.Zero,
            VfxCustomMaterialBlendFactor.One => BlendingFactor.One,
            VfxCustomMaterialBlendFactor.SourceColor => BlendingFactor.SrcColor,
            VfxCustomMaterialBlendFactor.OneMinusSourceColor => BlendingFactor.OneMinusSrcColor,
            VfxCustomMaterialBlendFactor.DestinationColor => BlendingFactor.DstColor,
            VfxCustomMaterialBlendFactor.OneMinusDestinationColor => BlendingFactor.OneMinusDstColor,
            VfxCustomMaterialBlendFactor.SourceAlpha => BlendingFactor.SrcAlpha,
            VfxCustomMaterialBlendFactor.OneMinusSourceAlpha => BlendingFactor.OneMinusSrcAlpha,
            _ => throw new ArgumentOutOfRangeException(nameof(factor), factor, null)
        };

        private void ApplyBlendMode(int blendMode, bool distortion = false)
        {
            VfxBlendModeDescriptor descriptor = VfxBlendModes.GetDrawDescriptor(blendMode, distortion);
            if (descriptor.Kind == VfxBlendModeKind.Opaque)
            {
                _gl.Disable(EnableCap.Blend);
                return;
            }

            _gl.Enable(EnableCap.Blend);
            _gl.BlendEquationSeparate(
                ToOpenGl(descriptor.RgbEquation),
                ToOpenGl(descriptor.AlphaEquation));
            _gl.BlendFuncSeparate(
                ToOpenGl(descriptor.SourceRgb),
                ToOpenGl(descriptor.DestinationRgb),
                ToOpenGl(descriptor.SourceAlpha),
                ToOpenGl(descriptor.DestinationAlpha));
        }

        private static BlendingFactor ToOpenGl(VfxBlendFactor factor) => factor switch
        {
            VfxBlendFactor.Zero => BlendingFactor.Zero,
            VfxBlendFactor.One => BlendingFactor.One,
            VfxBlendFactor.SourceAlpha => BlendingFactor.SrcAlpha,
            VfxBlendFactor.OneMinusSourceAlpha => BlendingFactor.OneMinusSrcAlpha,
            VfxBlendFactor.DestinationColor => BlendingFactor.DstColor,
            VfxBlendFactor.OneMinusSourceColor => BlendingFactor.OneMinusSrcColor,
            VfxBlendFactor.DestinationAlpha => BlendingFactor.DstAlpha,
            VfxBlendFactor.OneMinusDestinationAlpha => BlendingFactor.OneMinusDstAlpha,
            _ => throw new ArgumentOutOfRangeException(nameof(factor), factor, null)
        };

        private static GLEnum ToOpenGl(VfxBlendEquationKind equation) => equation switch
        {
            VfxBlendEquationKind.Add => GLEnum.FuncAdd,
            VfxBlendEquationKind.Min => GLEnum.Min,
            VfxBlendEquationKind.Max => GLEnum.Max,
            _ => throw new ArgumentOutOfRangeException(nameof(equation), equation, null)
        };

        internal MapSunData Sun { get; set; }
    }
}
