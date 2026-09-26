using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    public sealed partial class VfxOpenGlRenderer
    {
        private GameShaderRuntime _gameShaders;
        private GameShaderRuntime.Frame _gameFrame;
        private readonly Dictionary<uint, VfxShaderUniforms> _gameUniforms = new();
        private readonly Dictionary<string, Vector4> _particleParameters = new(StringComparer.Ordinal);

        internal (bool Depth, bool Color) SceneInputsFor(VfxEmitterDefinition emitter)
        {
            bool depth = ShouldUseSoftParticles(emitter, true);
            if (!emitter.HasResolvedCustomMaterial || _gameShaders == null) return (depth, false);
            var inputs = _gameShaders.ParticleSceneInputs(emitter, emitter.IsMeshPrimitive);
            return (depth || inputs.Depth, inputs.Color);
        }

        private int ParticlePassCount(VfxPlaybackRuntime.EmitterState emitter, bool mesh, bool wireframe) =>
            wireframe ? 0 : _gameShaders?.GetParticlePassCount(emitter.Def, mesh) ?? 0;

        private bool UseGameParticle(VfxPlaybackRuntime.EmitterState emitter, bool mesh, int pass, bool wireframe)
        {
            uint program = wireframe ? 0 : _gameShaders?.UseParticleProgram(emitter.Def, mesh, pass) ?? 0;
            if (program == 0)
            {
                for (uint unit = 0; unit < 16; unit++) _gl.BindSampler(unit, 0);
                if (mesh) _meshUniforms = _stockMeshUniforms;
                else _particleUniforms = _stockParticleUniforms;
                _gl.UseProgram(mesh ? _meshProgram : _program);
                return false;
            }
            if (!_gameUniforms.TryGetValue(program, out var uniforms))
                _gameUniforms.Add(program, uniforms = new VfxShaderUniforms(_gl, program));
            if (mesh) _meshUniforms = uniforms;
            else
            {
                _particleUniforms = uniforms;
                Matrix4x4.Invert(_gameFrame.View, out var inverse);
                Vector3 right = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, inverse));
                Vector3 up = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, inverse));
                Matrix4x4 projection = _gameFrame.View * _gameFrame.Projection;
                _gl.UniformMatrix4(uniforms.ViewProj, 1, false, in projection.M11);
                _gl.Uniform3(uniforms.CamRight, right.X, right.Y, right.Z);
                _gl.Uniform3(uniforms.CamUp, up.X, up.Y, up.Z);
                _gl.Uniform3(uniforms.CamPos, _gameFrame.Eye.X, _gameFrame.Eye.Y, _gameFrame.Eye.Z);
            }
            _gl.Uniform1(uniforms.GamePremultiplied, !emitter.Def.HasResolvedCustomMaterial && !emitter.Def.DrawsAsDistortion && emitter.Def.BlendMode is 0 or 5 ? 1 : 0);
            return true;
        }

        private void BindGameParticle(VfxPlaybackRuntime.EmitterState emitter, bool mesh, int pass, float phase)
        {
            var uniforms = mesh ? _meshUniforms : _particleUniforms;
            _gl.Uniform1(uniforms.HasTexMult, HasTextureMultLayer(emitter.Def) ? 1 : 0);
            _particleParameters.Clear();
            _particleParameters["TEXTURE_INFO"] = new Vector4(1f, 1f, 1f, 0f);
            _particleParameters["TEXTURE_INFO_2"] = new Vector4(1f, 1f, 1f, 0f);
            _particleParameters["PARTICLE_DEPTH_PUSH_PULL"] = new Vector4(emitter.Def.DepthPushPull, 0f, 0f, 0f);
            if (!emitter.Def.HasResolvedCustomMaterial)
            {
                var erosion = emitter.Def.AlphaErosion;
                _particleParameters["AlphaTestReferenceValue"] = new Vector4((emitter.Def.RenderState?.AlphaReference ?? 0) / 255f, 0f, 0f, 0f);
                _particleParameters["cAlphaErosionParams"] = new Vector4(0f, erosion?.SliceWidth ?? 1.5f, 1f / Math.Max(erosion?.FeatherIn ?? 0f, 1e-4f), 1f / Math.Max(erosion?.FeatherOut ?? 0f, 1e-4f));
                _particleParameters["cAlphaErosionTextureMixer"] = erosion?.ChannelMixer?.Sample(phase) ?? Vector4.UnitW;
                var palette = emitter.Def.PaletteDefinition;
                _particleParameters["cPaletteSelectMain"] = new Vector4(PaletteSelectorAtZero(palette), 0f,
                    palette?.ScrollU?.Sample(phase) ?? 0f, palette?.ScrollV?.Sample(phase) ?? 0f);
                _particleParameters["cPaletteSrcMixerMain"] = palette?.PaletteSourceMixColor ?? Vector4.Zero;
                _particleParameters["kColorFactor"] = Vector4.One;
                _particleParameters["cSoftParticleParams"] = ResolveSoftParticleParams(emitter.Def.SoftParticle);
                _particleParameters["cSoftParticleControl"] = ResolveSoftParticleControl(emitter.Def.BlendMode);
                var reflection = emitter.Def.Reflection;
                _particleParameters["vFresnel"] = reflection == null ? new Vector4(0f, 0f, 0f, 1f) :
                    new Vector4(reflection.FresnelColor.X, reflection.FresnelColor.Y, reflection.FresnelColor.Z, reflection.Fresnel);
                _particleParameters["vReflection"] = reflection == null ? new Vector4(1f, 0f, 1f, 0f) :
                    new Vector4(reflection.ReflectionFresnel, reflection.DirectOpacity, reflection.GlancingOpacity, 0f);
                _particleParameters["vReflectionFColor"] = reflection?.ReflectionFresnelColor ?? Vector4.One;
                _particleParameters["DistortionPower"] = new Vector4(emitter.Def.Distortion?.Strength ?? 0f, 0f, 0f, 0f);
            }
            var frame = _gameFrame with { TimeSeconds = emitter.RenderTime };
            _gameShaders.BindParticle(emitter.Def, mesh, pass, frame, _particleParameters, name => ParticleTexture(emitter, name));
        }

        private uint? ParticleTexture(VfxPlaybackRuntime.EmitterState emitter, string name) => name switch
        {
            "TEXTURE" => emitter.Texture != 0 ? emitter.Texture : string.IsNullOrWhiteSpace(emitter.Def.TexturePath) ? null : _textures.FallbackTransparentTexture,
            "TEXTUREMULT" => emitter.TextureMult != 0 ? emitter.TextureMult : null,
            "PARTICLE_COLOR_TEXTURE" => emitter.ColorGradientTexture != 0 ? emitter.ColorGradientTexture : null,
            "sPalettesTexture" => emitter.PaletteTexture != 0 ? emitter.PaletteTexture : null,
            "sAlphaErosionTexture" => emitter.ErosionTexture != 0 ? emitter.ErosionTexture :
                !string.IsNullOrWhiteSpace(emitter.Def.AlphaErosion?.TexturePath) ? _textures.FallbackTransparentTexture : null,
            "NORMAL_MAP" => emitter.DistortionTexture != 0 ? emitter.DistortionTexture : _textures.FallbackTransparentTexture,
            "REFLECTION_MAP_TX" => emitter.ReflectionTexture != 0 ? emitter.ReflectionTexture : null,
            "sDepthTexture_SharedTexture" => _capture.DepthTexture != 0 ? _capture.DepthTexture : null,
            "SAMPLER_BACK_BUFFER_COPY_SharedTexture" => _capture.ColorTexture != 0 ? _capture.ColorTexture : null,
            _ => emitter.ProgramTextures.TryGetValue(name, out uint texture) && texture != 0 ? texture : null
        };
    }
}
