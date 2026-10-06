using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Shaders;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    [Collection("Viewport native graphics")]
    public sealed class VfxMeshUvGpuTests
    {
        public static IEnumerable<object[]> UvCases()
        {
            foreach (VfxPrimitiveKind primitive in new[] { VfxPrimitiveKind.Mesh, VfxPrimitiveKind.AttachedMesh })
            for (int mode = 0; mode < 4; mode++)
            foreach (bool mult in new[] { false, true })
            for (int flips = 0; flips < 4; flips++)
            foreach (bool perspective in new[] { false, true })
            foreach (bool atlas in new[] { false, true })
                yield return new object[] { primitive, mode, mult, flips, perspective, atlas };
        }

        [Theory]
        [MemberData(nameof(UvCases))]
        public void NativeMeshKeepsTheAffineUvMapping(
            VfxPrimitiveKind primitive, int mode, bool mult, int flips, bool perspective, bool atlas)
        {
            string install = InstalledSkins.FindInstall();
            if (install == null) return;
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            using var renderer = new VfxOpenGlRenderer();
            renderer.Initialize(gl, InstalledSkins.Settings(install));
            uint target = Texture(gl, 64, 64, new byte[64 * 64 * 4]);
            uint framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            uint white = Texture(gl, 1, 1, new byte[] { 255, 255, 255, 255 });
            var texels = new byte[64 * 64 * 4];
            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int at = (y * 64 + x) * 4;
                texels[at] = (byte)(4 * x + 2);
                texels[at + 1] = (byte)(4 * y + 2);
                texels[at + 2] = 128;
                texels[at + 3] = (byte)(128 + 2 * x);
            }
            uint gradient = Texture(gl, 64, 64, texels);
            try
            {
                var center = new Vector2(0.23f, 0.61f);
                var scale = new Vector2(0.6f, 0.8f);
                var offset = new Vector2(0.18f, 0.12f);
                const float rotation = 0.3f;
                Vector2 divisions = atlas ? new Vector2(2, 3) : Vector2.One;
                const float frame = 3;
                var definition = new VfxEmitterDefinition("affine", VfxCurveF.Const(1), VfxCurveF.Const(1),
                    null, 0, 0, false, false, 3, VfxCurve3.Const(Vector3.One), null,
                    VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                    "base.tex", divisions, 6, false, true, PrimitiveKind: primitive, UvMode: (byte)mode,
                    RenderState: VfxEmitterRenderState.Default with
                    {
                        DisableBackfaceCull = true, TextureAddressMode = 2,
                        FlipU = !mult && (flips & 1) != 0, FlipV = !mult && (flips & 2) != 0
                    },
                    UvTransformCenter: center,
                    TextureMultPath: mult ? "mult.tex" : null,
                    TextureMultTexDiv: divisions, TextureMultAddressMode: 2,
                    TextureMultTransformCenter: center,
                    TextureMultFlipU: mult && (flips & 1) != 0,
                    TextureMultFlipV: mult && (flips & 2) != 0);
                var instance = new float[VfxPlaybackRuntime.InstanceStride];
                instance[3] = instance[4] = instance[18] = 1;
                instance[5] = instance[6] = instance[7] = instance[8] = 1;
                instance[10] = frame;
                instance[19] = instance[29] = offset.X;
                instance[20] = instance[30] = offset.Y;
                instance[21] = instance[31] = scale.X;
                instance[22] = instance[32] = scale.Y;
                instance[23] = instance[33] = rotation;
                instance[36] = instance[40] = instance[44] = 1;
                var emitter = new VfxPlaybackRuntime.EmitterState
                {
                    Def = definition, Texture = mult ? white : gradient, TextureMult = mult ? gradient : 0,
                    Instances = instance, InstanceCount = 1
                };
                // Depth varies across the plane so perspective interpolation is exercised.
                renderer.UploadEmitterMesh(emitter,
                    new float[] { -1, -1, -0.35f, 1, -1, 0.15f, 1, 1, 0.35f,
                                  -1, -1, -0.35f, 1, 1, 0.35f, -1, 1, -0.15f },
                    new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                    new float[] { 0, 0, 1, 0, 1, 1, 0, 0, 1, 1, 0, 1 }, null);
                var eye = new Vector3(0, 0, 3);
                Matrix4x4 view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
                Matrix4x4 projection = perspective
                    ? Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 0.1f, 10)
                    : Matrix4x4.CreateOrthographic(4, 4, 0.1f, 10);
                Matrix4x4 viewProjection = view * projection;
                Assert.True(Matrix4x4.Invert(viewProjection, out Matrix4x4 inverse));
                gl.Viewport(0, 0, 64, 64);
                gl.ClearColor(0, 0, 0, 0);
                gl.Clear(ClearBufferMask.ColorBufferBit);
                renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, viewProjection, view);
                Assert.Null(renderer.GameParticleFallback(emitter, true));
                var pixels = new byte[64 * 64 * 4];
                gl.ReadPixels(0, 0, 64, 64, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
                for (int y = 28; y <= 36; y += 4)
                for (int x = 28; x <= 36; x += 4)
                {
                    var ndc = new Vector2((x + 0.5f) / 32 - 1, (y + 0.5f) / 32 - 1);
                    Vector3 near = Unproject(ndc, 0, inverse);
                    Vector3 far = Unproject(ndc, 1, inverse);
                    var normal = new Vector3(-0.25f, -0.1f, 1);
                    Vector3 world = near + (far - near) * (-Vector3.Dot(near, normal) / Vector3.Dot(far - near, normal));
                    var meshUv = new Vector2(world.X, world.Y) * 0.5f + new Vector2(0.5f);
                    // The original SCREEN_SPACE_UV shader applies its rows to clip.xy / clip.w.
                    // Its mult layer retains mesh UVs; SEPARATE_ALPHA_UV uses only the linear rows.
                    Vector2 input = mode == 1 && !mult ? ndc : meshUv;
                    Vector2 turned = Turn((input - center) * scale, rotation);
                    Vector2 local = turned + center + offset;
                    if ((flips & 1) != 0) local.X = 1 - local.X;
                    if ((flips & 2) != 0) local.Y = 1 - local.Y;
                    float cell = frame % (divisions.X * divisions.Y);
                    Vector2 uv = (local + new Vector2(cell % divisions.X, MathF.Floor(cell / divisions.X))) / divisions;
                    Vector2 clamped = Vector2.Clamp(uv, new Vector2(0.5f / 64), new Vector2(63.5f / 64));
                    float alphaU = clamped.X;
                    if (mode == 2 && !mult)
                    {
                        Vector2 alphaUv = Turn(meshUv * scale, rotation);
                        if ((flips & 1) != 0) alphaUv.X = -alphaUv.X;
                        alphaU = Math.Clamp(alphaUv.X / divisions.X, 0.5f / 64, 63.5f / 64);
                    }
                    float[] expected = { clamped.X * 256, clamped.Y * 256, 128, 127 + 128 * alphaU };
                    int at = (y * 64 + x) * 4;
                    for (int channel = 0; channel < 4; channel++)
                        Assert.True(Math.Abs(pixels[at + channel] - expected[channel]) <= 2,
                            $"{primitive}, uv={mode}, mult={mult}, flips={flips}, perspective={perspective}, atlas={atlas}, " +
                            $"pixel=({x},{y}), channel={channel}: expected {expected[channel]}, got {pixels[at + channel]}.");
                }
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(framebuffer);
                foreach (uint texture in new[] { target, white, gradient }) gl.DeleteTexture(texture);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("vParticleUVTransform")]
        [InlineData("vParticleUVTransformMult")]
        public void CustomMeshAppliesItsUvTransformExactlyOnce(string member)
        {
            const string preamble = "#version 300 es\nprecision highp float;\nprecision highp int;\n";
            var blocks = member == null ? Array.Empty<GameShaderTranslator.UniformBlock>() :
                new[] { new GameShaderTranslator.UniformBlock("TestUv", "TestUv_vs", 32,
                    new[] { new GameShaderTranslator.BlockMember(member, 0, 32, true,
                        GameShaderTranslator.MemberScalar.Float, 2, 4, 1, true) }) };
            var vertexSidecar = new GameShaderTranslator.ShaderSidecar(blocks,
                Array.Empty<GameShaderTranslator.TextureBinding>(), Array.Empty<GameShaderTranslator.AttributeBinding>());
            var pixelSidecar = vertexSidecar with { Blocks = Array.Empty<GameShaderTranslator.UniformBlock>() };
            string vertex = preamble + "in vec3 a_POSITION;\nin vec4 a_TEXCOORD;\nout vec2 uv;\n" +
                (member == null ? string.Empty : "layout(std140) uniform TestUv_vs { vec4 m[2]; } TestUv_i;\n") +
                "void main(){ gl_Position = vec4(a_POSITION, 1.0); uv = " +
                (member == null ? "a_TEXCOORD.xy" :
                    "vec2(dot(TestUv_i.m[0].xyz, vec3(a_TEXCOORD.xy, 1.0)), dot(TestUv_i.m[1].xyz, vec3(a_TEXCOORD.xy, 1.0)))") + "; }\n";
            string pixel = preamble + "in vec2 uv;\nout vec4 color;\nvoid main(){ color = vec4(uv, 0.0, 1.0); }\n";
            GameShaderTranslator.TranslatedStage Stage(string source, GameShaderTranslator.ShaderSidecar sidecar) =>
                new(source, sidecar, Array.Empty<GameShaderTranslator.AppliedPatch>(),
                    Array.Empty<(string, string)>(), Array.Empty<uint>());
            var composed = GameParticleShaderPrelude.Compose(
                new GameShaderTranslator.TranslatedProgram(Stage(vertex, vertexSidecar), Stage(pixel, pixelSidecar)), true);
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            uint program = GlShaderCompiler.CreateRawProgram(gl, composed.Vertex.Glsl, composed.Pixel.Glsl);
            uint target = Texture(gl, 64, 64, new byte[64 * 64 * 4]);
            uint framebuffer = gl.GenFramebuffer();
            uint vao = gl.GenVertexArray();
            uint buffer = gl.GenBuffer();
            try
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                    TextureTarget.Texture2D, target, 0);
                Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
                gl.BindVertexArray(vao);
                gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
                gl.BufferData(BufferTargetARB.ArrayBuffer,
                    new ReadOnlySpan<float>(new float[] { -0.75f, -0.75f, 0.2f, 0, 0,
                        0.75f, -0.75f, 0.2f, 1, 0, 0, 0.75f, 0.2f, 0.5f, 1 }), BufferUsageARB.StaticDraw);
                gl.EnableVertexAttribArray(0);
                gl.EnableVertexAttribArray(1);
                gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 5 * sizeof(float), IntPtr.Zero);
                gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 5 * sizeof(float), new IntPtr(3 * sizeof(float)));
                gl.UseProgram(program);
                gl.Uniform3(gl.GetUniformLocation(program, "uScale"), 1f, 1f, 1f);
                gl.Uniform3(gl.GetUniformLocation(program, "uPlacementRight"), 1f, 0f, 0f);
                gl.Uniform3(gl.GetUniformLocation(program, "uPlacementUp"), 0f, 1f, 0f);
                gl.Uniform3(gl.GetUniformLocation(program, "uPlacementForward"), 0f, 0f, 1f);
                foreach (string suffix in new[] { "", "Mult" })
                {
                    gl.Uniform2(gl.GetUniformLocation(program, "uTexDiv" + suffix), 1f, 1f);
                    gl.Uniform2(gl.GetUniformLocation(program, "uUvScale" + suffix), 0.6f, 0.8f);
                    gl.Uniform1(gl.GetUniformLocation(program, "uUvRotation" + suffix), 0.3f);
                    gl.Uniform2(gl.GetUniformLocation(program, "uUvTransformCenter" + suffix), 0.23f, 0.61f);
                }
                gl.Uniform2(gl.GetUniformLocation(program, "uBirthUvOffset"), 0.18f, 0.12f);
                gl.Uniform2(gl.GetUniformLocation(program, "uUvOffsetMult"), 0.18f, 0.12f);
                gl.Viewport(0, 0, 64, 64);
                gl.ClearColor(0, 0, 0, 0);
                gl.Clear(ClearBufferMask.ColorBufferBit);
                gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
                var result = new byte[4];
                gl.ReadPixels(32, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, result.AsSpan());
                var raw = new Vector2(0.5f + (1f / 64) / 1.5f);
                Vector2 expected = Turn((raw - new Vector2(0.23f, 0.61f)) * new Vector2(0.6f, 0.8f), 0.3f) +
                    new Vector2(0.23f, 0.61f) + new Vector2(0.18f, 0.12f);
                Assert.InRange(Math.Abs(result[0] - expected.X * 255), 0, 2);
                Assert.InRange(Math.Abs(result[1] - expected.Y * 255), 0, 2);
                Assert.Equal(255, result[3]);
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(framebuffer);
                gl.DeleteTexture(target);
                gl.DeleteBuffer(buffer);
                gl.DeleteVertexArray(vao);
                gl.DeleteProgram(program);
            }
        }

        private static Vector2 Turn(Vector2 value, float angle) => new(
            value.X * MathF.Cos(angle) - value.Y * MathF.Sin(angle),
            value.X * MathF.Sin(angle) + value.Y * MathF.Cos(angle));

        private static Vector3 Unproject(Vector2 ndc, float depth, Matrix4x4 inverse)
        {
            Vector4 point = Vector4.Transform(new Vector4(ndc, depth, 1), inverse);
            return new Vector3(point.X, point.Y, point.Z) / point.W;
        }

        private static uint Texture(GL gl, uint width, uint height, byte[] pixels)
        {
            uint texture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, texture);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, width, height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            return texture;
        }
    }
}
