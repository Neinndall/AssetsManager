using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    [Collection("Viewport native graphics")]
    public sealed class VfxScreenUvGpuTests
    {
        public static IEnumerable<object[]> ScreenCases()
        {
            foreach (VfxPrimitiveKind primitive in new[] { VfxPrimitiveKind.CameraQuad,
                         VfxPrimitiveKind.ArbitraryQuad, VfxPrimitiveKind.Ray,
                         VfxPrimitiveKind.CameraTrail, VfxPrimitiveKind.ArbitraryTrail,
                         VfxPrimitiveKind.Beam, VfxPrimitiveKind.CameraSegmentBeam })
            for (int flips = 0; flips < 4; flips++)
            foreach (bool perspective in new[] { false, true })
            foreach (bool atlas in new[] { false, true })
            foreach (bool mult in new[] { false, true })
            foreach (bool game in new[] { false, true })
                if (!game || primitive is not (VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam))
                    yield return new object[] { primitive, flips, perspective, atlas, mult, game };
        }

        [Theory]
        [MemberData(nameof(ScreenCases))]
        public void ScreenUvUsesProjectedCoordinatesAndPreservesCoverage(
            VfxPrimitiveKind primitive, int flips, bool perspective, bool atlas, bool mult, bool game)
        {
            string install = InstalledSkins.FindInstall();
            if (install == null) return;
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            using var renderer = new VfxOpenGlRenderer();
            renderer.Initialize(gl, game ? InstalledSkins.Settings(install) : null);
            uint target = Texture(gl, 64, 64, new byte[64 * 64 * 4]);
            uint framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            var texels = new byte[64 * 64 * 4];
            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int at = (y * 64 + x) * 4;
                texels[at] = (byte)(4 * x + 2);
                texels[at + 1] = (byte)(4 * y + 2);
                texels[at + 2] = 128;
                texels[at + 3] = 255;
            }
            uint gradient = Texture(gl, 64, 64, texels);
            uint multiplier = Texture(gl, 1, 1, new byte[] { 128, 192, 64, 255 });
            try
            {
                var center = new Vector2(0.23f, 0.61f);
                var scale = new Vector2(0.65f, 0.8f);
                var offset = new Vector2(0.18f, 0.12f);
                const float rotation = 0.3f;
                Vector2 divisions = atlas ? new Vector2(2, 3) : Vector2.One;
                bool trail = primitive is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail;
                bool beam = primitive is VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam;
                var definition = new VfxEmitterDefinition("screen", VfxCurveF.Const(1), VfxCurveF.Const(1),
                    null, 0, 0, false, false, 3, VfxCurve3.Const(Vector3.One), null,
                    VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                    "base.tex", divisions, 6, false, false, PrimitiveKind: primitive, UvMode: 1,
                    RenderState: VfxEmitterRenderState.Default with
                    {
                        DisableBackfaceCull = true, TextureAddressMode = 2,
                        FlipU = (flips & 1) != 0, FlipV = (flips & 2) != 0
                    }, UvTransformCenter: center, TextureMultPath: mult ? "mult.tex" : null,
                    TextureMultAddressMode: 2,
                    Beam: beam ? new VfxBeamDefinition(0, 0, 1, VfxCurve3.Const(Vector3.One),
                        VfxCurve4.Const(Vector4.One), false, Vector3.Zero, Vector3.Zero) : null,
                    Trail: trail ? new VfxTrailDefinition(VfxCurve3.Const(Vector3.One), 0, 0, 0, 0) : null);
                int count = trail ? 2 : 1;
                var instances = new float[count * VfxPlaybackRuntime.InstanceStride];
                var emitter = new VfxPlaybackRuntime.EmitterState
                {
                    Def = definition, Texture = gradient, TextureMult = mult ? multiplier : 0,
                    SystemOrientation = Matrix4x4.Identity, InstanceCount = count,
                    SystemOrigin = new Vector3(-0.75f, 0, 0.2f), SystemTarget = new Vector3(0.75f, 0, 0.2f)
                };
                for (int index = 0; index < count; index++)
                {
                    int at = index * VfxPlaybackRuntime.InstanceStride;
                    instances[at] = trail ? (index == 0 ? -0.75f : 0.75f) : 0;
                    instances[at + 2] = trail && index == 1 ? 0.5f : 0.2f;
                    instances[at + 3] = instances[at + 4] = instances[at + 18] = 0.7f;
                    if (beam) instances[at + 4] = 0;
                    instances[at + 5] = instances[at + 6] = instances[at + 7] = instances[at + 8] = 1;
                    instances[at + 10] = 3;
                    instances[at + 19] = offset.X;
                    instances[at + 20] = offset.Y;
                    instances[at + 21] = scale.X;
                    instances[at + 22] = scale.Y;
                    instances[at + 23] = rotation;
                    instances[at + 31] = instances[at + 32] = 1;
                    instances[at + 36] = instances[at + 40] = instances[at + 44] = 1;
                    if (primitive == VfxPrimitiveKind.Ray)
                    {
                        instances[at + 40] = instances[at + 44] = 0;
                        instances[at + 41] = -1;
                        instances[at + 43] = 1;
                    }
                    emitter.Particles.Add(new VfxPlaybackRuntime.Particle
                    {
                        Serial = (uint)index, Life = 1, BirthFrame = Matrix4x4.Identity,
                        TrailTiling = new Vector3(2, 1, 0),
                        BirthRotation = primitive == VfxPrimitiveKind.ArbitraryTrail
                            ? new Vector3(0, 0, MathF.PI / 2) : Vector3.Zero
                    });
                }
                emitter.Instances = instances;
                Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 3), Vector3.Zero, Vector3.UnitY);
                Matrix4x4 projection = perspective
                    ? Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2, 1, 0.1f, 10)
                    : Matrix4x4.CreateOrthographic(4, 4, 0.1f, 10);
                gl.Viewport(0, 0, 64, 64);
                byte[] Draw(byte mode)
                {
                    emitter.Def = definition with { UvMode = mode };
                    gl.ClearColor(0, 0, 0, 0);
                    gl.Clear(ClearBufferMask.ColorBufferBit);
                    renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, view * projection, view);
                    var pixels = new byte[64 * 64 * 4];
                    gl.ReadPixels(0, 0, 64, 64, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
                    return pixels;
                }
                byte[] ordinary = Draw(0);
                byte[] screen = Draw(1);
                int[] covered = Enumerable.Range(0, 64 * 64).Where(at => ordinary[at * 4 + 3] == 255).ToArray();
                Assert.NotEmpty(covered);
                if (beam)
                {
                    // Beam screen-space transposition remains unaudited; preserve its prior path.
                    Assert.Equal(ordinary, screen);
                    Assert.Equal(GLEnum.NoError, gl.GetError());
                    return;
                }
                foreach (int at in covered)
                {
                    var ndc = new Vector2((at % 64 + 0.5f) / 32 - 1, (at / 64 + 0.5f) / 32 - 1);
                    Vector2 value = (ndc - center) * scale;
                    value = new Vector2(value.X * MathF.Cos(rotation) - value.Y * MathF.Sin(rotation),
                        value.X * MathF.Sin(rotation) + value.Y * MathF.Cos(rotation)) + center + offset;
                    if ((flips & 1) != 0) value.X = 1 - value.X;
                    if ((flips & 2) != 0) value.Y = 1 - value.Y;
                    float frame = 3 % (divisions.X * divisions.Y);
                    Vector2 uv = (value + new Vector2(frame % divisions.X, MathF.Floor(frame / divisions.X))) / divisions;
                    uv = Vector2.Clamp(uv, new Vector2(0.5f / 64), new Vector2(63.5f / 64));
                    float[] expected = { uv.X * 256 * (mult ? 128 / 255f : 1),
                        uv.Y * 256 * (mult ? 192 / 255f : 1), 128 * (mult ? 64 / 255f : 1), 255 };
                    for (int channel = 0; channel < 4; channel++)
                        Assert.True(Math.Abs(screen[at * 4 + channel] - expected[channel]) <= 2,
                            $"{primitive}, flips={flips}, perspective={perspective}, atlas={atlas}, mult={mult}, game={game}, " +
                            $"pixel={at}, channel={channel}: expected {expected[channel]}, got {screen[at * 4 + channel]}.");
                }
                for (int at = 0; at < 64 * 64; at++) Assert.Equal(ordinary[at * 4 + 3], screen[at * 4 + 3]);
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(framebuffer);
                foreach (uint texture in new[] { target, gradient, multiplier }) gl.DeleteTexture(texture);
            }
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
