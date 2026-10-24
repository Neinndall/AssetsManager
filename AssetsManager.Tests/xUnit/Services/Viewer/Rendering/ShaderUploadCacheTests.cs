using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using AssetsManager.Shaders;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    [Collection("Viewport native graphics")]
    public sealed class ShaderUploadCacheTests
    {
        [Fact]
        public void MapUniformUploadsKeepInactiveGrassSpheresAwayAndTerrainCoordinatesInBothStages()
        {
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            using var runtime = new GameShaderRuntime(gl, false, null);
            uint program = GlShaderCompiler.CreateProgram(gl, false,
                "void main() { gl_Position = vec4(0.0); }",
                "out vec4 result; void main() { result = vec4(1.0); }");
            Type runtimeType = typeof(GameShaderRuntime);
            const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            Type blockType = runtimeType.GetNestedType("BlockRuntime", BindingFlags.NonPublic);
            Type programType = runtimeType.GetNestedType("ProgramRuntime", BindingFlags.NonPublic);
            Type samplerType = runtimeType.GetNestedType("SamplerRuntime", BindingFlags.NonPublic);
            (string Name, int Floats)[] definitions =
                { ("GrassDisortVS", 80), ("PerFrameVertexCB", 136), ("PerFramePixelCB", 144) };
            Array blocks = Array.CreateInstance(blockType, definitions.Length);
            uint[] buffers = new uint[definitions.Length];
            try
            {
                for (int i = 0; i < definitions.Length; i++)
                {
                    object block = Activator.CreateInstance(blockType, true);
                    var definition = definitions[i];
                    blockType.GetField("Block", fields).SetValue(block,
                        new GameShaderTranslator.UniformBlock(definition.Name, definition.Name,
                            (uint)definition.Floats * 4, Array.Empty<GameShaderTranslator.BlockMember>()));
                    buffers[i] = gl.GenBuffer();
                    blockType.GetField("Buffer", fields).SetValue(block, buffers[i]);
                    blockType.GetField("Binding", fields).SetValue(block, (uint)i + 2);
                    blockType.GetField("Data", fields).SetValue(block, Enumerable.Repeat(float.NaN, definition.Floats).ToArray());
                    blockType.GetField("UploadedData", fields).SetValue(block, new float[definition.Floats]);
                    blocks.SetValue(block, i);
                }
                object linked = Activator.CreateInstance(programType, fields, null,
                    new object[] { gl, program, blocks, Array.CreateInstance(samplerType, 0), new Dictionary<uint, string>() }, null);
                var terrain = new MapTerrainData(new Vector2(100f, 200f), new Vector2(1100f, 2200f),
                    Array.Empty<MapSkinEnvironmentData>(), new Dictionary<int, float>());
                var frame = new GameShaderRuntime.Frame(Matrix4x4.Identity, Matrix4x4.Identity, Vector3.Zero, 0f, null,
                    Environment: new GameShaderRuntime.EnvironmentFrame(terrain.TerrainTransform, 0, 0, 1f));
                MethodInfo update = runtimeType.GetMethod("UpdateBlocks", fields);
                // Repeat to cover both the first upload and the unchanged-buffer cache path.
                for (int pass = 0; pass < 2; pass++)
                {
                    gl.UseProgram(program);
                    update.Invoke(runtime, new object[] { linked, null, null, frame, null, null, null });
                    for (int i = 0; i < definitions.Length; i++)
                    {
                        var uploaded = new float[definitions[i].Floats];
                        gl.BindBuffer(BufferTargetARB.UniformBuffer, buffers[i]);
                        gl.GetBufferSubData<float>(BufferTargetARB.UniformBuffer, 0,
                            (nuint)(uploaded.Length * sizeof(float)), uploaded);
                        if (i == 0)
                        {
                            for (int sphere = 0; sphere < 10; sphere++)
                            {
                                int at = sphere * 4;
                                Assert.Equal(new[] { 1e6f, 1e6f, 1e6f, 0f }, uploaded.Skip(at).Take(4));
                                Vector3 delta = -new Vector3(uploaded[at], uploaded[at + 1], uploaded[at + 2]);
                                Assert.True(float.IsFinite(1f / delta.Length()));
                            }
                            Assert.All(uploaded.Skip(40), value => Assert.Equal(0f, value));
                        }
                        else
                        {
                            int at = i == 1 ? 24 : 8;
                            Assert.Equal(new[] { terrain.TerrainTransform.X, terrain.TerrainTransform.Y,
                                terrain.TerrainTransform.Z, terrain.TerrainTransform.W }, uploaded.Skip(at).Take(4));
                        }
                    }
                }
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
                gl.UseProgram(0);
                foreach (uint buffer in buffers) if (buffer != 0) gl.DeleteBuffer(buffer);
                gl.DeleteProgram(program);
            }
        }

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
