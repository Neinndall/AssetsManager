using System;
using System.Numerics;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>Stock Jade character shell shared by champion and MAP character hosts.</summary>
    internal sealed class CharacterOutlineRenderer : IDisposable
    {
        internal const string Vertex = @"
layout(location=0) in vec3 aPosition;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUv;
layout(location=5) in vec4 aSkinIndex;
layout(location=6) in vec4 aSkinWeight;
layout(std140) uniform BoneTransforms { mat4 uBones[512]; };
uniform mat4 uWorld;
uniform mat4 uViewProjection;
uniform vec3 uEye;
uniform int uSkin;
uniform float uWidth;
uniform float uCameraDistance;
uniform float uDepthOffset;
out vec2 vUv;
void main() {
    vec3 position = aPosition;
    vec3 normal = aNormal;
    if (uSkin != 0) {
        ivec4 joints = ivec4(aSkinIndex + vec4(0.5));
        mat4 skin = uBones[joints.x] * aSkinWeight.x + uBones[joints.y] * aSkinWeight.y
            + uBones[joints.z] * aSkinWeight.z + uBones[joints.w] * aSkinWeight.w;
        position = (skin * vec4(position, 1.0)).xyz;
        normal = mat3(skin) * normal;
    }
    vec4 world = uWorld * vec4(position, 1.0);
    vec3 worldNormal = normalize(mat3(uWorld) * normal);
    vec3 toCamera = uEye - world.xyz;
    float range = length(toCamera);
    world.xyz += toCamera / max(range, 1e-6) * uDepthOffset
        + worldNormal * (range / uCameraDistance * uWidth);
    vUv = aUv;
    gl_Position = uViewProjection * world;
}";
        internal const string Fragment = @"
in vec2 vUv;
uniform vec3 uColor;
uniform float uAlpha;
uniform sampler2D uMask;
uniform int uMaskOn;
uniform vec4 uMaskChannels;
out vec4 fragColor;
void main() {
    vec4 mask = uMaskOn != 0 ? texture(uMask, vUv) : vec4(1.0);
    float coverage = clamp(dot(mask, uMaskChannels) * uAlpha, 0.0, 1.0);
    if (coverage == 0.0) discard;
    fragColor = vec4(uColor, coverage);
}";
        private readonly GL _gl;
        private readonly uint _program, _sampler;
        private readonly int _world, _viewProjection, _eye, _skin, _width, _cameraDistance, _depthOffset, _color, _alpha, _maskOn, _maskChannels;

        internal CharacterOutlineRenderer(GL gl, bool gles)
        {
            _gl = gl;
            _program = GlShaderCompiler.CreateProgram(gl, gles, Vertex, Fragment);
            int U(string name) => gl.GetUniformLocation(_program, name);
            _world = U("uWorld"); _viewProjection = U("uViewProjection"); _eye = U("uEye"); _skin = U("uSkin");
            _width = U("uWidth"); _cameraDistance = U("uCameraDistance"); _depthOffset = U("uDepthOffset");
            _color = U("uColor"); _alpha = U("uAlpha"); _maskOn = U("uMaskOn"); _maskChannels = U("uMaskChannels");
            uint block = gl.GetUniformBlockIndex(_program, "BoneTransforms");
            if (block != uint.MaxValue) gl.UniformBlockBinding(_program, block, 0);
            gl.UseProgram(_program); gl.Uniform1(U("uMask"), 0);
            _sampler = gl.GenSampler();
            gl.SamplerParameter(_sampler, SamplerParameterI.MinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            gl.SamplerParameter(_sampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Linear);
            gl.SamplerParameter(_sampler, SamplerParameterI.WrapS, (int)TextureWrapMode.Repeat);
            gl.SamplerParameter(_sampler, SamplerParameterI.WrapT, (int)TextureWrapMode.Repeat);
        }

        internal void Bind(CharacterOutlineDefinition outline, Matrix4x4 world, Matrix4x4 viewProjection,
            Vector3 eye, bool skin, Func<string, uint?> texture)
        {
            _gl.UseProgram(_program);
            _gl.UniformMatrix4(_world, 1, false, in world.M11);
            _gl.UniformMatrix4(_viewProjection, 1, false, in viewProjection.M11);
            _gl.Uniform3(_eye, eye); _gl.Uniform1(_skin, skin ? 1 : 0);
            _gl.Uniform1(_width, outline.Width); _gl.Uniform1(_cameraDistance, outline.CameraDistance);
            _gl.Uniform1(_depthOffset, outline.DepthOffset); _gl.Uniform3(_color, outline.Color);
            _gl.Uniform1(_alpha, outline.Alpha); _gl.Uniform4(_maskChannels, outline.MaskChannels);
            _gl.ActiveTexture(TextureUnit.Texture0);
            uint mask = string.IsNullOrWhiteSpace(outline.MaskPath) ? 0 : texture(outline.MaskPath) ?? 0;
            _gl.BindTexture(TextureTarget.Texture2D, mask); _gl.BindSampler(0, _sampler);
            _gl.Uniform1(_maskOn, mask != 0 ? 1 : 0);
            _gl.Enable(EnableCap.Blend);
            _gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
            _gl.BlendEquation(BlendEquationModeEXT.FuncAdd);
            _gl.Disable(EnableCap.CullFace); _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Lequal); _gl.DepthMask(true);
        }

        public void Dispose() { _gl.DeleteSampler(_sampler); _gl.DeleteProgram(_program); }
    }
}
