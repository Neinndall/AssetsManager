using System;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>Shared data-texture and lighting bindings for champion and MAP character PBR previews.</summary>
    internal sealed class CharacterPbrBindings : IDisposable
    {
        private readonly GL _gl;
        private readonly int _on, _normalOn, _camera, _direction, _light, _direction2, _light2, _sky, _ground, _ambient;
        private readonly uint _dfg, _sampler;
        private readonly int _view;
        private readonly int _orthographic;
        internal Matrix4x4 View { get; set; } = Matrix4x4.Identity;
        internal Matrix4x4 Projection { get; set; } = Matrix4x4.Identity;

        internal CharacterPbrBindings(GL gl, uint program)
        {
            _gl = gl;
            int Location(string name) => gl.GetUniformLocation(program, name);
            _on = Location("uPbrOn"); _normalOn = Location("uPbrNormalOn"); _camera = Location("uPbrCamera");
            _direction = Location("uPbrLightDirection"); _light = Location("uPbrLightColor");
            _direction2 = Location("uPbrLightDirection2"); _light2 = Location("uPbrLightColor2");
            _sky = Location("uPbrSky"); _ground = Location("uPbrGround"); _ambient = Location("uPbrAmbientStrength");
            _view = Location("uPbrView");
            _orthographic = Location("uPbrOrthographic");
            gl.UseProgram(program);
            gl.Uniform1(Location("uPbrRma"), 1); gl.Uniform1(Location("uPbrNormal"), 2); gl.Uniform1(Location("uPbrDfg"), 3);
            _dfg = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, _dfg);
            gl.TexImage2D(TextureTarget.Texture2D, 0, (InternalFormat)0x822F /* GL_RG16F */, 16, 16, 0,
                PixelFormat.RG, PixelType.HalfFloat, CharacterPbrDfg.Data);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _sampler = gl.GenSampler();
            gl.SamplerParameter(_sampler, SamplerParameterI.MinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            gl.SamplerParameter(_sampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Linear);
            gl.SamplerParameter(_sampler, SamplerParameterI.WrapS, (int)TextureWrapMode.Repeat);
            gl.SamplerParameter(_sampler, SamplerParameterI.WrapT, (int)TextureWrapMode.Repeat);
            gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        internal void Light(Vector3 camera, Vector3 direction, Vector3 light, Vector3 direction2,
            Vector3 light2, Vector3 sky, Vector3 ground, float ambientStrength)
        {
            _gl.Uniform3(_camera, camera); _gl.Uniform3(_direction, direction); _gl.Uniform3(_light, light);
            _gl.Uniform3(_direction2, direction2); _gl.Uniform3(_light2, light2);
            _gl.Uniform3(_sky, sky); _gl.Uniform3(_ground, ground); _gl.Uniform1(_ambient, ambientStrength);
            Matrix4x4 view = View;
            _gl.UniformMatrix4(_view, 1, false, in view.M11);
            _gl.Uniform1(_orthographic, MathF.Abs(Projection.M44) > 1e-6f ? 1 : 0);
        }

        internal void Bind(ModelMaterialDefinition material, Func<string, uint?> texture, bool tangents, bool enabled)
        {
            CharacterPbrMaps maps = enabled && material?.BindingKind == ModelMaterialBindingKind.TextureOnly ? material.Pbr : null;
            if (maps == null)
            {
                _gl.Uniform1(_on, 0);
                return;
            }
            _gl.ActiveTexture(TextureUnit.Texture0);
            uint baseTexture = (uint)_gl.GetInteger(GetPName.TextureBinding2D);
            uint Resolve(System.Collections.Generic.IReadOnlyList<string> paths, string fallback)
            {
                if (paths != null)
                    foreach (string path in paths)
                        if (texture(path) is uint held && held != 0) return held;
                return string.IsNullOrWhiteSpace(fallback) ? 0 : texture(fallback) ?? 0;
            }
            uint rma = Resolve(maps.RmaPaths, maps.Rma);
            uint normal = rma != 0 && tangents ? Resolve(maps.NormalPaths, maps.Normal) : 0;
            _gl.Uniform1(_on, rma != 0 ? 1 : 0);
            _gl.Uniform1(_normalOn, normal != 0 ? 1 : 0);
            for (uint unit = 1; unit <= 3; unit++)
            {
                _gl.ActiveTexture((TextureUnit)((uint)TextureUnit.Texture0 + unit));
                _gl.BindTexture(TextureTarget.Texture2D, unit == 1 ? rma : unit == 2 ? normal : _dfg);
                _gl.BindSampler(unit, unit == 3 ? 0 : _sampler);
            }
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, baseTexture);
        }

        internal void Reset()
        {
            for (uint unit = 1; unit <= 3; unit++)
            {
                _gl.ActiveTexture((TextureUnit)((uint)TextureUnit.Texture0 + unit));
                _gl.BindTexture(TextureTarget.Texture2D, 0); _gl.BindSampler(unit, 0);
            }
            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        public void Dispose() { _gl.DeleteTexture(_dfg); _gl.DeleteSampler(_sampler); }
    }
}
