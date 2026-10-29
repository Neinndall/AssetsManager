using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

public sealed class CharacterPbrGpuTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProductionRenderersConnectMaterialMapsAndKeepUnshadedModeIndependent(bool map)
    {
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var draw = new Draw(map);
                var lit = draw.HostPixel(map, 255, StudioViewMode.Lit);
                var occluded = draw.HostPixel(map, 0, StudioViewMode.Lit);
                Assert.True(Difference(lit, occluded) > 5);
                Assert.Equal(lit, draw.HostPixel(map, 255, StudioViewMode.Lit, late: true));
                Assert.Equal(draw.HostPixel(map, 255, StudioViewMode.Unshaded), draw.HostPixel(map, 0, StudioViewMode.Unshaded));
                Assert.Equal(GLEnum.NoError, draw.Gl.GetError());
            }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(45)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PixelsMatchTheExactReferenceMaterialRenderedByThree185(bool map)
    {
        // Captured independently in Edge/WebGL using gridPbrMaterial from b75b5c85 and Three 0.185.1.
        using var draw = new Draw(map);
        void Check(byte[] actual, params byte[] expected)
        {
            Assert.InRange(Difference(actual, expected), 0, 2);
            Assert.Equal(255, actual[3]);
        }
        Check(draw.Pixel(128, 0, 255), 158, 88, 61);
        Check(draw.Pixel(128, 255, 255), 149, 68, 25);
        Check(draw.Pixel(128, 0, 0), 0, 0, 0);
        Check(draw.Pixel(128, 0, 255, direct: true), 84, 43, 27);
        Check(draw.Pixel(128, 0, 0, direct: true), 84, 43, 27);
        Check(draw.Pixel(10, 255, 255, direct: true), 0, 0, 0);
        Check(draw.Pixel(240, 255, 255, direct: true), 48, 17, 3);
        Check(draw.Pixel(128, 0, 255, direct: true, normal: new byte[] { 128, 204, 0, 255 }), 94, 49, 31);
        Check(draw.Pixel(128, 0, 255, direct: true, normal: new byte[] { 128, 204, 255, 255 }), 94, 49, 31);
        Check(draw.Pixel(128, 0, 255, grid: true), 77, 40, 25);
        Check(draw.Pixel(128, 255, 255, grid: true), 72, 29, 7);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothCharacterShadersReadRmaAndReconstructNormalZWithoutDarkeningDirectLight(bool map)
    {
        using var draw = new Draw(map);
        byte[] dielectric = draw.Pixel(128, 0, 255);
        byte[] metal = draw.Pixel(128, 255, 255);
        Assert.True(Difference(dielectric, metal) > 5);
        Assert.True(dielectric.Take(3).Sum(value => (int)value) > 20);
        Assert.All(draw.Pixel(128, 0, 0).Take(3), value => Assert.Equal(0, value));
        byte[] direct = draw.Pixel(128, 0, 255, direct: true);
        Assert.Equal(direct, draw.Pixel(128, 0, 0, direct: true));
        Assert.True(Difference(draw.Pixel(10, 255, 255, direct: true),
            draw.Pixel(240, 255, 255, direct: true)) > 3);
        byte[] normal = draw.Pixel(128, 0, 255, direct: true, normal: new byte[] { 128, 204, 0, 255 });
        Assert.Equal(normal, draw.Pixel(128, 0, 255, direct: true, normal: new byte[] { 128, 204, 255, 255 }));
        Assert.True(Difference(normal, direct) > 3);
        Assert.Equal(direct, draw.Pixel(128, 0, 255, direct: true,
            normal: new byte[] { 128, 204, 0, 255 }, tangents: false));
        byte[] mirrored = draw.Pixel(128, 0, 255, direct: true, normal: new byte[] { 128, 204, 0, 255 }, mirror: true);
        Assert.InRange(Difference(normal, mirrored), 0, 3);
        Assert.Equal(draw.Pixel(128, 0, 255, enabled: false), draw.Pixel(10, 255, 0, enabled: false));
        Assert.Equal(GLEnum.NoError, draw.Gl.GetError());
    }

    [Fact]
    public void MapsCanArriveAndLeaveAndBindingsPreserveDiffuseAndReleaseTheirHandles()
    {
        using var draw = new Draw(false);
        byte[] absent = draw.Pixel(128, 0, 255, missing: true);
        byte[] present = draw.Pixel(128, 0, 255);
        Assert.True(Difference(absent, present) > 3);
        Assert.Equal(absent, draw.Pixel(128, 0, 255, missing: true));
        Assert.Equal(present, draw.Pixel(128, 0, 255));
        draw.Bindings.Reset();
        for (uint unit = 1; unit <= 3; unit++)
        {
            draw.Gl.ActiveTexture((TextureUnit)((uint)TextureUnit.Texture0 + unit));
            Assert.Equal(0, draw.Gl.GetInteger(GetPName.TextureBinding2D));
        }
        draw.Gl.ActiveTexture(TextureUnit.Texture0);
        Assert.Equal(GLEnum.NoError, draw.Gl.GetError());
    }

    private static int Difference(byte[] left, byte[] right) => left.Take(3).Zip(right).Max(pair => Math.Abs(pair.First - pair.Second));

    private sealed class Draw : IDisposable
    {
        private readonly HiddenWglContext _context = new();
        internal readonly GL Gl;
        internal readonly CharacterPbrBindings Bindings;
        private readonly uint _program, _vao, _vertices, _base, _rma, _normal, _frame, _target;
        private readonly bool _map;
        private readonly ModelMaterialDefinition _material = ModelMaterialDefinition.TextureOnly("base") with { Pbr = new("rma", "normal") };

        internal Draw(bool map)
        {
            _map = map;
            Gl = GL.GetApi(_context.GetProcAddress);
            _program = GlShaderCompiler.CreateProgram(Gl, false, map ? MapCharacterShaderSource.Vertex : GlMeshShaderSource.Vertex,
                map ? MapCharacterShaderSource.Fragment : GlMeshShaderSource.Fragment);
            Bindings = new CharacterPbrBindings(Gl, _program);
            _vao = Gl.GenVertexArray();
            Gl.BindVertexArray(_vao);
            _vertices = Gl.GenBuffer();
            Gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vertices);
            float[] positions = { -1, -1, 0, 1, -1, 0, -1, 1, 0, 1, 1, 0 };
            Gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(positions), BufferUsageARB.StaticDraw);
            Gl.EnableVertexAttribArray(0);
            Gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 0, IntPtr.Zero);
            Gl.VertexAttrib3(1, 0f, 0f, 1f); Gl.VertexAttrib2(2, 0.5f, 0.5f); Gl.VertexAttrib4(3, 1f, 0f, 0f, 1f);
            _base = Texture(new byte[] { 153, 71, 26, 255 }, InternalFormat.Srgb8Alpha8);
            _rma = Texture(new byte[] { 128, 0, 255, 255 }); _normal = Texture(new byte[] { 128, 128, 255, 255 });
            _target = Gl.GenTexture(); Gl.BindTexture(TextureTarget.Texture2D, _target);
            Gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 32, 32, 0, PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
            _frame = Gl.GenFramebuffer(); Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _frame);
            Gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _target, 0);
            Assert.Equal(GLEnum.FramebufferComplete, Gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
        }

        internal byte[] Pixel(byte rough, byte metal, byte ao, bool direct = false, byte[] normal = null,
            bool mirror = false, bool enabled = true, bool missing = false, bool grid = false, bool tangents = true)
        {
            Gl.ActiveTexture(TextureUnit.Texture0);
            Update(_rma, new[] { rough, metal, ao, (byte)255 });
            Update(_normal, normal ?? new byte[] { 128, 128, 255, 255 });
            Gl.UseProgram(_program);
            int U(string name) => Gl.GetUniformLocation(_program, name);
            Matrix4x4 identity = Matrix4x4.Identity, world = Matrix4x4.CreateScale(mirror ? -1 : 1, 1, 1);
            Gl.UniformMatrix4(U(_map ? "uViewProjection" : "uViewProj"), 1, false, in identity.M11);
            Gl.UniformMatrix4(U("uWorld"), 1, false, in world.M11);
            Gl.Uniform1(U(_map ? "uBaseTexture" : "uTex"), 0);
            Gl.Uniform1(U("uHasTexture"), 1); Gl.Uniform1(U("uOpacity"), 1f);
            Gl.Uniform3(U("uColor"), Vector3.One); Gl.Uniform4(U("uColorTint"), Vector4.One);
            Gl.Uniform2(U(_map ? "uUvRepeat" : "uMaterialUvRepeat"), 1f, 1f);
            Gl.Uniform1(U("uMaterialSrgb"), 1); Gl.Uniform1(U("uLit"), enabled ? 1 : 0);
            Gl.Uniform1(U("uMaterialUnlit"), enabled ? 0 : 1);
            Gl.Uniform1(U("uLightGridOn"), grid ? 1 : 0);
            for (int face = 0; face < 6; face++) Gl.Uniform3(U($"uLightGridCube[{face}]"), new Vector3(0.5f));
            Bindings.Light(new(0, 0, 3), Vector3.Normalize(new Vector3(0, 0.6f, 0.8f)), direct ? Vector3.One : Vector3.Zero,
                Vector3.UnitZ, Vector3.Zero, Vector3.One, Vector3.One, direct ? 0 : MathF.PI);
            Gl.BindTexture(TextureTarget.Texture2D, _base);
            Bindings.Bind(_material, path =>
            {
                // A first upload binds a data texture on the active unit; the diffuse binding must survive it.
                Gl.BindTexture(TextureTarget.Texture2D, _normal);
                return missing ? null : path == "rma" ? _rma : _normal;
            }, tangents, enabled);
            Assert.Equal((int)_base, Gl.GetInteger(GetPName.TextureBinding2D));
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _frame); Gl.Viewport(0, 0, 32, 32);
            Gl.ClearColor(0, 0, 0, 0); Gl.Clear(ClearBufferMask.ColorBufferBit);
            Gl.Disable(EnableCap.DepthTest); Gl.Disable(EnableCap.Blend); Gl.Disable(EnableCap.CullFace);
            Gl.FrontFace(mirror ? FrontFaceDirection.CW : FrontFaceDirection.Ccw);
            Gl.BindVertexArray(_vao); Gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
            byte[] pixel = new byte[4];
            Gl.ReadPixels(16, 16, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, pixel.AsSpan());
            return pixel;
        }

        private uint Texture(byte[] value, InternalFormat format = InternalFormat.Rgba8)
        {
            uint id = Gl.GenTexture(); Gl.BindTexture(TextureTarget.Texture2D, id);
            Gl.TexImage2D(TextureTarget.Texture2D, 0, format, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, new ReadOnlySpan<byte>(value));
            Gl.GenerateMipmap(TextureTarget.Texture2D);
            Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            return id;
        }

        internal byte[] HostPixel(bool map, byte occlusion, StudioViewMode mode, bool late = false)
        {
            BitmapSource Bitmap(params byte[] bytes)
            {
                var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, bytes, 4);
                bitmap.Freeze(); return bitmap;
            }
            var textures = new Dictionary<string, BitmapSource> { ["base"] = Bitmap(26, 71, 153, 255), ["rma"] = Bitmap(occlusion, 0, 128, 255) };
            BitmapSource arriving = textures["rma"];
            if (late) textures.Remove("rma");
            void Arrive()
            {
                textures["rma"] = arriving;
                Gl.Clear(ClearBufferMask.ColorBufferBit);
            }
            var material = ModelMaterialDefinition.TextureOnly("base") with { Pbr = new("rma", null) };
            Vector3[] positions = { new(-1,-1,0), new(1,-1,0), new(-1,1,0), new(1,1,0) };
            var normals = Enumerable.Repeat(Vector3.UnitZ, 4).ToArray();
            var uv = Enumerable.Repeat(new Vector2(.5f), 4).ToArray();
            uint[] indices = { 0,1,2, 2,1,3 };
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _frame); Gl.Viewport(0,0,32,32);
            Gl.ClearColor(0,0,0,0); Gl.Clear(ClearBufferMask.ColorBufferBit);
            if (map)
            {
                var mesh = new MapCharacterMeshData(positions, normals, uv, null, null, null, indices,
                    new[] { new MapCharacterMeshRange("Body", 0, 6) });
                var asset = new MapCharacterAssetData(new MapCharacterSkinData("test",null,null,1,Array.Empty<string>(),0), mesh, null,
                    new SknMaterialTextureResolution(material, new Dictionary<string, ModelMaterialDefinition>()), textures, null);
                var placement = new MapCharacterData(new MapPlaceableData(1,1,0,"test",Matrix4x4.Identity,255,null,null), "test", null, null);
                using var group = new MapCharacterRuntimeGroup(asset, new MapCharacterAnimationRuntime(null,null), new[] { placement });
                using var renderer = new MapCharacterRenderer(); renderer.Initialize(Gl);
                renderer.Render(new[] { group }, Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity,
                    new Vector3(0,0,3), 0, null, viewMode: mode, shadersEnabled: true);
                if (late)
                {
                    Arrive();
                    renderer.Render(new[] { group }, Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity,
                        new Vector3(0,0,3), 0, null, viewMode: mode, shadersEnabled: true);
                }
            }
            else
            {
                var geometry = new MeshGeometry3D
                {
                    Positions = new Point3DCollection(positions.Select(p => new Point3D(p.X,p.Y,p.Z))),
                    Normals = new Vector3DCollection(normals.Select(n => new Vector3D(n.X,n.Y,n.Z))),
                    TextureCoordinates = new System.Windows.Media.PointCollection(uv.Select(p => new System.Windows.Point(p.X,p.Y))),
                    TriangleIndices = new Int32Collection(indices.Select(i => (int)i))
                };
                using var model = new SceneModel();
                model.Parts.Add(new ModelPart("Body", new GeometryModel3D { Geometry = geometry })
                    { AllTextures = textures, SelectedTextureName = "base", MaterialDefinition = material });
                using var renderer = new GlMeshRenderer(); renderer.Initialize(Gl);
                renderer.Render(model, Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity,
                    new Vector3(0,0,3), Vector3.UnitZ, Vector3.Zero, Vector3.UnitZ, Vector3.Zero, new Vector3(MathF.PI),
                    viewMode: mode, shadersEnabled: true);
                if (late)
                {
                    Arrive();
                    renderer.Render(model, Matrix4x4.Identity, Matrix4x4.Identity, Matrix4x4.Identity,
                        new Vector3(0,0,3), Vector3.UnitZ, Vector3.Zero, Vector3.UnitZ, Vector3.Zero, new Vector3(MathF.PI),
                        viewMode: mode, shadersEnabled: true);
                }
            }
            byte[] result = new byte[4];
            Gl.ReadPixels(16,16,1,1,PixelFormat.Rgba,PixelType.UnsignedByte,result.AsSpan());
            return result;
        }

        private void Update(uint id, byte[] value)
        {
            Gl.BindTexture(TextureTarget.Texture2D, id);
            Gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, new ReadOnlySpan<byte>(value));
        }

        public void Dispose()
        {
            Bindings.Reset(); Bindings.Dispose();
            foreach (uint texture in new[] { _base, _rma, _normal, _target }) Gl.DeleteTexture(texture);
            Gl.DeleteFramebuffer(_frame); Gl.DeleteBuffer(_vertices); Gl.DeleteVertexArray(_vao); Gl.DeleteProgram(_program);
            Gl.Dispose(); _context.Dispose();
        }
    }
}
