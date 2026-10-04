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
    public sealed class VfxLayerCompositionGpuTests
    {
        public static IEnumerable<object[]> LayerCases()
        {
            foreach (VfxPrimitiveKind primitive in new[]
                     { VfxPrimitiveKind.CameraQuad, VfxPrimitiveKind.Mesh, VfxPrimitiveKind.AttachedMesh })
            foreach (int uvMode in primitive == VfxPrimitiveKind.CameraQuad ? new[] { 0 } : new[] { 0, 1, 2, 3 })
            for (int multState = 0; multState <= 3; multState++)
            foreach (bool palette in new[] { false, true })
            foreach (bool erosion in new[] { false, true })
            foreach (bool fallback in new[] { false, true })
                yield return new object[] { primitive, uvMode, multState, palette, erosion, fallback };
        }

        [Theory]
        [MemberData(nameof(LayerCases))]
        public void TextureLayersPreserveAuthoredColorAndCoverage(
            VfxPrimitiveKind primitive, int uvMode, int multState, bool palette, bool erosion, bool fallback)
        {
            string install = InstalledSkins.FindInstall();
            if (install == null) return;
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            using var renderer = new VfxOpenGlRenderer();
            renderer.Initialize(gl, fallback ? null : InstalledSkins.Settings(install));
            uint target = Texture(gl, 64, 64, new byte[64 * 64 * 4]);
            uint framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            uint baseTexture = Texture(gl, 1, 1, new byte[] { 160, 192, 224, 204 });
            uint multTexture = Texture(gl, 1, 1, new byte[] { 128, 128, 128, 153 });
            uint rampTexture = Texture(gl, 1, 1, new byte[] { 255, 64, 128, 128 });
            uint paletteTexture = Texture(gl, 1, 1, new byte[] { 64, 128, 192, 255 });
            uint erosionTexture = Texture(gl, 1, 1, new byte[] { 0, 0, 0, 128 });
            try
            {
                bool mesh = primitive is VfxPrimitiveKind.Mesh or VfxPrimitiveKind.AttachedMesh;
                var definition = new VfxEmitterDefinition("layers", VfxCurveF.Const(1), VfxCurveF.Const(1),
                    null, 0, 0, false, false, 3, VfxCurve3.Const(Vector3.One), null,
                    VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                    "base.tex", Vector2.One, 1, false, mesh, PrimitiveKind: primitive,
                    RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true },
                    UvMode: (byte)uvMode,
                    TextureMultPath: multState is 1 or 2 ? "mult.tex" : null,
                    AuthoredFeatures: multState == 3 ? new VfxEmitterAuthoredFeatures(HasTextureMultLayer: true) : null,
                    ParticleColorTexturePath: "ramp.tex",
                    PaletteDefinition: palette
                        ? new VfxPaletteDefinition(1, VfxCurve3.Const(Vector3.Zero), "palette.tex", Vector4.UnitX) : null,
                    AlphaErosion: erosion
                        ? new VfxAlphaErosionDefinition("erosion.tex", VfxCurveF.Const(0.15f), 0.1f, 0.1f, 2,
                            VfxCurve4.Const(Vector4.UnitW), SliceWidth: 0.4f) : null);
                var instance = new float[VfxPlaybackRuntime.InstanceStride];
                instance[3] = instance[4] = instance[18] = 0.75f;
                instance[5] = instance[6] = instance[7] = instance[8] = 1;
                instance[21] = instance[22] = instance[31] = instance[32] = 1;
                instance[24] = 0.15f;
                instance[28] = 1;
                instance[36] = instance[40] = instance[44] = 1;
                var emitter = new VfxPlaybackRuntime.EmitterState
                {
                    Def = definition, Texture = baseTexture, TextureMult = multState == 2 ? multTexture : 0,
                    ColorGradientTexture = rampTexture, PaletteTexture = palette ? paletteTexture : 0,
                    ErosionTexture = erosion ? erosionTexture : 0, Instances = instance, InstanceCount = 1
                };
                if (mesh)
                    renderer.UploadEmitterMesh(emitter,
                        new float[] { -1, -1, 0, 1, -1, 0, 0, 1, 0 },
                        new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                        new float[] { 0, 0, 1, 0, 0.5f, 1 }, null);

                gl.Viewport(0, 0, 64, 64);
                gl.ClearColor(0, 0, 0, 0);
                gl.Clear(ClearBufferMask.ColorBufferBit);
                renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
                Assert.Equal(fallback
                    ? "ShaderCache.dx11.wad.client was not found in the configured game installs." : null,
                    renderer.GameParticleFallback(emitter, mesh));
                var pixel = new byte[4];
                gl.ReadPixels(32, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel.AsSpan());

                // The original mesh MULT_PASS shaders have no particle-ramp sampler. Quad MULT_PASS retains it.
                Vector4 expected = new(160 / 255f, 192 / 255f, 224 / 255f, 204 / 255f);
                if (palette) expected = new Vector4(64 / 255f, 128 / 255f, 192 / 255f, expected.W);
                if (!erosion && (!mesh || multState == 0))
                    expected *= new Vector4(1, 64 / 255f, 128 / 255f, 128 / 255f);
                if (multState == 2) expected *= new Vector4(128 / 255f, 128 / 255f, 128 / 255f, 153 / 255f);
                if (erosion) expected.W *= Math.Clamp((0.15f - 128 / 255f + 0.4f) / 0.1f, 0, 1);
                for (int channel = 0; channel < 4; channel++)
                    Assert.True(Math.Abs(pixel[channel] - MathF.Round(expected[channel] * 255)) <= 2,
                        $"{primitive}, uv={uvMode}, mult={multState}, palette={palette}, erosion={erosion}, fallback={fallback}: " +
                        $"channel {channel} expected {MathF.Round(expected[channel] * 255)}, got {pixel[channel]}; " +
                        $"RGBA=({string.Join(",", pixel)}).");
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(framebuffer);
                foreach (uint texture in new[] { target, baseTexture, multTexture, rampTexture, paletteTexture, erosionTexture })
                    gl.DeleteTexture(texture);
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
