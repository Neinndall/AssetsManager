using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    public sealed partial class VfxOpenGlRenderer
    {
        private GameShaderRuntime _gameShaders;
        private GameShaderRuntime.Frame _gameFrame;
        private Matrix4x4 _gameViewProjection;
        private Vector3 _gameCameraRight;
        private Vector3 _gameCameraUp;
        private readonly HashSet<uint> _cameraPrograms = new();
        private readonly Dictionary<uint, VfxShaderUniforms> _gameUniforms = new();
        private readonly Dictionary<string, Vector4> _particleParameters = new(StringComparer.Ordinal);

        internal (bool Depth, bool Color) SceneInputsFor(VfxEmitterDefinition emitter)
        {
            if (emitter.DrawsAsProjection) return (false, false);
            bool depth = ShouldUseSoftParticles(emitter, true);
            if (!emitter.HasResolvedCustomMaterial || _gameShaders == null) return (depth, false);
            var inputs = _gameShaders.ParticleSceneInputs(emitter, emitter.IsMeshPrimitive);
            return (depth || inputs.Depth, inputs.Color);
        }

        /// <summary>Shader fallback from authored data; live resource readiness is checked by the state overload.</summary>
        internal string GameParticleFallback(VfxEmitterDefinition emitter, bool mesh) =>
            _gameShaders == null ? "Game shaders are unavailable." : _gameShaders.ParticleProgramFallback(emitter, mesh);

        internal string GameParticleFallback(VfxPlaybackRuntime.EmitterState emitter, bool mesh) =>
            GameParticleFallback(emitter.Def, mesh) ??
            (HasGameParticleResources(emitter) ? null : "Palette texture is unavailable.");

        private static bool HasGameParticleResources(VfxPlaybackRuntime.EmitterState emitter) =>
            emitter.Def.HasResolvedCustomMaterial || emitter.Def.PaletteDefinition is null || emitter.PaletteTexture != 0;

        private static bool HasParticleDraw(VfxPlaybackRuntime.EmitterState emitter) =>
            emitter.InstanceCount > 0 && emitter.IsVisible && (!emitter.Def.IsMeshPrimitive || emitter.MeshVao != 0);

        private int ParticlePassCount(VfxPlaybackRuntime.EmitterState emitter, bool mesh, bool wireframe) =>
            wireframe || emitter.Def.DrawsAsProjection || !HasParticleDraw(emitter) || !HasGameParticleResources(emitter)
                ? 0 : _gameShaders?.GetParticlePassCount(emitter.Def, mesh) ?? 0;

        private bool ParticlePassTransparent(VfxPlaybackRuntime.EmitterState emitter, int pass, bool wireframe) =>
            HasParticleDraw(emitter) && IsParticlePassTransparent(emitter.Def, wireframe, !emitter.Def.DrawsAsProjection && emitter.Def.HasResolvedCustomMaterial
                ? _gameShaders?.GetParticlePassState(emitter.Def, emitter.Def.IsMeshPrimitive, pass)?.BlendEnabled
                : null);

        internal static bool IsParticlePassTransparent(VfxEmitterDefinition emitter, bool wireframe, bool? customBlend = null)
        {
            if (emitter.IsGroundLayer) return false;
            if (wireframe) return true;
            if (emitter.DrawsAsProjection) return VfxBlendModes.GetDrawDescriptor(emitter.BlendMode, false).Kind != VfxBlendModeKind.Opaque;
            if (customBlend.HasValue) return customBlend.Value;
            return emitter.HasResolvedCustomMaterial ||
                VfxBlendModes.GetDrawDescriptor(emitter.BlendMode, emitter.DrawsAsDistortion).Kind != VfxBlendModeKind.Opaque;
        }

        private bool UseGameParticle(VfxPlaybackRuntime.EmitterState emitter, bool mesh, int pass, bool wireframe)
        {
            // A palettized native pass cannot use a white substitute while its palette uploads or is missing.
            uint program = wireframe || !HasGameParticleResources(emitter)
                ? 0 : _gameShaders?.UseParticleProgram(emitter.Def, mesh, pass) ?? 0;
            if (program == 0)
            {
                for (uint unit = 0; unit < 16; unit++) _gl.BindSampler(unit, 0);
                if (mesh) _meshUniforms = _stockMeshUniforms;
                else _particleUniforms = _stockParticleUniforms;
                _drawBindings.UseProgram(mesh ? _meshProgram : _program);
                return false;
            }
            if (!_gameUniforms.TryGetValue(program, out var uniforms))
                _gameUniforms.Add(program, uniforms = new VfxShaderUniforms(_gl, program));
            if (mesh) _meshUniforms = uniforms;
            else
            {
                _particleUniforms = uniforms;
                // Each linked program keeps its camera uniforms while other emitters draw.
                if (_cameraPrograms.Add(program))
                {
                    _gl.UniformMatrix4(uniforms.ViewProj, 1, false, in _gameViewProjection.M11);
                    uniforms.Uniform3(uniforms.CamRight, _gameCameraRight.X, _gameCameraRight.Y, _gameCameraRight.Z);
                    uniforms.Uniform3(uniforms.CamUp, _gameCameraUp.X, _gameCameraUp.Y, _gameCameraUp.Z);
                    uniforms.Uniform3(uniforms.CamPos, _gameFrame.Eye.X, _gameFrame.Eye.Y, _gameFrame.Eye.Z);
                }
            }
            uniforms.Uniform1(uniforms.GamePremultiplied, !emitter.Def.HasResolvedCustomMaterial && !emitter.Def.DrawsAsDistortion && emitter.Def.BlendMode is 0 or 2 ? 1 : 0);
            return true;
        }

        private void BindGameParticle(VfxPlaybackRuntime.EmitterState emitter, bool mesh, int pass, float phase)
        {
            var uniforms = mesh ? _meshUniforms : _particleUniforms;
            uniforms.Uniform1(uniforms.HasTexMult, HasTextureMultLayer(emitter.Def) ? 1 : 0);
            _particleParameters.Clear();
            VfxShaderParameterUtils.PopulateNativeParameters(_particleParameters, emitter.Def, phase);
            var frame = _gameFrame with { TimeSeconds = emitter.RenderTime };
            _gameShaders.BindParticle(emitter.Def, mesh, pass, frame, _particleParameters, name => ParticleTexture(emitter, name));
            // LTK quad/ribbon preludes draw in WORLD; mesh preludes draw in mirrored ENGINE_WORLD.
            bool mirrored = mesh && (emitter.Def.PrimitiveKind != VfxPrimitiveKind.AttachedMesh ||
                _ownerWorldTransform.GetDeterminant() < 0f);
            if (mesh && emitter.Def.RenderState?.FlipWinding == true) mirrored = !mirrored;
            _gl.FrontFace(mirrored ? FrontFaceDirection.CW : FrontFaceDirection.Ccw);
        }

        private uint? ParticleTexture(VfxPlaybackRuntime.EmitterState emitter, string name) => name switch
        {
            // An emitter naming no texture binds the engine's 1x1 transparent black, as does one whose texture failed to
            // load: it draws nothing itself and only carries its children (Evelynn W mark's Start_flash). A custom
            // material keeps its own sampler neutrals.
            "TEXTURE" => emitter.Texture != 0 ? emitter.Texture : emitter.Def.HasResolvedCustomMaterial ? null : _textures.FallbackTransparentTexture,
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
