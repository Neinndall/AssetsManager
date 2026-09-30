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
        private readonly Dictionary<uint, VfxShaderUniforms> _gameUniforms = new();
        private readonly Dictionary<string, Vector4> _particleParameters = new(StringComparer.Ordinal);

        internal (bool Depth, bool Color) SceneInputsFor(VfxEmitterDefinition emitter)
        {
            bool depth = ShouldUseSoftParticles(emitter, true);
            if (!emitter.HasResolvedCustomMaterial || _gameShaders == null) return (depth, false);
            var inputs = _gameShaders.ParticleSceneInputs(emitter, emitter.IsMeshPrimitive);
            return (depth || inputs.Depth, inputs.Color);
        }

        /// <summary>Why the emitter draws with the stock program instead of the game's: null when the game's program is used.</summary>
        internal string GameParticleFallback(VfxEmitterDefinition emitter, bool mesh) =>
            _gameShaders == null ? "Game shaders are unavailable." : _gameShaders.ParticleProgramFallback(emitter, mesh);

        private static bool HasParticleDraw(VfxPlaybackRuntime.EmitterState emitter) =>
            emitter.InstanceCount > 0 && emitter.IsVisible && (!emitter.Def.IsMeshPrimitive || emitter.MeshVao != 0);

        private int ParticlePassCount(VfxPlaybackRuntime.EmitterState emitter, bool mesh, bool wireframe) =>
            wireframe || !HasParticleDraw(emitter) ? 0 : _gameShaders?.GetParticlePassCount(emitter.Def, mesh) ?? 0;

        private bool ParticlePassTransparent(VfxPlaybackRuntime.EmitterState emitter, int pass, bool wireframe) =>
            HasParticleDraw(emitter) && IsParticlePassTransparent(emitter.Def, wireframe, emitter.Def.HasResolvedCustomMaterial
                ? _gameShaders?.GetParticlePassState(emitter.Def, emitter.Def.IsMeshPrimitive, pass)?.BlendEnabled
                : null);

        internal static bool IsParticlePassTransparent(VfxEmitterDefinition emitter, bool wireframe, bool? customBlend = null)
        {
            if (emitter.IsGroundLayer) return false;
            if (wireframe) return true;
            if (customBlend.HasValue) return customBlend.Value;
            return emitter.HasResolvedCustomMaterial ||
                VfxBlendModes.GetDrawDescriptor(emitter.BlendMode, emitter.DrawsAsDistortion).Kind != VfxBlendModeKind.Opaque;
        }

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
            VfxShaderParameterUtils.PopulateNativeParameters(_particleParameters, emitter.Def, phase);
            var frame = _gameFrame with { TimeSeconds = emitter.RenderTime };
            _gameShaders.BindParticle(emitter.Def, mesh, pass, frame, _particleParameters, name => ParticleTexture(emitter, name));
            // LTK quad/ribbon preludes draw in WORLD; mesh preludes draw in mirrored ENGINE_WORLD.
            bool mirrored = mesh && (emitter.Def.PrimitiveKind != VfxPrimitiveKind.AttachedMesh ||
                _ownerWorldTransform.GetDeterminant() < 0f);
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
