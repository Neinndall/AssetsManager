using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class VfxSurfaceAlignmentTests
{
    private static VfxEmitterDefinition Definition(VfxPrimitiveKind kind = VfxPrimitiveKind.AttachedMesh) =>
        new("surface", VfxCurveF.Const(1), VfxCurveF.Const(1), null, 0, 0, false, false, 3,
            VfxCurve3.Const(Vector3.One), null, VfxCurve4.Const(Vector4.One), null, null, null, null,
            VfxCurve3.Const(Vector3.Zero), "white.tex", Vector2.One, 1, false, true,
            PrimitiveKind: kind, DepthPushPull: -1,
            RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true });

    [Theory]
    [InlineData(VfxPrimitiveKind.AttachedMesh, false, -1f)]
    [InlineData(VfxPrimitiveKind.AttachedMesh, true, 0f)]
    [InlineData(VfxPrimitiveKind.Mesh, true, -1f)]
    [InlineData(VfxPrimitiveKind.CameraQuad, true, -1f)]
    [InlineData(VfxPrimitiveKind.CameraTrail, true, -1f)]
    public void AlignmentPreservesOffsetsOutsideStandardAttachedSurfaces(VfxPrimitiveKind kind, bool align, float expected)
    {
        var definition = Definition(kind);
        var parameters = new Dictionary<string, Vector4>();
        VfxShaderParameterUtils.PopulateNativeParameters(parameters, definition, 0, align);
        Assert.Equal(expected, parameters["PARTICLE_DEPTH_PUSH_PULL"].X);
        Assert.Equal(-1f, definition.DepthPushPull);
    }

    [Fact]
    public void CustomMaterialsAndDistortionKeepTheirAuthoredOffset()
    {
        var custom = Definition() with { CustomMaterial = ModelMaterialDefinition.TextureOnly("white") };
        var distortion = Definition() with { Distortion = new VfxDistortionDefinition(1, 1, "normal.tex") };
        Assert.True(custom.HasResolvedCustomMaterial);
        Assert.True(distortion.DrawsAsDistortion);
        foreach (var definition in new[] { custom, distortion })
        {
            var parameters = new Dictionary<string, Vector4>();
            VfxShaderParameterUtils.PopulateNativeParameters(parameters, definition, 0, true);
            Assert.Equal(-1f, parameters["PARTICLE_DEPTH_PUSH_PULL"].X);
        }
    }

    [Fact]
    public void NativeSurfaceAlignmentChangesDepthAndRestoresAuthoredOffsetWithoutReload()
    {
        string install = InstalledSkins.FindInstall();
        Assert.NotNull(install);
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var renderer = new VfxOpenGlRenderer();
        Assert.False(renderer.AlignSurfaceEffects);
        renderer.Initialize(gl, InstalledSkins.Settings(install));
        uint color = gl.GenTexture();
        uint white = gl.GenTexture();
        uint depth = gl.GenRenderbuffer();
        uint framebuffer = gl.GenFramebuffer();
        try
        {
            gl.BindTexture(TextureTarget.Texture2D, color);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 64, 64, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
            gl.BindTexture(TextureTarget.Texture2D, white);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, new byte[] { 255, 255, 255, 255 }.AsSpan());
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, 64, 64);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, color, 0);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);
            Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

            var instance = new float[VfxPlaybackRuntime.InstanceStride];
            instance[3] = instance[4] = instance[18] = 1;
            instance[5] = instance[6] = instance[7] = instance[8] = 1;
            instance[21] = instance[22] = instance[31] = instance[32] = 1;
            instance[36] = instance[40] = instance[44] = 1;
            var emitter = new VfxPlaybackRuntime.EmitterState
            {
                Def = Definition(), Texture = white, TextureWidth = 1, TextureHeight = 1,
                Instances = instance, InstanceCount = 1, MeshOwnerScale = 1
            };
            renderer.UploadEmitterMesh(emitter, new float[] { -1, -1, 0, 1, -1, 0, 0, 1, 0 },
                new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 }, new float[] { 0, 0, 1, 0, .5f, 1 }, null);
            Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(0, 0, 5), Vector3.Zero, Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 3, 1, .1f, 100);
            float Sample(bool align)
            {
                renderer.AlignSurfaceEffects = align;
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                gl.Viewport(0, 0, 64, 64);
                gl.DepthMask(true);
                gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, view * projection, view);
                Assert.Null(renderer.GameParticleFallback(emitter, true));
                var pixel = new float[1];
                gl.ReadPixels(32, 32, 1, 1, PixelFormat.DepthComponent, PixelType.Float, pixel.AsSpan());
                Assert.InRange(pixel[0], 0f, .999f);
                return pixel[0];
            }

            float authored = Sample(false), aligned = Sample(true), restored = Sample(false);
            Assert.True(aligned > authored + .001f, $"Authored={authored}, aligned={aligned}");
            Assert.Equal(authored, restored);
            Assert.Equal(-1f, emitter.Def.DepthPushPull);
            Assert.Equal(GLEnum.NoError, gl.GetError());
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteRenderbuffer(depth);
            gl.DeleteTexture(color);
            gl.DeleteTexture(white);
        }
    }
}
