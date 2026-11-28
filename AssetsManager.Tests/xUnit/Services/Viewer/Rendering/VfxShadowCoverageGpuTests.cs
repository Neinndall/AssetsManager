using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Tests.Support;
using AssetsManager.Tests.xUnit.Services.Viewer.Vfx;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class VfxShadowCoverageGpuTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (bool mesh in new[] { false, true })
        foreach (float slice in new[] { 0f, .2f })
        for (int ramp = 0; ramp < 3; ramp++)
        foreach (float erosion in new[] { -1f, .5f, 3f })
        foreach (bool mult in new[] { false, true })
            yield return new object[] { mesh, slice, ramp, erosion, mult };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ShadowCoverageUsesItsOwnRampAndErosionRulesInsteadOfTheColorSlice(bool mesh, float slice, int ramp, float erosion, bool mult)
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new VfxOpenGlRenderer();
        renderer.Initialize(gl);
        uint depth = gl.GenTexture(), framebuffer = gl.GenFramebuffer();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, depth);
            gl.TexImage2D<float>(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent32f, 32, 32, 0,
                PixelFormat.DepthComponent, PixelType.Float, ReadOnlySpan<float>.Empty);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D, depth, 0);
            gl.DrawBuffer(DrawBufferMode.None); gl.ReadBuffer(ReadBufferMode.None);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
            var definition = VfxQualityFlexTests.Definition() with
            {
                DoesCastShadow = true, BlendMode = 3, IsMeshPrimitive = mesh,
                PrimitiveKind = mesh ? VfxPrimitiveKind.Mesh : VfxPrimitiveKind.CameraQuad,
                SliceRange = slice,
                AuthoredFeatures = mult ? new VfxEmitterAuthoredFeatures(HasTextureMultLayer: true) : null,
                AlphaErosion = erosion < 0 ? null : new VfxAlphaErosionDefinition(null, VfxCurveF.Const(erosion), 0, 0, 0),
                RenderState = VfxEmitterRenderState.Default with { DisableBackfaceCull = true, AlphaReference = 255 }
            };
            var instance = new float[VfxPlaybackRuntime.InstanceStride];
            instance[3] = instance[4] = .7f;
            instance[5] = instance[6] = instance[7] = instance[8] = instance[18] = 1;
            instance[21] = instance[22] = instance[31] = instance[32] = 1;
            instance[24] = MathF.Max(0, erosion); instance[28] = 1;
            instance[36] = instance[40] = instance[44] = 1;
            var state = new VfxPlaybackRuntime.EmitterState
            {
                Def = definition, InstanceCount = 1, Instances = instance,
                Texture = renderer.UploadTexture(new byte[] { 255, 255, 255, 255 }, 1, 1),
                ColorGradientTexture = ramp == 0 ? 0 : renderer.UploadTexture(new byte[] { 255, 255, 255, ramp == 1 ? (byte)0 : (byte)255 }, 1, 1)
            };
            if (mesh) renderer.UploadEmitterMesh(state,
                new float[] { -.7f, -.7f, 0, .7f, -.7f, 0, 0, .7f, 0 },
                new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, new float[] { 0, 0, 1, 0, .5f, 1 }, null);
            foreach (float alpha in new[] { 1f, .02f, .04f })
            {
                instance[8] = alpha;
                gl.Viewport(0, 0, 32, 32); gl.DepthMask(true); gl.ClearDepth(1);
                gl.Clear(ClearBufferMask.DepthBufferBit);
                renderer.RenderShadows(new[] { new VfxRenderQueueEntry(state, 0, 0) }, Matrix4x4.Identity, Matrix4x4.Identity);
                var center = new float[1];
                gl.ReadPixels<float>(16, 16, 1, 1, PixelFormat.DepthComponent, PixelType.Float, center);
                bool usesRamp = erosion < 0 && !(mesh && mult);
                bool covered = alpha >= .025f && erosion < 3f && !(usesRamp && ramp == 1);
                Assert.InRange(MathF.Abs(center[0] - (covered ? .5f : 1f)), 0, .00001f);
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer); gl.DeleteTexture(depth);
        }
    }

    [Fact]
    public void MapParticleRendererShadowPassMirrorsEngineParticlesIntoViewportSpace()
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new MapParticleRenderer();
        renderer.Initialize(gl);

        uint depth = gl.GenTexture();
        uint white = gl.GenTexture();
        uint framebuffer = gl.GenFramebuffer();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, white);
            gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba, 1, 1, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte, new byte[] { 255, 255, 255, 255 });
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

            gl.BindTexture(TextureTarget.Texture2D, depth);
            gl.TexImage2D<float>(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent32f, 64, 64, 0,
                PixelFormat.DepthComponent, PixelType.Float, ReadOnlySpan<float>.Empty);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                TextureTarget.Texture2D, depth, 0);
            gl.DrawBuffer(DrawBufferMode.None);
            gl.ReadBuffer(ReadBufferMode.None);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

            var emitter = VfxQualityFlexTests.Definition() with
            {
                DoesCastShadow = true,
                BlendMode = 3,
                IsSingleParticle = true,
                Rate = VfxCurveF.Const(1f),
                ParticleLifetime = VfxCurveF.Const(5f),
                BirthScale = VfxCurve3.Const(new Vector3(2f, 2f, 2f)),
                BirthColor = VfxCurve4.Const(Vector4.One),
                TexturePath = "white.dds",
                PrimitiveKind = VfxPrimitiveKind.CameraQuad,
                RenderState = VfxEmitterRenderState.Default with { DisableBackfaceCull = true, AlphaReference = 255 }
            };
            var system = new VfxSystemDefinition(42u, "shadow_map_particle", "Effects/ShadowMapParticle", new[] { emitter });
            var placed = new MapPlaceableData(
                1u,
                2u,
                MapParticleParser.MapParticleClass,
                "shadow_at_plus4",
                Matrix4x4.CreateTranslation(4f, 0f, 0f),
                MapPlaceableData.EveryLayer,
                null,
                new Dictionary<uint, BinTreeProperty>());
            var placement = new MapParticleData(placed, 42u, false, false);
            MapParticleRuntime runtime = MapParticleRuntime.Create(
                placement,
                system,
                new Dictionary<uint, VfxSystemDefinition> { [42u] = system },
                new Dictionary<uint, uint>());
            runtime.Graph.Root.Emitters[0].Texture = white;
            runtime.Graph.Root.Emitters[0].TextureWidth = 1;
            runtime.Graph.Root.Emitters[0].TextureHeight = 1;
            runtime.Advance(0.05f);

            Matrix4x4 eyeView = Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 10f), Vector3.Zero, Vector3.UnitY);
            Matrix4x4 ortho = Matrix4x4.CreateOrthographic(20f, 20f, 0.1f, 100f);
            Matrix4x4 viewProjection = eyeView * ortho;

            Assert.True(renderer.PrepareRenderFrame(new[] { runtime }, viewProjection, eyeView, 64, 64));
            IPreparedParticlePass pass = renderer;
            Assert.True(pass.HasPreparedShadowCasters);

            gl.Viewport(0, 0, 64, 64);
            gl.DepthMask(true);
            gl.ClearDepth(1d);
            gl.Clear(ClearBufferMask.DepthBufferBit);
            pass.RenderPreparedShadowPass(viewProjection, eyeView);

            var depths = new float[64 * 64];
            gl.ReadPixels<float>(0, 0, 64, 64, PixelFormat.DepthComponent, PixelType.Float, depths);

            float DepthAtViewportX(float viewportX)
            {
                int px = Math.Clamp((int)(((viewportX / 10f) + 1f) * 0.5f * 64f), 0, 63);
                return depths[32 * 64 + px];
            }

            Assert.True(DepthAtViewportX(-4f) < 0.9f,
                $"Expected MAP particle at engine X=+4 to cast shadow at viewport X=-4 (depth={DepthAtViewportX(-4f)}).");
            Assert.Equal(1f, DepthAtViewportX(4f));
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteTexture(depth);
            gl.DeleteTexture(white);
        }
    }
}
