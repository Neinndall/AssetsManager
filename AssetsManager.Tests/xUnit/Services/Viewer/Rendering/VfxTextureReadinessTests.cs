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

        [Theory]
        [InlineData(VfxPrimitiveKind.CameraQuad, true)]
        [InlineData(VfxPrimitiveKind.Mesh, true)]
        [InlineData(VfxPrimitiveKind.AttachedMesh, true)]
        [InlineData(VfxPrimitiveKind.CameraQuad, false)]
        [InlineData(VfxPrimitiveKind.Mesh, false)]
        [InlineData(VfxPrimitiveKind.AttachedMesh, false)]
        public void NativeErosionChannelSelectionMatchesParticleStateAcrossEmitterAge(
            VfxPrimitiveKind primitive, bool curvedMixer)
        {
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
            uint texture = CreateTexture(gl, 1, 1, new byte[] { 255, 255, 255, 255 });
            uint erosion = CreateTexture(gl, 1, 1, new byte[] { 255, 0, 0, 0 });
            try
            {
                bool mesh = primitive != VfxPrimitiveKind.CameraQuad;
                VfxCurve4 mixer = curvedMixer
                    ? new VfxCurve4(Vector4.UnitW, new[] { 0f, 1f }, new[] { Vector4.UnitW, Vector4.UnitX })
                    : VfxCurve4.Const(Vector4.UnitW);
                var definition = new VfxEmitterDefinition("erosion", VfxCurveF.Const(1), VfxCurveF.Const(1),
                    1, 0, 0, false, false, 4, VfxCurve3.Const(Vector3.One), null,
                    VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                    "base.tex", Vector2.One, 1, false, mesh, PrimitiveKind: primitive,
                    RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true },
                    AlphaErosion: new VfxAlphaErosionDefinition("erosion.tex", VfxCurveF.Const(-0.2f),
                        0.1f, 0.1f, 2, mixer, SliceWidth: 0.4f));
                float[] instance = new float[VfxPlaybackRuntime.InstanceStride];
                instance[3] = instance[4] = instance[18] = 0.75f;
                instance[5] = instance[6] = instance[7] = instance[8] = 1;
                instance[21] = instance[22] = instance[31] = instance[32] = 1;
                instance[24] = -0.2f;
                instance[28] = 1;
                instance[36] = instance[40] = instance[44] = 1;
                var emitter = new VfxPlaybackRuntime.EmitterState
                {
                    Def = definition, Texture = texture, ErosionTexture = erosion,
                    Instances = instance, InstanceCount = 1
                };
                if (mesh)
                    renderer.UploadEmitterMesh(emitter,
                        new float[] { -1, -1, 0, 1, -1, 0, 0, 1, 0 },
                        new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                        new float[] { 0, 0, 1, 0, 0.5f, 1 }, null);
                byte[] initial = Draw(gl, renderer, emitter, framebuffer);
                Assert.Contains(Enumerable.Range(0, 64 * 64), at => initial[at * 4] > 32);
                foreach (float age in new[] { 0.5f, 1f })
                {
                    // Instances sample the mixer at zero; changing only the emitter's clock must preserve that channel.
                    emitter.Age = age;
                    byte[] later = Draw(gl, renderer, emitter, framebuffer);
                    Assert.Null(renderer.GameParticleFallback(emitter, mesh));
                    Assert.True(initial.SequenceEqual(later),
                        $"{primitive} changed erosion channel at emitter age {age} with unchanged particle state.");
                }
                instance[24] = 1f;
                byte[] eroded = Draw(gl, renderer, emitter, framebuffer);
                Assert.DoesNotContain(Enumerable.Range(0, 64 * 64), at => eroded[at * 4] > 32);
                instance[24] = -0.2f;
                Assert.True(initial.SequenceEqual(Draw(gl, renderer, emitter, framebuffer)));
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(framebuffer);
                gl.DeleteTexture(target);
                gl.DeleteTexture(texture);
                gl.DeleteTexture(erosion);
            }
        }

        public static IEnumerable<object[]> CustomSamplerCases()
        {
            foreach (VfxPrimitiveKind primitive in new[]
                     { VfxPrimitiveKind.CameraQuad, VfxPrimitiveKind.Mesh, VfxPrimitiveKind.AttachedMesh })
            {
                yield return new object[] { primitive, "Clamp_No_Mip", (int)MapTextureWrap.Repeat, true };
                yield return new object[] { primitive, "Clamp_Linear_Mip", (int)MapTextureWrap.Repeat, true };
                yield return new object[] { primitive, "CharacterClamp", (int)MapTextureWrap.Repeat, true };
                yield return new object[] { primitive, "Wrap_No_Mip", (int)MapTextureWrap.Clamp, false };
                yield return new object[] { primitive, "CharacterWrap", (int)MapTextureWrap.Clamp, false };
                yield return new object[] { primitive, null, (int)MapTextureWrap.Clamp, true };
                yield return new object[] { primitive, null, (int)MapTextureWrap.Repeat, false };
                yield return new object[] { primitive, null, (int)MapTextureWrap.Mirror, true };
            }
        }

        [Theory]
        [MemberData(nameof(CustomSamplerCases))]
        public void CustomMaterialPreservesSamplerAddressingWithSingleLevelTextures(
            VfxPrimitiveKind primitive, string sharedSampler, int addressMode, bool blue)
        {
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
            uint texture = CreateTexture(gl, 2, 1, new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 });
            try
            {
                bool mesh = primitive != VfxPrimitiveKind.CameraQuad;
                var definition = new VfxEmitterDefinition("custom sampler", VfxCurveF.Const(1), VfxCurveF.Const(1),
                    null, 0, 0, false, false, 4, VfxCurve3.Const(Vector3.One), null,
                    VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                    "emitter.tex", Vector2.One, 1, false, mesh, PrimitiveKind: primitive,
                    RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true });
                var parameters = new Dictionary<string, Vector4>();
                VfxShaderParameterUtils.PopulateNativeParameters(parameters, definition, 0);
                var wrap = (MapTextureWrap)addressMode;
                var sampler = new GameMaterialSamplerState(sharedSampler, wrap, wrap, wrap, true, true);
                GameMaterialProgram program = GameParticleProgramResolver.Create(definition, mesh);
                program = program with
                {
                    Passes = program.Passes.Select(pass => pass with
                    {
                        State = pass.State with { CullEnabled = false },
                        Parameters = parameters.Select(pair => new GameMaterialParameter(
                            pair.Key, pair.Value, GameMaterialParamSource.Material)).ToArray(),
                        Textures = new[] { new GameMaterialTexture("TEXTURE", new MapTextureReference("custom.tex", 0),
                            GameMaterialTextureSource.ShaderDefault, sampler) }
                    }).ToArray()
                };
                definition = definition with
                {
                    CustomMaterial = ModelMaterialDefinition.TextureOnly("custom.tex") with { Program = program }
                };
                float[] instance = new float[VfxPlaybackRuntime.InstanceStride];
                instance[3] = instance[4] = instance[18] = 0.75f;
                instance[5] = instance[6] = instance[7] = instance[8] = 1;
                // Vertices sample near U=1.25: repeat reads red, clamp/mirror read blue.
                instance[19] = 1.25f;
                instance[20] = 0.5f;
                instance[21] = instance[22] = 0.001f;
                instance[31] = instance[32] = 1;
                instance[36] = instance[40] = instance[44] = 1;
                var emitter = new VfxPlaybackRuntime.EmitterState
                {
                    Def = definition, Texture = texture, TextureWidth = 2, TextureHeight = 1,
                    Instances = instance, InstanceCount = 1
                };
                emitter.ProgramTextures["custom.tex"] = texture;
                if (mesh)
                    renderer.UploadEmitterMesh(emitter,
                        new float[] { -1, -1, 0, 1, -1, 0, 0, 1, 0 },
                        new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                        new float[] { 0, 0, 1, 0, 0.5f, 1 }, null);

                byte[] pixels = Draw(gl, renderer, emitter, framebuffer);
                Assert.Null(renderer.GameParticleFallback(emitter, mesh));
                int channel = blue ? 2 : 0;
                Assert.Contains(Enumerable.Range(0, 64 * 64), at => pixels[at * 4 + channel] > 64);
                Assert.DoesNotContain(Enumerable.Range(0, 64 * 64), at => pixels[at * 4 + 2 - channel] > 16);
                Assert.Equal(pixels, Draw(gl, renderer, emitter, framebuffer));
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
