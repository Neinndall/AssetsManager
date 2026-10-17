using System;
using System.Collections.Generic;
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
    public sealed class VfxCameraQuadRollGpuTests
    {
        public static IEnumerable<object[]> Cases()
        {
            foreach (string orientation in new[] { "camera", "unit", "directed", "arbitrary", "plane" })
            foreach (bool native in new[] { false, true })
            foreach (bool pivot in new[] { false, true })
            foreach (var shape in new[]
                     {
                         (0.65f, 0.22f, 0.7f), (0.65f, 0.22f, MathF.PI / 2),
                         (0.65f, 0.22f, 0f), (0.3f, 0.3f, -0.6f), (-0.65f, 0.22f, -0.6f)
                     })
                yield return new object[] { orientation, native, pivot, shape.Item1, shape.Item2, shape.Item3 };
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void CoverageAndUvsFollowAuthoredSizeBeforeCameraRoll(
            string orientation, bool native, bool pivot, float sizeX, float sizeY, float roll)
        {
            string install = native ? InstalledSkins.FindInstall() : null;
            if (native) Assert.False(string.IsNullOrWhiteSpace(install), "Native coverage requires an installed client.");
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            using var renderer = new VfxOpenGlRenderer();
            renderer.Initialize(gl, native ? InstalledSkins.Settings(install) : null);
            const int width = 96;
            uint target = Texture(gl, width, width, new byte[width * width * 4]);
            uint framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            var gradient = new byte[64 * 64 * 4];
            for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int at = (y * 64 + x) * 4;
                gradient[at] = (byte)(x * 4 + 2);
                gradient[at + 1] = (byte)(y * 4 + 2);
                gradient[at + 2] = gradient[at + 3] = 255;
            }
            uint texture = Texture(gl, 64, 64, gradient);
            try
            {
                bool arbitrary = orientation == "arbitrary";
                var primitive = arbitrary ? VfxPrimitiveKind.ArbitraryQuad
                    : orientation == "unit" ? VfxPrimitiveKind.CameraUnitQuad : VfxPrimitiveKind.CameraQuad;
                var definition = new VfxEmitterDefinition("camera roll", VfxCurveF.Const(1), VfxCurveF.Const(1),
                    null, 0, 0, false, false, 3, VfxCurve3.Const(Vector3.One), null,
                    VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                    "gradient.tex", Vector2.One, 1, false, false, PrimitiveKind: primitive,
                    IsDirectionOriented: orientation == "directed", IsArbitraryQuad: arbitrary,
                    LegacyOrientation: orientation == "plane" ? (byte)3 : (byte)0,
                    LegacyScaleUpFromOrigin: pivot,
                    RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true, TextureAddressMode = 2 });
                var instance = new float[VfxPlaybackRuntime.InstanceStride];
                instance[3] = sizeX; instance[4] = sizeY; instance[18] = 1;
                instance[5] = instance[6] = instance[7] = instance[8] = 1;
                instance[9] = roll;
                instance[21] = instance[22] = instance[31] = instance[32] = 1;
                instance[36] = instance[40] = instance[44] = 1;
                var emitter = new VfxPlaybackRuntime.EmitterState
                { Def = definition, Texture = texture, Instances = instance, InstanceCount = 1 };
                gl.Viewport(0, 0, width, width);
                gl.ClearColor(0, 0, 0, 0);
                gl.Clear(ClearBufferMask.ColorBufferBit);
                renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
                Assert.Equal(native ? null : "ShaderCache.dx11.wad.client was not found in the configured game installs.",
                    renderer.GameParticleFallback(emitter, false));
                var pixels = new byte[width * width * 4];
                gl.ReadPixels(0, 0, width, width, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
                float angle = arbitrary || orientation == "directed" ? 0 : roll;
                float reach = orientation == "unit" || arbitrary ? 1 : 2;
                var size = new Vector2(sizeX, sizeY) * reach;
                int covered = 0, empty = 0;
                for (int y = 0; y < width; y++)
                for (int x = 0; x < width; x++)
                {
                    var point = new Vector2((x + 0.5f) * 2 / width - 1, (y + 0.5f) * 2 / width - 1);
                    // Invert the geometric contract, independently of shader corner construction.
                    Vector2 local = orientation == "plane" ? Turn(point / size, -angle) : Turn(point, -angle) / size;
                    local.Y -= pivot ? 0.5f : 0;
                    float margin = MathF.Min(0.5f - MathF.Abs(local.X), 0.5f - MathF.Abs(local.Y));
                    if (MathF.Abs(margin) < 0.035f) continue;
                    int at = (y * width + x) * 4;
                    if (margin < 0)
                    {
                        Assert.True(pixels[at + 3] == 0, $"Unexpected coverage at ({x}, {y}), local={local}.");
                        empty++;
                        continue;
                    }
                    covered++;
                    Assert.Equal(255, pixels[at + 3]);
                    Vector2 uv = arbitrary ? new Vector2(local.Y + 0.5f, local.X + 0.5f)
                        : new Vector2(local.X + 0.5f, 0.5f - local.Y);
                    Assert.InRange(MathF.Abs(pixels[at] - uv.X * 256), 0, 2);
                    Assert.InRange(MathF.Abs(pixels[at + 1] - uv.Y * 256), 0, 2);
                }
                Assert.True(covered > 20 && empty > 20, "Both interior and exterior samples must be checked.");
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(framebuffer);
                gl.DeleteTexture(target);
                gl.DeleteTexture(texture);
            }
        }

        private static Vector2 Turn(Vector2 value, float angle) => Vector2.Transform(value, Matrix3x2.CreateRotation(angle));

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
