using System;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    public sealed class ShaderUploadCacheTests
    {
        [Fact]
        public void ConstantBufferComparisonPreservesBitPatternsAndLength()
        {
            float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
            float nan = BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00001));
            float otherNan = BitConverter.Int32BitsToSingle(unchecked((int)0x7fc00002));
            Assert.False(GameShaderRuntime.BlockDataMatches(new[] { 0f }, new[] { negativeZero }));
            Assert.False(GameShaderRuntime.BlockDataMatches(new[] { nan }, new[] { otherNan }));
            Assert.True(GameShaderRuntime.BlockDataMatches(new[] { nan, negativeZero }, new[] { nan, negativeZero }));
            Assert.False(GameShaderRuntime.BlockDataMatches(new[] { 1f }, new[] { 1f, 0f }));
        }

        [Fact]
        public void UniformCacheSkipsIdenticalWritesAndKeepsProgramStateIndependent()
        {
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            const string vertex = "void main() { gl_Position = vec4(0.0, 0.0, 0.0, 1.0); }";
            const string fragment = @"uniform float uAlphaCutoff;
uniform int uPrimitiveKind;
uniform vec2 uTexDiv;
uniform vec3 uCamPos;
uniform vec4 uColor;
out vec4 result;
void main() { result = uColor + vec4(uCamPos, uAlphaCutoff) + vec4(uTexDiv, float(uPrimitiveKind), 0.0); }";
            uint first = GlShaderCompiler.CreateProgram(gl, false, vertex, fragment);
            uint second = GlShaderCompiler.CreateProgram(gl, false, vertex, fragment);
            try
            {
                var a = new VfxShaderUniforms(gl, first);
                var b = new VfxShaderUniforms(gl, second);
                gl.UseProgram(first);
                a.Uniform1(-1, 99);
                Assert.Equal(0, a.UploadCount);
                a.Uniform1(a.AlphaCutoff, 0f);
                a.Uniform1(a.AlphaCutoff, 0f);
                Assert.Equal(1, a.UploadCount);
                float negativeZero = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
                a.Uniform1(a.AlphaCutoff, negativeZero);
                Assert.Equal(2, a.UploadCount);
                a.Uniform1(a.PrimitiveKind, 7);
                a.Uniform2(a.TexDiv, 2, 3);
                a.Uniform3(a.CamPos, 4, 5, 6);
                a.Uniform4(a.Color, 7, 8, 9, 10);
                int uploads = a.UploadCount;

                gl.UseProgram(second);
                b.Uniform1(b.AlphaCutoff, 12f);
                gl.UseProgram(first);
                a.Uniform1(a.PrimitiveKind, 7);
                a.Uniform2(a.TexDiv, 2, 3);
                a.Uniform3(a.CamPos, 4, 5, 6);
                a.Uniform4(a.Color, 7, 8, 9, 10);
                Assert.Equal(uploads, a.UploadCount);
                gl.GetUniform(first, a.AlphaCutoff, out float actual);
                Assert.Equal(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(actual));
                gl.GetUniform(second, b.AlphaCutoff, out float other);
                Assert.Equal(12f, other);
                a.Uniform4(a.Color, 7, 8, 9, 11);
                Assert.Equal(uploads + 1, a.UploadCount);
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.UseProgram(0);
                gl.DeleteProgram(first);
                gl.DeleteProgram(second);
            }
        }
    }
}
