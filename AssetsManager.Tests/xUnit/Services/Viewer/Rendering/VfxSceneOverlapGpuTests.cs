using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Tests.Support;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Environment;
using Silk.NET.OpenGL;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class VfxSceneOverlapGpuTests
{
    public static IEnumerable<object[]> SurfaceCases()
    {
        foreach (int host in new[] { 0, 1, 2 })
        foreach (bool transparent in new[] { false, true })
        foreach (bool depthWrite in new[] { false, true })
        foreach (bool reverseOwners in new[] { false, true })
        foreach (bool native in new[] { false, true })
            yield return new object[] { host, transparent, depthWrite, reverseOwners, native };
    }

    [Theory]
    [MemberData(nameof(SurfaceCases))]
    public void OwnerPhasesCompositeOverRealSurfacesAndRespectTheirDepth(
        int host, bool transparent, bool depthWrite, bool reverseOwners, bool native)
    {
        using var context = new HiddenWglContext();
        using GL gl = GL.GetApi(context.GetProcAddress);
        using var particles = new VfxOpenGlRenderer();
        string install = InstalledSkins.FindInstall();
        if (native) Assert.NotNull(install);
        particles.Initialize(gl, native ? InstalledSkins.Settings(install) : null);
        uint framebuffer = gl.GenFramebuffer(), color = gl.GenRenderbuffer(), depth = gl.GenRenderbuffer();
        uint white = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, white);
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, new byte[] { 255, 255, 255, 255 }.AsSpan());
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, color);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, 64, 64);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
            RenderbufferTarget.Renderbuffer, color);
        gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
        gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, 64, 64);
        gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            RenderbufferTarget.Renderbuffer, depth);
        Assert.Equal(GLEnum.FramebufferComplete, gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));

        Vector3[] positions = { new(-1, -1, 0), new(1, -1, 0), new(-1, 1, 0), new(1, 1, 0) };
        var normals = Enumerable.Repeat(Vector3.UnitZ, 4).ToArray();
        var uv = Enumerable.Repeat(new Vector2(.5f), 4).ToArray();
        uint[] indices = { 0, 1, 2, 2, 1, 3 };
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 64, 160, 32, transparent ? (byte)128 : (byte)255 }, 4);
        bitmap.Freeze();
        var textures = new Dictionary<string, BitmapSource> { ["surface"] = bitmap };
        var material = ModelMaterialDefinition.TextureOnly("surface") with
        {
            BindingKind = ModelMaterialBindingKind.Authored,
            AlphaCutoff = 0,
            RenderState = ModelMaterialRenderState.Default with
            {
                Blending = transparent ? ModelMaterialBlendMode.Normal : ModelMaterialBlendMode.Opaque,
                DepthWrite = depthWrite, DoubleSided = true
            }
        };
        using var model = new SceneModel();
        var geometry = new MeshGeometry3D
        {
            Positions = new Point3DCollection(positions.Select(p => new Point3D(p.X, p.Y, p.Z))),
            Normals = new Vector3DCollection(normals.Select(n => new Vector3D(n.X, n.Y, n.Z))),
            TextureCoordinates = new System.Windows.Media.PointCollection(uv.Select(p => new System.Windows.Point(p.X, p.Y))),
            TriangleIndices = new Int32Collection(indices.Select(i => (int)i))
        };
        model.Parts.Add(new ModelPart("surface", new GeometryModel3D { Geometry = geometry })
            { AllTextures = textures, SelectedTextureName = "surface", MaterialDefinition = material });
        var mesh = new MapCharacterMeshData(positions, normals, uv, null, null, null, indices,
            new[] { new MapCharacterMeshRange("surface", 0, 6) });
        var asset = new MapCharacterAssetData(new MapCharacterSkinData("surface", null, null, 1, Array.Empty<string>(), 0),
            mesh, null, new SknMaterialTextureResolution(material, new Dictionary<string, ModelMaterialDefinition>()), textures, null);
        var placement = new MapCharacterData(new MapPlaceableData(1, 1, 0, "surface", Matrix4x4.Identity, 255, null, null),
            "surface", null, null);
        using var group = new MapCharacterRuntimeGroup(asset, new MapCharacterAnimationRuntime(null, null), new[] { placement });
        using var characterRenderer = new GlMeshRenderer { OutlinesEnabled = false };
        using var mapRenderer = new MapCharacterRenderer { OutlinesEnabled = false };
        using var terrainRenderer = new MapGeometryRenderer();
        if (host == 2)
        {
            terrainRenderer.Initialize(gl);
            var terrain = new MapGeometryData(positions, normals, uv, null, indices,
                new[] { new MapGeometryMeshData(new Vector3(-1, -1, 0), new Vector3(1, 1, 0), 255,
                    (byte)EnvironmentQuality.AllQualities, MapGeometryMeshFlags.CullDisabled, 0, 1,
                    EnvironmentAssetMeshRenderFlags.Default, 0, 0) },
                new[] { new MapGeometrySubmeshData(0, 6, 0) }, new[] { "surface" });
            var terrainMaterial = new MapMaterialDefinition("surface", 0, false, false, null,
                new MapBaseTexture("base", new MapTextureReference("surface", 0), MapMaterialBaseRule.Exact,
                    MapTextureWrap.Clamp, MapTextureWrap.Clamp),
                null, null, null, null, null,
                MapMaterialRenderState.Default with
                {
                    Blending = transparent ? MapMaterialBlendMode.Normal : MapMaterialBlendMode.Opaque,
                    SourceFactor = transparent ? MapBlendFactor.SourceAlpha : MapBlendFactor.One,
                    DestinationFactor = transparent ? MapBlendFactor.OneMinusSourceAlpha : MapBlendFactor.Zero,
                    DoubleSided = true, DepthWrite = depthWrite
                }, Array.Empty<string>());
            terrainRenderer.LoadScene(new MapSceneData(null, null, terrain, null, new[] { terrainMaterial },
                new Dictionary<string, MapTextureImage> { ["surface"] = new(new[] { bitmap }) },
                Array.Empty<MapPlaceableChunkData>(), Array.Empty<MapCharacterData>(), Array.Empty<MapParticleData>(), null));
        }
        else if (host == 1) mapRenderer.Initialize(gl);
        else characterRenderer.Initialize(gl);
        try
        {
            Clear();
            Surface();
            byte[] baseline = Pixel();
            Assert.True(baseline[1] > 40, "The real surface renderer must cover the sampled pixel.");
            Assert.InRange((int)baseline[3], transparent ? 126 : 254, transparent ? 130 : 255);
            foreach (bool behind in new[] { false, true })
            {
                Clear();
                float z = behind ? .2f : -.2f;
                Vector4 red = new(.8f, 0, 0, .5f), blue = new(0, 0, .8f, .5f);
                var redEmitter = State("red", red, z, 10);
                var blueEmitter = State("blue", blue, z, 0);
                var redOwner = new Owner(particles, redEmitter);
                var blueOwner = new Owner(particles, blueEmitter);
                IPreparedParticlePass[] owners = reverseOwners ? new Owner[] { blueOwner, redOwner } : new Owner[] { redOwner, blueOwner };
                PreparedParticlePasses.Render(owners, new List<IDisposable>(), Surface);
                Vector4 expected = new(baseline[0] / 255f, baseline[1] / 255f, baseline[2] / 255f, baseline[3] / 255f);
                if (!behind || !depthWrite)
                    foreach (Vector4 source in reverseOwners ? new[] { blue, red } : new[] { red, blue })
                        expected = source * source.W + expected * (1 - source.W);
                byte[] actual = Pixel();
                for (int channel = 0; channel < 4; channel++)
                    Assert.True(Math.Abs(actual[channel] - expected[channel] * 255) <= 3,
                        $"host={host}, transparent={transparent}, depthWrite={depthWrite}, reverse={reverseOwners}, native={native}, behind={behind}, channel={channel}: expected {expected[channel] * 255}, got {actual[channel]}.");
                var actualDepth = new float[1];
                gl.ReadPixels(32, 32, 1, 1, PixelFormat.DepthComponent, PixelType.Float, actualDepth.AsSpan());
                Assert.InRange(Math.Abs(actualDepth[0] - (depthWrite ? .5f : 1f)), 0, .00001f);
                if (native) Assert.Null(particles.GameParticleFallback(redEmitter, false));
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
        }
        finally
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteRenderbuffer(color); gl.DeleteRenderbuffer(depth); gl.DeleteTexture(white);
        }

        void Clear()
        {
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer); gl.Viewport(0, 0, 64, 64);
            gl.DepthMask(true); gl.ColorMask(true, true, true, true); gl.Disable(EnableCap.ScissorTest);
            gl.ClearColor(0, 0, 0, 0); gl.ClearDepth(1);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        }
        void Surface()
        {
            if (host == 2)
                terrainRenderer.Render(Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity,
                    new Vector3(0, 0, 3), 0, viewMode: StudioViewMode.Unshaded, shadersEnabled: false);
            else if (host == 1)
                mapRenderer.Render(new[] { group }, Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity,
                    new Vector3(0, 0, 3), 0, null, viewMode: StudioViewMode.Unshaded, shadersEnabled: false);
            else
                characterRenderer.Render(model, Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity,
                    new Vector3(0, 0, 3), Vector3.UnitZ, Vector3.Zero, Vector3.UnitZ, Vector3.Zero, Vector3.One,
                    viewMode: StudioViewMode.Unshaded, shadersEnabled: false);
        }
        byte[] Pixel()
        {
            var pixel = new byte[4];
            gl.ReadPixels(32, 32, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel.AsSpan());
            return pixel;
        }
        VfxPlaybackRuntime.EmitterState State(string name, Vector4 tint, float z, int pass)
        {
            var definition = new VfxEmitterDefinition(name, VfxCurveF.Const(1), VfxCurveF.Const(1),
                null, 0, 0, false, false, 1, VfxCurve3.Const(Vector3.One), null, VfxCurve4.Const(Vector4.One),
                null, null, null, null, VfxCurve3.Const(Vector3.Zero), "white.tex", Vector2.One, 1, false, false,
                PrimitiveKind: VfxPrimitiveKind.CameraQuad,
                RenderState: VfxEmitterRenderState.Default with { DisableBackfaceCull = true, RenderPass = pass });
            var row = new float[VfxPlaybackRuntime.InstanceStride];
            row[2] = z; row[3] = row[4] = row[18] = .5f;
            row[5] = tint.X; row[6] = tint.Y; row[7] = tint.Z; row[8] = tint.W;
            row[21] = row[22] = row[31] = row[32] = row[36] = row[40] = row[44] = 1;
            return new VfxPlaybackRuntime.EmitterState
                { Def = definition, Texture = white, TextureWidth = 1, TextureHeight = 1, Instances = row, InstanceCount = 1 };
        }
    }

    // Each owner retains its queue, as separate prepared Studio sessions do. Cross-owner pass
    // priorities are deliberately opposite to the owner sequence to expose that boundary.
    private sealed class Owner(VfxOpenGlRenderer renderer, VfxPlaybackRuntime.EmitterState emitter) : IPreparedParticlePass
    {
        public IDisposable BeginPreparedRenderBatch() => renderer.BeginRenderBatch();
        public void RenderPreparedColorPass() { }
        public void RenderPreparedPostColorPass() => renderer.Render(new[] { new VfxRenderQueueEntry(emitter, 0, 0) },
            Matrix4x4.Identity, Matrix4x4.Identity);
        public void CapturePreparedDistortionFrame() { }
        public void RenderPreparedDistortionPass() { }
    }
}
