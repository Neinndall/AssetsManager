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
    public sealed class VfxSharedSamplerGpuTests
    {
        public static IEnumerable<object[]> SharedCases()
        {
            foreach (VfxPrimitiveKind primitive in new[] { VfxPrimitiveKind.CameraQuad,
                         VfxPrimitiveKind.ArbitraryQuad, VfxPrimitiveKind.Ray,
                         VfxPrimitiveKind.CameraTrail, VfxPrimitiveKind.ArbitraryTrail,
                         VfxPrimitiveKind.Beam, VfxPrimitiveKind.CameraSegmentBeam,
                         VfxPrimitiveKind.Mesh, VfxPrimitiveKind.PlanarProjection })
            foreach (byte uvMode in new byte[] { 0, 1, 2 })
            for (int baseAddress = 0; baseAddress < 4; baseAddress++)
            for (int multAddress = 0; multAddress < 4; multAddress++)
            foreach (bool game in new[] { false, true })
                yield return new object[] { primitive, uvMode, baseAddress, multAddress, game };
        }

        [Theory]
        [MemberData(nameof(SharedCases))]
        public void SharedTextureLayersMatchIndependentTextureCopies(
            VfxPrimitiveKind primitive, byte uvMode, int baseAddress, int multAddress, bool game)
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
            uint multiplier = Texture(gl, 64, 64, texels);
            try
            {
                var center = new Vector2(0.23f, 0.61f);
                var scale = new Vector2(0.65f, 0.8f);
                var offset = new Vector2(0.18f, 0.12f);
                const float rotation = 0.3f;
                Vector2 divisions = Vector2.One;
                bool mesh = primitive == VfxPrimitiveKind.Mesh;
                bool projectionKind = primitive == VfxPrimitiveKind.PlanarProjection;
                bool trail = primitive is VfxPrimitiveKind.CameraTrail or VfxPrimitiveKind.ArbitraryTrail;
                bool beam = primitive is VfxPrimitiveKind.Beam or VfxPrimitiveKind.CameraSegmentBeam;
                var definition = new VfxEmitterDefinition("screen", VfxCurveF.Const(1), VfxCurveF.Const(1),
                    null, 0, 0, false, false, 3, VfxCurve3.Const(Vector3.One), null,
                    VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                    "base.tex", divisions, 6, false, mesh, PrimitiveKind: primitive, UvMode: uvMode,
                    RenderState: VfxEmitterRenderState.Default with
                    {
                        DisableBackfaceCull = true, TextureAddressMode = baseAddress
                    }, UvTransformCenter: center, TextureMultPath: "base.tex",
                    TextureMultAddressMode: multAddress,
                    Projection: projectionKind ? new VfxProjectionDefinition() : null,
                    Beam: beam ? new VfxBeamDefinition(0, 0, 1, VfxCurve3.Const(Vector3.One),
                        VfxCurve4.Const(Vector4.One), false, Vector3.Zero, Vector3.Zero) : null,
                    Trail: trail ? new VfxTrailDefinition(VfxCurve3.Const(Vector3.One), 0, 0, 0, 0) : null);
                int count = trail ? 2 : 1;
                var instances = new float[count * VfxPlaybackRuntime.InstanceStride];
                var emitter = new VfxPlaybackRuntime.EmitterState
                {
                    Def = definition, Texture = gradient, TextureMult = multiplier,
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
                    instances[at + 29] = -0.3f;
                    instances[at + 30] = 0.2f;
                    instances[at + 31] = 0.75f; instances[at + 32] = 1.1f;
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
                if (mesh)
                    renderer.UploadEmitterMesh(emitter,
                        new float[] { -1, -1, 0, 1, -1, 0, 0, 1, 0 },
                        new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                        new float[] { 0, 0, 1, 0, 0.5f, 1 }, null);
                Matrix4x4 view = Matrix4x4.CreateLookAt(projectionKind ? new Vector3(0, 3, 0) : new Vector3(0, 0, 3),
                    Vector3.Zero, projectionKind ? Vector3.UnitZ : Vector3.UnitY);
                Matrix4x4 projection = Matrix4x4.CreateOrthographic(4, 4, 0.1f, 10);
                gl.Viewport(0, 0, 64, 64);
                byte[] Draw(uint secondary)
                {
                    emitter.TextureMult = secondary;
                    gl.ClearColor(0, 0, 0, 0);
                    gl.Clear(ClearBufferMask.ColorBufferBit);
                    renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) }, view * projection, view);
                    var pixels = new byte[64 * 64 * 4];
                    gl.ReadPixels(0, 0, 64, 64, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
                    return pixels;
                }
                Draw(multiplier); // Compile the selected game program before comparing its pixels.
                byte[] independent = Draw(multiplier);
                byte[] shared = Draw(gradient);
                Assert.Contains(independent, value => value != 0);
                Assert.Equal(independent, shared);
                Assert.Equal(independent, Draw(multiplier));
                if (game && !projectionKind && (mesh || uvMode == 0))
                    Assert.Null(renderer.GameParticleFallback(emitter, mesh));
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(framebuffer);
                foreach (uint texture in new[] { target, gradient, multiplier }) gl.DeleteTexture(texture);
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void StockSamplersPreserveTexturesRestoreCallerAndReleaseOwnedObjects(int addressMode)
        {
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            using var renderer = new VfxOpenGlRenderer();
            renderer.Initialize(gl);
            uint texture = Texture(gl, 1, 1, new byte[] { 255, 255, 255, 255 });
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.MirroredRepeat);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.MirroredRepeat);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            uint callerSampler = gl.GenSampler();
            uint[] units = { 0, 1, 7, 8 };
            foreach (uint unit in units) gl.BindSampler(unit, callerSampler);
            var definition = new VfxEmitterDefinition("state", VfxCurveF.Const(1), VfxCurveF.Const(1),
                null, 0, 0, false, false, 3, VfxCurve3.Const(Vector3.One), null,
                VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                "same.tex", Vector2.One, 1, false, false,
                RenderState: VfxEmitterRenderState.Default with
                { TextureAddressMode = addressMode, DisableBackfaceCull = true },
                TextureMultPath: "same.tex", TextureMultAddressMode: (addressMode + 1) % 4);
            var instances = new float[VfxPlaybackRuntime.InstanceStride];
            instances[3] = instances[4] = instances[18] = 0.5f;
            instances[5] = instances[6] = instances[7] = instances[8] = 1;
            instances[21] = instances[22] = instances[31] = instances[32] = 1;
            instances[36] = instances[40] = instances[44] = 1;
            var emitter = new VfxPlaybackRuntime.EmitterState
            { Def = definition, Texture = texture, TextureMult = texture, Instances = instances, InstanceCount = 1 };
            var queue = new[] { new VfxRenderQueueEntry(emitter, 0, 0) };
            var owned = new uint[units.Length];
            try
            {
                using (renderer.BeginRenderBatch())
                {
                    for (int frame = 0; frame < 10; frame++)
                    {
                        renderer.Render(queue, Matrix4x4.Identity, Matrix4x4.Identity);
                        for (int index = 0; index < units.Length; index++)
                        {
                            gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + units[index]));
                            gl.GetInteger(GLEnum.SamplerBinding, out int bound);
                            Assert.NotEqual(0, bound);
                            Assert.NotEqual(callerSampler, (uint)bound);
                            if (frame == 0) owned[index] = (uint)bound;
                            else Assert.Equal(owned[index], (uint)bound);
                        }
                    }
                }
                for (int index = 0; index < units.Length; index++)
                {
                    gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + units[index]));
                    gl.GetInteger(GLEnum.SamplerBinding, out int restored);
                    Assert.Equal(callerSampler, (uint)restored);
                }
                gl.BindTexture(TextureTarget.Texture2D, texture);
                gl.GetTexParameter(TextureTarget.Texture2D, GLEnum.TextureWrapS, out int wrapS);
                gl.GetTexParameter(TextureTarget.Texture2D, GLEnum.TextureWrapT, out int wrapT);
                gl.GetTexParameter(TextureTarget.Texture2D, GLEnum.TextureMinFilter, out int minFilter);
                gl.GetTexParameter(TextureTarget.Texture2D, GLEnum.TextureMagFilter, out int magFilter);
                Assert.Equal((int)TextureWrapMode.MirroredRepeat, wrapS);
                Assert.Equal((int)TextureWrapMode.MirroredRepeat, wrapT);
                Assert.Equal((int)TextureMinFilter.Nearest, minFilter);
                Assert.Equal((int)TextureMagFilter.Nearest, magFilter);
                renderer.Dispose();
                foreach (uint sampler in owned) Assert.False(gl.IsSampler(sampler));
                Assert.True(gl.IsSampler(callerSampler));
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally
            {
                foreach (uint unit in units) gl.BindSampler(unit, 0);
                gl.DeleteSampler(callerSampler);
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
