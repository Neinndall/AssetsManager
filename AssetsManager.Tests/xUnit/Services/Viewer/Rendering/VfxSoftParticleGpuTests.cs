using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Semantics;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    [Collection("Viewport native graphics")]
    public sealed class VfxSoftParticleGpuTests
    {
        public static IEnumerable<object[]> SoftCases()
        {
            foreach (VfxPrimitiveKind primitive in new[]
                     {
                         VfxPrimitiveKind.CameraQuad, VfxPrimitiveKind.Mesh, VfxPrimitiveKind.AttachedMesh,
                         VfxPrimitiveKind.Beam, VfxPrimitiveKind.CameraSegmentBeam,
                         VfxPrimitiveKind.CameraTrail, VfxPrimitiveKind.ArbitraryTrail
                     })
            foreach (int mode in new[] { 0, 1, 3, 5 })
            foreach (float gap in new[] { 0.1f, 5f, 10f })
            foreach (bool orthographic in new[] { false, true })
            foreach (bool fallback in new[] { false, true })
            foreach (var range in new[] { (Near: 1f, Far: 201f), (Near: 2f, Far: 20000f) })
            foreach (float distance in orthographic && range.Far == 20000f
                         ? new[] { 60f, 10000f, 19000f } : new[] { 60f })
                yield return new object[] { primitive, mode, gap, orthographic, fallback, range.Near, range.Far, distance };
        }

        [Theory]
        [MemberData(nameof(SoftCases))]
        public void SceneIntersectionFadeMatchesTheWorldSpaceGap(
            VfxPrimitiveKind primitive, int mode, float gap, bool orthographic, bool fallback, float near, float far, float distance)
        {
            string install = InstalledSkins.FindInstall();
            if (install == null) return;
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            using var renderer = new VfxOpenGlRenderer();
            renderer.Initialize(gl, fallback ? null : InstalledSkins.Settings(install));
            uint target = Texture(gl, 64, 64, new byte[64 * 64 * 4]);
            uint depth = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, depth);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, 64, 64, 0,
                PixelFormat.DepthComponent, PixelType.Float, ReadOnlySpan<float>.Empty);
            uint framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D, depth, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            uint white = Texture(gl, 1, 1, new byte[] { 255, 255, 255, 255 });
            try
            {
                bool mesh = primitive is VfxPrimitiveKind.Mesh or VfxPrimitiveKind.AttachedMesh;
                bool trail = primitive is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail;
                float z = -(distance - gap);
                var definition = new VfxEmitterDefinition("soft", VfxCurveF.Const(1), VfxCurveF.Const(1),
                    null, 0, 0, false, false, mode, VfxCurve3.Const(Vector3.One), null,
                    VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                    "white.tex", Vector2.One, 1, false, mesh, PrimitiveKind: primitive,
                    RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true },
                    SoftParticle: new VfxSoftParticleDefinition(0, 10, 100, 10),
                    Beam: new VfxBeamDefinition(0, 0, 1, VfxCurve3.Const(Vector3.One),
                        VfxCurve4.Const(Vector4.One), false, Vector3.Zero, Vector3.Zero),
                    Trail: new VfxTrailDefinition(VfxCurve3.Const(Vector3.One), 0, 0, 0, 0));
                Vector4 authored = new(0.3f, 0.2f, 0.1f, 0.5f);
                Vector4 prepared = VfxColorSemantics.PremultiplyForAddOrSubtract(authored, mode, false);
                int count = trail ? 2 : 1;
                var instances = new float[count * VfxPlaybackRuntime.InstanceStride];
                var emitter = new VfxPlaybackRuntime.EmitterState
                {
                    Def = definition, Texture = white, TextureWidth = 1, TextureHeight = 1,
                    SystemOrigin = new Vector3(-20, 0, z), SystemTarget = new Vector3(20, 0, z),
                    SystemOrientation = Matrix4x4.Identity, InstanceCount = count
                };
                for (int index = 0; index < count; index++)
                {
                    int at = index * VfxPlaybackRuntime.InstanceStride;
                    instances[at] = trail ? (index == 0 ? -20 : 20) : 0;
                    instances[at + 2] = z;
                    instances[at + 3] = 20;
                    instances[at + 4] = instances[at + 18] = mesh || primitive == VfxPrimitiveKind.CameraQuad ? 20 : 0;
                    instances[at + 5] = prepared.X;
                    instances[at + 6] = prepared.Y;
                    instances[at + 7] = prepared.Z;
                    instances[at + 8] = prepared.W;
                    instances[at + 21] = instances[at + 22] = instances[at + 31] = instances[at + 32] = 1;
                    instances[at + 36] = instances[at + 40] = instances[at + 44] = 1;
                    emitter.Particles.Add(new VfxPlaybackRuntime.Particle
                    {
                        Serial = (uint)index, Life = 1, BirthFrame = Matrix4x4.Identity,
                        TrailTiling = new Vector3(2, 1, 0),
                        BirthRotation = primitive == VfxPrimitiveKind.ArbitraryTrail
                            ? new Vector3(0, 0, MathF.PI / 2) : Vector3.Zero
                    });
                }
                emitter.Instances = instances;
                if (primitive == VfxPrimitiveKind.AttachedMesh)
                    renderer.SetOwnerWorldTransform(Matrix4x4.CreateTranslation(0, 0, z));
                if (mesh)
                    renderer.UploadEmitterMesh(emitter,
                        new float[] { -1, -1, 0, 1, -1, 0, 0, 1, 0 },
                        new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                        new float[] { 0, 0, 1, 0, 0.5f, 1 }, null);

                Matrix4x4 projection = orthographic
                    ? Matrix4x4.CreateOrthographic(100, 100, near, far)
                    : Matrix4x4.CreatePerspectiveFieldOfView(1, 1, near, far);
                Vector4 background = new(0.2f, 0.4f, 0.6f, 0.3f);
                void Draw(Matrix4x4 camera)
                {
                    Vector4 sceneClip = Vector4.Transform(new Vector4(0, 0, -distance, 1), camera);
                    gl.Viewport(0, 0, 64, 64);
                    gl.DepthMask(true);
                    gl.DepthFunc(DepthFunction.Lequal);
                    gl.ClearDepth((sceneClip.Z / sceneClip.W + 1) * 0.5);
                    gl.ClearColor(background.X, background.Y, background.Z, background.W);
                    gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                    renderer.CaptureScene(64, 64, captureColor: false, captureDepth: true);
                    renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, camera, Matrix4x4.Identity);
                }
                // Reuse the same programs across projection changes, then a second identical frame.
                Draw(orthographic
                    ? Matrix4x4.CreatePerspectiveFieldOfView(1, 1, near, far)
                    : Matrix4x4.CreateOrthographic(100, 100, 2, 20000));
                Draw(projection);
                Draw(projection);
                Assert.Equal(fallback
                    ? "ShaderCache.dx11.wad.client was not found in the configured game installs." : null,
                    renderer.GameParticleFallback(emitter, mesh));
                var pixel = new byte[4];
                gl.ReadPixels(32, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel.AsSpan());

                // Attached meshes have no soft fade in the original shader. Other paths fade over a ten-unit gap.
                float fraction = primitive == VfxPrimitiveKind.AttachedMesh ? 1 : gap / 10;
                float fade = fraction * fraction * (3 - 2 * fraction);
                Vector4 expected = ComposeSoft(mode, prepared, background, fade);
                for (int channel = 0; channel < 4; channel++)
                    Assert.True(Math.Abs(pixel[channel] - MathF.Round(expected[channel] * 255)) <= 2,
                        $"{primitive}, mode={mode}, gap={gap}, ortho={orthographic}, fallback={fallback}, range={near}..{far}, distance={distance}: " +
                        $"channel {channel} expected {MathF.Round(expected[channel] * 255)}, got {pixel[channel]}; " +
                        $"RGBA=({string.Join(",", pixel)}).");
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(framebuffer);
                gl.DeleteTexture(target);
                gl.DeleteTexture(depth);
                gl.DeleteTexture(white);
            }
        }

        private static Vector4 ComposeSoft(int mode, Vector4 source, Vector4 background, float fade)
        {
            if (mode is 1 or 5) source.W *= fade;
            if (mode != 1) source *= new Vector4(fade, fade, fade, 1);
            Vector4 result = mode switch
            {
                0 => source + background,
                1 => source * source.W + background * (1 - source.W),
                3 => source,
                5 => source + background * (1 - source.W),
                _ => throw new ArgumentOutOfRangeException(nameof(mode))
            };
            return Vector4.Clamp(result, Vector4.Zero, Vector4.One);
        }

        private static uint Texture(GL gl, uint width, uint height, byte[] pixels)
        {
            uint texture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, texture);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, width, height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
            return texture;
        }
    }
}
