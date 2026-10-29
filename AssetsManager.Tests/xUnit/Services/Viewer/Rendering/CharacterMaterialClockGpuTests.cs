using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Viewer.Animation;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Tests.Support;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Mesh;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class CharacterMaterialClockGpuTests
{
    [InstalledChampionFact("Akali")]
    public async Task StudioMaterialPausesSeeksAndUsesTheSuppliedActorClock()
    {
        await WithSkin((asset, skin, animation, clip, settings) =>
        {
            using var target = new Target();
            using var renderer = new GlMeshRenderer(settings) { SuppressBloom = true };
            renderer.Initialize(target.Gl);
            using var model = Model(asset, skin);
            model.SkinningMatrices = animation.EvaluateClip(asset, clip, 1);
            var light = GlMeshRenderer.ReferenceCharacterLighting();
            byte[] Draw(double? time = null)
            {
                target.Clear();
                renderer.Render(model, target.ViewProjection, target.View, target.Projection, Target.Eye,
                    light.LightDirection, light.LightColor, light.FillDirection, light.FillColor, light.AmbientColor,
                    shadersEnabled: true, mirrorCharacterX: true, timeSeconds: time);
                Assert.Equal(GLEnum.NoError, target.Gl.GetError());
                return target.Pixels();
            }

            model.AnimationTime = .125;
            byte[] paused = Draw();
            Thread.Sleep(50);
            Assert.Equal(paused, Draw());
            model.AnimationTime = .825;
            Assert.False(paused.SequenceEqual(Draw()), "The original body shader must still animate when the clock advances.");
            model.AnimationTime = .125;
            Assert.Equal(paused, Draw());
            model.AnimationTime = 99;
            Assert.Equal(paused, Draw(.125));
            model.AnimationTime = 100;
            Assert.Equal(paused, Draw(.125));
        });
    }

    [InstalledChampionFact("Akali")]
    public async Task SelectedMapClipKeepsItsOwnShaderClockWhileTheMapAdvances()
    {
        await WithSkin((asset, _, animation, clip, settings) =>
        {
            using var target = new Target();
            using var renderer = new MapCharacterRenderer(settings);
            renderer.Initialize(target.Gl);
            var placement = new MapCharacterData(new MapPlaceableData(1, 1, 0, "clock", Matrix4x4.Identity,
                255, null, null), "Characters/Akali/Skins/Skin102", null, null);
            using var group = new MapCharacterRuntimeGroup(asset, animation, new[] { placement });
            group.SetPreviewClip(clip, placement);
            group.SetPreviewHiddenSubmeshes(asset.Vfx.OwnerSceneContext.InitialHiddenSubmeshHashes.ToHashSet());
            group.PreviewTimeSeconds = .125f;
            byte[] Draw(float mapTime)
            {
                target.Clear();
                renderer.Render(new[] { group }, target.ViewProjection, target.View, target.Projection,
                    Target.Eye, mapTime, null);
                Assert.Equal(GLEnum.NoError, target.Gl.GetError());
                return target.Pixels();
            }

            byte[] paused = Draw(10);
            Assert.Equal(paused, Draw(20));
            group.PreviewTimeSeconds = .825f;
            Assert.False(paused.SequenceEqual(Draw(20)));
            group.PreviewTimeSeconds = .125f;
            Assert.Equal(paused, Draw(30));
            group.ClearPreviewClip();
            Assert.False(Draw(10).SequenceEqual(Draw(20)), "Unselected map characters retain the map's running clock.");
        });
    }

    private static async Task WithSkin(Action<MapCharacterAssetData, SkinnedMesh, MapCharacterAnimationRuntime,
        AnimationClipDefinition, AppSettings> check)
    {
        string install = InstalledSkins.FindInstall();
        Assert.NotNull(install);
        AppSettings settings = InstalledSkins.Settings(install);
        var log = new LogService(new Serilog.LoggerConfiguration().CreateLogger());
        var provider = new WadContentProvider(log, new WadNodeLoaderService(null, log), new DirectoriesCreator(), new SvgParser());
        var resolver = new MapAssetResolver(provider, settings);
        var asset = await InstalledSkins.CreateLoader(settings, log).LoadAsync("Characters/Akali/Skins/Skin102", null);
        Assert.NotNull(asset);
        var clip = Assert.Single(asset.AnimationGraph.Clips, candidate => candidate.OwnerPathHash == 0x9dd9dc06);
        using var animation = new MapCharacterAnimationRuntime(resolver, log);
        Assert.True(await animation.PrepareClipAsync(asset, clip));
        var source = await resolver.ResolveReferenceAsync(asset.Skin.Mesh, null);
        using Stream stream = await resolver.OpenReadAsync(source);
        using var skin = SkinnedMesh.ReadFromSimpleSkin(stream);
        Exception failure = null;
        var thread = new Thread(() =>
        {
            try { check(asset, skin, animation, clip, settings); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(60)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static SceneModel Model(MapCharacterAssetData asset, SkinnedMesh skin)
    {
        var range = asset.Mesh.Ranges.First(item => item.Name.Equals("Body", StringComparison.OrdinalIgnoreCase));
        var geometry = new MeshGeometry3D
        {
            Positions = new Point3DCollection(asset.Mesh.Positions.Select(p => new Point3D(p.X, p.Y, p.Z))),
            Normals = new Vector3DCollection(asset.Mesh.Normals.Select(p => new Vector3D(p.X, p.Y, p.Z))),
            TextureCoordinates = new PointCollection(asset.Mesh.Uv.Select(p => new System.Windows.Point(p.X, p.Y))),
            TriangleIndices = new Int32Collection(asset.Mesh.Indices.Skip(range.StartIndex).Take(range.IndexCount).Select(i => (int)i))
        };
        var material = asset.Materials.ResolveMaterialDefinition(range.Name);
        Assert.NotNull(material.Program);
        var model = new SceneModel { Skeleton = asset.Skeleton, SkinnedMesh = skin, Scale = asset.Skin.Scale,
            SelfIllumination = asset.Materials.SelfIllumination };
        model.Parts.Add(new ModelPart(range.Name, new GeometryModel3D(geometry, null))
        {
            SourceVertexIndices = Enumerable.Range(0, asset.Mesh.VertexCount).ToArray(),
            AllTextures = asset.Textures.ToDictionary(item => item.Key, item => item.Value),
            SelectedTextureName = material.BaseTextureName, MaterialDefinition = material
        });
        model.GpuSkinningData = GpuSkinningData.TryCreate(asset.Skeleton, skin, model.Parts, out string failure);
        Assert.True(model.GpuSkinningData != null, failure);
        return model;
    }

    private sealed class Target : IDisposable
    {
        private readonly HiddenWglContext _context = new();
        internal readonly GL Gl;
        private readonly uint _framebuffer, _color, _depth;
        internal static readonly Vector3 Eye = new(150, 130, 180);
        internal readonly Matrix4x4 View = Matrix4x4.CreateLookAt(Eye, new Vector3(0, 110, 0), Vector3.UnitY);
        internal readonly Matrix4x4 Projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, 1, 1, 5000);
        internal Matrix4x4 ViewProjection => View * Projection;

        internal Target()
        {
            Gl = GL.GetApi(_context.GetProcAddress);
            _framebuffer = Gl.GenFramebuffer(); _color = Gl.GenRenderbuffer(); _depth = Gl.GenRenderbuffer();
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            Gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _color);
            Gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Rgba8, 256, 256);
            Gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                RenderbufferTarget.Renderbuffer, _color);
            Gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, _depth);
            Gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, 256, 256);
            Gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment,
                RenderbufferTarget.Renderbuffer, _depth);
            Assert.Equal(GLEnum.FramebufferComplete, Gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));
        }

        internal void Clear()
        {
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            Gl.Viewport(0, 0, 256, 256);
            Gl.DepthMask(true); Gl.ClearDepth(1); Gl.ClearColor(0, 0, 0, 0);
            Gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
        }

        internal byte[] Pixels()
        {
            var pixels = new byte[256 * 256 * 4];
            Gl.ReadPixels(0, 0, 256, 256, Silk.NET.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            Assert.Contains(pixels, pixel => pixel > 0);
            return pixels;
        }

        public void Dispose()
        {
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            Gl.DeleteFramebuffer(_framebuffer); Gl.DeleteRenderbuffer(_color); Gl.DeleteRenderbuffer(_depth);
            Gl.Dispose(); _context.Dispose();
        }
    }
}
