using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
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
    public sealed class VfxTextureReadinessTests
    {
        [Theory]
        [InlineData(VfxPrimitiveKind.CameraQuad, true, false)]
        [InlineData(VfxPrimitiveKind.Mesh, true, false)]
        [InlineData(VfxPrimitiveKind.AttachedMesh, true, false)]
        [InlineData(VfxPrimitiveKind.CameraQuad, false, false)]
        [InlineData(VfxPrimitiveKind.Mesh, false, false)]
        [InlineData(VfxPrimitiveKind.AttachedMesh, false, false)]
        [InlineData(VfxPrimitiveKind.CameraQuad, true, true)]
        [InlineData(VfxPrimitiveKind.Mesh, true, true)]
        [InlineData(VfxPrimitiveKind.AttachedMesh, true, true)]
        public void PaletteReadinessPreservesTextureAndNativeRecovery(
            VfxPrimitiveKind primitive, bool hasPalette, bool custom)
        {
            bool mesh = primitive != VfxPrimitiveKind.CameraQuad;
            string install = InstalledSkins.FindInstall();
            if (install == null) return;
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            using var renderer = new VfxOpenGlRenderer();
            renderer.Initialize(gl, InstalledSkins.Settings(install));

            uint target = CreateTexture(gl, 64, 64, new byte[64 * 64 * 4]);
            uint framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

            byte[] texels = new byte[16 * 16 * 4];
            for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                int at = (y * 16 + x) * 4;
                byte color = x is >= 6 and <= 9 && y is >= 6 and <= 9 ? (byte)255 : (byte)0;
                texels[at] = texels[at + 1] = texels[at + 2] = color;
                texels[at + 3] = 255;
            }
            uint texture = CreateTexture(gl, 16, 16, texels);
            uint palette = 0;
            try
            {
                var definition = new VfxEmitterDefinition("palette", VfxCurveF.Const(1), VfxCurveF.Const(1),
                    null, 0, 0, false, false, 4, VfxCurve3.Const(Vector3.One), null,
                    VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                    "base.tex", Vector2.One, 1, false, mesh,
                    RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true },
                    PrimitiveKind: primitive,
                    PaletteDefinition: hasPalette
                        ? new VfxPaletteDefinition(1, VfxCurve3.Const(Vector3.Zero), "palette.tex", new Vector4(1, 0, 0, 0))
                        : null);
                float[] instance = new float[VfxPlaybackRuntime.InstanceStride];
                instance[3] = instance[4] = instance[18] = 0.75f;
                instance[5] = instance[6] = instance[7] = instance[8] = 1;
                instance[21] = instance[22] = instance[31] = instance[32] = 1;
                instance[36] = instance[40] = instance[44] = 1;
                var emitter = new VfxPlaybackRuntime.EmitterState
                {
                    Def = definition, Texture = texture, TextureWidth = 16, TextureHeight = 16,
                    Instances = instance, InstanceCount = 1
                };
                if (custom)
                {
                    // The fixture uses a stock shader as a custom pass, which must author its stock defaults.
                    var parameters = new Dictionary<string, Vector4>();
                    VfxShaderParameterUtils.PopulateNativeParameters(parameters, definition, 0);
                    GameMaterialProgram program = GameParticleProgramResolver.Create(
                        definition with { PaletteDefinition = null }, mesh);
                    program = program with
                    {
                        Passes = program.Passes.Select(pass => pass with
                        {
                            State = pass.State with { CullEnabled = false },
                            Parameters = parameters.Select(pair => new GameMaterialParameter(
                                pair.Key, pair.Value, GameMaterialParamSource.Material)).ToArray(),
                            Textures = new[] { new GameMaterialTexture("TEXTURE", new MapTextureReference("base.tex", 0),
                                GameMaterialTextureSource.Material, null) }
                        }).ToArray()
                    };
                    emitter.Def = definition with
                    {
                        CustomMaterial = ModelMaterialDefinition.TextureOnly("base.tex") with { Program = program }
                    };
                    emitter.ProgramTextures["base.tex"] = texture;
                }
                if (mesh)
                    renderer.UploadEmitterMesh(emitter,
                        new float[] { -1, -1, 0, 1, -1, 0, 0, 1, 0 },
                        new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                        new float[] { 0, 0, 1, 0, 0.5f, 1 }, null);

                byte[] missing = Draw(gl, renderer, emitter, framebuffer);
                int lit = Enumerable.Range(0, 64 * 64).Count(at => missing[at * 4] > 32);
                Assert.InRange(lit, 1, 512);
                Assert.Equal(hasPalette && !custom ? "Palette texture is unavailable." : null,
                    renderer.GameParticleFallback(emitter, mesh));

                palette = CreateTexture(gl, 2, 1, new byte[] { 0, 0, 0, 255, 0, 0, 255, 255 });
                emitter.PaletteTexture = palette;
                byte[] loaded = Draw(gl, renderer, emitter, framebuffer);
                if (hasPalette && !custom)
                {
                    Assert.Contains(Enumerable.Range(0, 64 * 64), at => loaded[at * 4 + 2] > 64);
                    Assert.DoesNotContain(Enumerable.Range(0, 64 * 64), at => loaded[at * 4] > 16);
                }
                else
                    Assert.Equal(missing, loaded);
                Assert.Null(renderer.GameParticleFallback(emitter, mesh));

                emitter.PaletteTexture = 0;
                Assert.Equal(missing, Draw(gl, renderer, emitter, framebuffer));
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(framebuffer);
                gl.DeleteTexture(target);
                gl.DeleteTexture(texture);
                if (palette != 0) gl.DeleteTexture(palette);
            }
        }

        private static byte[] Draw(GL gl, VfxOpenGlRenderer renderer,
            VfxPlaybackRuntime.EmitterState emitter, uint framebuffer)
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.Viewport(0, 0, 64, 64);
            gl.ClearColor(0, 0, 0, 1);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
            var pixels = new byte[64 * 64 * 4];
            gl.ReadPixels(0, 0, 64, 64, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            return pixels;
        }

        private static uint CreateTexture(GL gl, uint width, uint height, byte[] pixels)
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
