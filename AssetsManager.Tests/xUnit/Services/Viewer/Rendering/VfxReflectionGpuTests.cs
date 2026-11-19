using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    [Collection("Viewport native graphics")]
    public sealed class VfxReflectionGpuTests
    {
        [Theory]
        [InlineData(VfxPrimitiveKind.CameraQuad, false)]
        [InlineData(VfxPrimitiveKind.CameraQuad, true)]
        [InlineData(VfxPrimitiveKind.Beam, false)]
        [InlineData(VfxPrimitiveKind.Beam, true)]
        public void NonMeshReflectionDefersFromNativeAndLeavesBaseColorUnchanged(
            VfxPrimitiveKind primitive, bool withCube)
        {
            string install = InstalledSkins.FindInstall();
            Assert.NotNull(install);

            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            using var renderer = new VfxOpenGlRenderer();
            renderer.Initialize(gl, InstalledSkins.Settings(install));

            uint target = Texture2D(gl, 64, 64, new byte[64 * 64 * 4]);
            uint framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

            uint white = Texture2D(gl, 1, 1, new byte[] { 255, 255, 255, 255 });
            uint cube = CreateSolidCubemap(gl, 255, 64, 32, 255);
            try
            {
                bool beam = primitive == VfxPrimitiveKind.Beam;
                var reflection = new VfxReflectionDefinition(
                    Fresnel: 1f,
                    ReflectionFresnel: 1f,
                    DirectOpacity: 1f,
                    GlancingOpacity: 1f,
                    FresnelColor: new Vector4(0f, 1f, 0f, 1f),
                    ReflectionFresnelColor: new Vector4(1f, 0f, 0f, 1f),
                    TexturePath: withCube ? "reflection.dds" : null);

                var definition = new VfxEmitterDefinition(
                    Name: "nonmesh-refl",
                    Rate: VfxCurveF.Const(1f),
                    ParticleLifetime: VfxCurveF.Const(1f),
                    EmitterLifetime: null,
                    ParticleLinger: 0f,
                    TimeBeforeFirstEmission: 0f,
                    IsSingleParticle: true,
                    Disabled: false,
                    BlendMode: 1,
                    BirthScale: VfxCurve3.Const(Vector3.One),
                    ScaleOverLife: null,
                    BirthColor: VfxCurve4.Const(new Vector4(0.2f, 0.4f, 0.8f, 1f)),
                    ColorOverLife: null,
                    BirthVelocity: null,
                    Acceleration: null,
                    BirthRotationalVelocity: null,
                    EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                    TexturePath: "white.tex",
                    TexDiv: Vector2.One,
                    NumFrames: 1,
                    RandomStartFrame: false,
                    IsMeshPrimitive: false,
                    PrimitiveKind: primitive,
                    Reflection: reflection,
                    Beam: beam ? new VfxBeamDefinition(0, 0, 2, VfxCurve3.Const(Vector3.One),
                        VfxCurve4.Const(Vector4.One), false, Vector3.Zero, Vector3.Zero) : null,
                    RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true });

                var instance = new float[VfxPlaybackRuntime.InstanceStride];
                instance[3] = 2f;
                instance[4] = beam ? 0f : 2f;
                instance[5] = 0.2f;
                instance[6] = 0.4f;
                instance[7] = 0.8f;
                instance[8] = 1f;
                instance[21] = instance[22] = 1f;
                instance[36] = instance[40] = instance[44] = 1f;

                var emitter = new VfxPlaybackRuntime.EmitterState
                {
                    Def = definition,
                    Texture = white,
                    ReflectionTexture = withCube ? cube : 0,
                    SystemOrientation = Matrix4x4.Identity,
                    SystemOrigin = new Vector3(-1f, 0f, 0f),
                    SystemTarget = new Vector3(1f, 0f, 0f),
                    Instances = instance,
                    InstanceCount = 1
                };
                emitter.Particles.Add(new VfxPlaybackRuntime.Particle
                {
                    Serial = 0,
                    Ordinal = 0,
                    Life = 1f,
                    BirthFrame = Matrix4x4.Identity,
                    TrailTiling = new Vector3(1f, 1f, 0f)
                });

                Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 3f), Vector3.Zero, Vector3.UnitY);
                Matrix4x4 projection = Matrix4x4.CreateOrthographic(2f, 2f, 0.1f, 10f);

                gl.Viewport(0, 0, 64, 64);
                gl.ClearColor(0f, 0f, 0f, 0f);
                gl.Clear(ClearBufferMask.ColorBufferBit);
                renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, view * projection, view);

                // QUAD_PS has no REFLECTIVE permutation: native defers to stock and adds no synthetic rim/cubemap.
                Assert.NotNull(renderer.GameParticleFallback(emitter, mesh: false));

                var pixel = new byte[4];
                gl.ReadPixels(32, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel.AsSpan());
                Assert.InRange(Math.Abs(pixel[0] - 51), 0, 3);
                Assert.InRange(Math.Abs(pixel[1] - 102), 0, 3);
                Assert.InRange(Math.Abs(pixel[2] - 204), 0, 3);
                Assert.InRange(Math.Abs(pixel[3] - 255), 0, 3);
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(framebuffer);
                foreach (uint texture in new[] { target, white, cube }) gl.DeleteTexture(texture);
            }
        }

        [Theory]
        [InlineData(false, VfxPrimitiveKind.Mesh)]
        [InlineData(true, VfxPrimitiveKind.Mesh)]
        [InlineData(false, VfxPrimitiveKind.AttachedMesh)]
        [InlineData(true, VfxPrimitiveKind.AttachedMesh)]
        public void MeshReflectionAndFresnelRespondToSurfaceNormalsInNativeAndStock(
            bool game, VfxPrimitiveKind primitive)
        {
            string install = game ? InstalledSkins.FindInstall() : null;
            if (game) Assert.NotNull(install);

            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            using var renderer = new VfxOpenGlRenderer();
            renderer.Initialize(gl, game ? InstalledSkins.Settings(install) : null);

            uint target = Texture2D(gl, 64, 64, new byte[64 * 64 * 4]);
            uint framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

            uint black = Texture2D(gl, 1, 1, new byte[] { 0, 0, 0, 255 });
            uint cube = CreateSolidCubemap(gl, 255, 0, 0, 255);
            try
            {
                var reflection = new VfxReflectionDefinition(
                    Fresnel: 2f,
                    ReflectionFresnel: 2f,
                    DirectOpacity: 0f,
                    GlancingOpacity: 1f,
                    FresnelColor: new Vector4(0f, 1f, 0f, 1f),
                    ReflectionFresnelColor: new Vector4(1f, 0f, 0f, 1f),
                    TexturePath: "cube.dds");

                var definition = new VfxEmitterDefinition(
                    Name: "mesh-refl",
                    Rate: VfxCurveF.Const(1f),
                    ParticleLifetime: VfxCurveF.Const(1f),
                    EmitterLifetime: null,
                    ParticleLinger: 0f,
                    TimeBeforeFirstEmission: 0f,
                    IsSingleParticle: true,
                    Disabled: false,
                    BlendMode: 1,
                    BirthScale: VfxCurve3.Const(Vector3.One),
                    ScaleOverLife: null,
                    BirthColor: VfxCurve4.Const(Vector4.One),
                    ColorOverLife: null,
                    BirthVelocity: null,
                    Acceleration: null,
                    BirthRotationalVelocity: null,
                    EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                    TexturePath: "black.tex",
                    TexDiv: Vector2.One,
                    NumFrames: 1,
                    RandomStartFrame: false,
                    IsMeshPrimitive: true,
                    PrimitiveKind: primitive,
                    Reflection: reflection,
                    RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true });

                var instance = new float[VfxPlaybackRuntime.InstanceStride];
                instance[3] = instance[4] = instance[18] = 1f;
                instance[5] = instance[6] = instance[7] = instance[8] = 1f;
                instance[21] = instance[22] = instance[31] = instance[32] = 1f;
                instance[36] = instance[40] = instance[44] = 1f;

                var emitter = new VfxPlaybackRuntime.EmitterState
                {
                    Def = definition,
                    Texture = black,
                    ReflectionTexture = cube,
                    Instances = instance,
                    InstanceCount = 1
                };

                // Left patch has vertices close to the camera ray (normal +Z); right patch has a grazing normal (~80 deg from +Z).
                Vector3 grazing = Vector3.Normalize(new Vector3(0.985f, 0f, 0.174f));
                renderer.UploadEmitterMesh(
                    emitter,
                    new float[]
                    {
                        -0.5f, -0.25f, 0f,  0f, -0.25f, 0f,  0f, 0.25f, 0f,
                        -0.5f, -0.25f, 0f,  0f,  0.25f, 0f, -0.5f, 0.25f, 0f,
                         0.25f, -0.25f, 0f, 0.75f, -0.25f, 0f, 0.75f, 0.25f, 0f,
                         0.25f, -0.25f, 0f, 0.75f,  0.25f, 0f, 0.25f, 0.25f, 0f
                    },
                    new float[]
                    {
                        0f, 0f, 1f,  0f, 0f, 1f,  0f, 0f, 1f,
                        0f, 0f, 1f,  0f, 0f, 1f,  0f, 0f, 1f,
                        grazing.X, grazing.Y, grazing.Z,  grazing.X, grazing.Y, grazing.Z,  grazing.X, grazing.Y, grazing.Z,
                        grazing.X, grazing.Y, grazing.Z,  grazing.X, grazing.Y, grazing.Z,  grazing.X, grazing.Y, grazing.Z
                    },
                    new float[]
                    {
                        0f, 0f, 1f, 0f, 1f, 1f, 0f, 0f, 1f, 1f, 0f, 1f,
                        0f, 0f, 1f, 0f, 1f, 1f, 0f, 0f, 1f, 1f, 0f, 1f
                    },
                    null);

                var eye = new Vector3(0f, 0f, 3f);
                Matrix4x4 view = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitY);
                Matrix4x4 projection = Matrix4x4.CreateOrthographic(2f, 2f, 0.1f, 10f);

                gl.Viewport(0, 0, 64, 64);
                gl.ClearColor(0f, 0f, 0f, 0f);
                gl.Clear(ClearBufferMask.ColorBufferBit);
                renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, view * projection, view);
                if (game) Assert.Null(renderer.GameParticleFallback(emitter, mesh: true));

                var facingPixel = new byte[4];
                var grazingPixel = new byte[4];
                gl.ReadPixels(24, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, facingPixel.AsSpan());
                gl.ReadPixels(48, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, grazingPixel.AsSpan());

                Assert.True(facingPixel[0] < 10 && facingPixel[1] < 10,
                    $"{(game ? "native" : "stock")} {primitive}: facing normal produced unexpected sheen ({facingPixel[0]}, {facingPixel[1]}).");
                Assert.True(grazingPixel[0] > 100 && grazingPixel[1] > 100,
                    $"{(game ? "native" : "stock")} {primitive}: grazing normal failed to produce cubemap R ({grazingPixel[0]}) and Fresnel G ({grazingPixel[1]}).");
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(framebuffer);
                foreach (uint texture in new[] { target, black, cube }) gl.DeleteTexture(texture);
            }
        }

        private static uint Texture2D(GL gl, uint width, uint height, byte[] pixels)
        {
            uint texture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, texture);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, width, height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            return texture;
        }

        private static uint CreateSolidCubemap(GL gl, byte r, byte g, byte b, byte a)
        {
            uint cube = gl.GenTexture();
            gl.BindTexture(TextureTarget.TextureCubeMap, cube);
            byte[] face = { r, g, b, a };
            for (int i = 0; i < 6; i++)
            {
                gl.TexImage2D((TextureTarget)((int)TextureTarget.TextureCubeMapPositiveX + i),
                    0, InternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, face.AsSpan());
            }
            gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
            gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
            gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
            gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureWrapR, (int)GLEnum.ClampToEdge);
            return cube;
        }
    }
}
