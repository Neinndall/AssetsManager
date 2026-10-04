using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Resolvers;
using LeagueToolkit.Core.Mesh;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Tests.Support;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `skin-part-render <skin-path> <focus-submesh> <toward-submesh> <out.png> [size] [distance]`: draws the skin's visible
    /// submeshes in bind pose through the viewer's GlMeshRenderer with the Studio reference lighting,
    /// optionally with game programs enabled by `--shaders`,
    /// framed on the focus submesh and seen from the side the toward submesh sits on.
    /// </summary>
    internal static class SkinPartRenderDiagnostic
    {
        public static void Run(string[] args)
        {
            var parameters = args.Where(arg => arg.StartsWith("--parameter=", StringComparison.Ordinal))
                .Select(arg => arg[12..].Split('=', 2))
                .ToDictionary(pair => pair[0], pair => float.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
            bool shaders = args.Contains("--shaders"), isolate = args.Contains("--isolate"), studio = args.Contains("--studio");
            bool studioLoader = args.Contains("--studio-loader");
            studio |= studioLoader;
            args = args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToArray();
            if (args.Length < 4)
            {
                Console.WriteLine("Usage: skin-part-render <skin-path> <focus-submesh> <toward-submesh> <out.png> [size] [distance] [--shaders] [--isolate] [--studio] [--studio-loader] [--parameter=name=value]");
                return;
            }

            string install = InstalledSkins.FindInstall();
            AppSettings settings = InstalledSkins.Settings(install);
            using var logger = new Serilog.LoggerConfiguration().CreateLogger();
            var log = new LogService(logger);
            var loader = InstalledSkins.CreateLoader(settings, log);
            string projectRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "am-skin-part-render")).FullName;
            MapCharacterAssetData asset = loader.LoadAsync(args[0], projectRoot, CancellationToken.None).GetAwaiter().GetResult();
            var provider = new WadContentProvider(log, new WadNodeLoaderService(null, log), new AssetsManager.Utils.DirectoriesCreator(), new SvgParser());
            var resolver = new MapAssetResolver(provider, settings);
            var source = resolver.ResolveReferenceAsync(asset.Skin.Mesh, projectRoot).GetAwaiter().GetResult();
            using Stream stream = resolver.OpenReadAsync(source).GetAwaiter().GetResult();
            using SkinnedMesh skin = SkinnedMesh.ReadFromSimpleSkin(stream);
            int size = args.Length > 4 ? int.Parse(args[4]) : 512;

            Exception failure = null;
            var thread = new Thread(() =>
            {
                if (studioLoader)
                {
                    var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                    app.Dispatcher.BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            using var hashes = new HashResolverService(new DirectoriesCreator(), log);
                            await hashes.LoadAllHashesAsync();
                            string meshPath = asset.Skin.Mesh.VirtualPath ?? hashes.ResolveHash(asset.Skin.Mesh.PathHash);
                            string modelPath = Path.Combine(projectRoot, Path.GetFileName(meshPath));
                            if (!modelPath.EndsWith(".skn", StringComparison.OrdinalIgnoreCase)) modelPath += ".skn";
                            using (var file = File.Create(modelPath))
                            using (Stream raw = await resolver.OpenReadAsync(source)) await raw.CopyToAsync(file);
                            var rig = await resolver.ResolveReferenceAsync(asset.Skin.Skeleton, null);
                            using (var file = File.Create(Path.ChangeExtension(modelPath, ".skl")))
                            using (Stream raw = await resolver.OpenReadAsync(rig)) await raw.CopyToAsync(file);
                            string binPath = Path.Combine(projectRoot, "skin.bin");
                            var bin = await resolver.ResolveVirtualAsync("data/" + args[0].ToLowerInvariant() + ".bin", null);
                            using (var file = File.Create(binPath))
                            using (Stream raw = await resolver.OpenReadAsync(bin)) await raw.CopyToAsync(file);
                            using var model = await new SknLoadingService(log, hashes, provider, settings)
                                .LoadModelWithSkinBin(modelPath, binPath, projectRoot);
                            if (model == null) throw new InvalidOperationException("Studio loader returned no model.");
                            model.GpuSkinningData = GpuSkinningData.TryCreate(model.Skeleton, model.SkinnedMesh, model.Parts, out string skinningFailure);
                            if (model.GpuSkinningData == null) throw new InvalidOperationException(skinningFailure);
                            foreach (ModelPart part in model.Parts)
                                foreach (GameMaterialTexture texture in (part.MaterialDefinition.Program?.Passes ?? Array.Empty<GameMaterialPass>()).SelectMany(pass => pass.Textures))
                                {
                                    string path = texture.Texture?.VirtualPath ?? texture.Texture?.PathHash.ToString("x16");
                                    string key = SknMaterialTextureResolver.MatchTextureKey(path, part.AllTextures.Keys.ToArray());
                                    Console.WriteLine($"[StudioTexture] {part.Name} {texture.Name}: path={path} key={key} loaded={!string.IsNullOrWhiteSpace(key)}");
                                }
                            Render(asset, args[1], args[2], args[3], size, args.Length > 5 ? float.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 3.2f, settings, skin, shaders, isolate, parameters: parameters, studio: studio, loadedModel: model);
                        }
                        catch (Exception ex) { failure = ex; }
                        finally { app.Dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Normal); }
                    }));
                    System.Windows.Threading.Dispatcher.Run();
                    return;
                }
                try { Render(asset, args[1], args[2], args[3], size, args.Length > 5 ? float.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 3.2f, settings, skin, shaders, isolate, parameters: parameters, studio: studio); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) Console.WriteLine($"[PartRender] failed: {failure}");
        }

        internal static int Render(MapCharacterAssetData asset, string focusName, string towardName, string output, int size, float distance, AppSettings settings, SkinnedMesh skin, bool shaders, bool isolateFocus = false, Matrix4x4[] pose = null, IReadOnlyDictionary<string, float> parameters = null, bool studio = false, SceneModel loadedModel = null)
        {
            MapCharacterMeshData mesh = asset.Mesh;
            var hidden = asset.Materials.InitialHiddenSubmeshes?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new();
            var model = loadedModel ?? new SceneModel { Name = "render", Skeleton = asset.Skeleton, SkinnedMesh = skin, SkinningMatrices = pose, SelfIllumination = asset.Materials.SelfIllumination };
            var parts = loadedModel != null ? model.Parts.Where(part => part.IsVisible && (!isolateFocus || part.Name == focusName)).ToList() : mesh.Ranges
                .Where(range => !hidden.Contains(range.Name))
                .Where(range => !isolateFocus || range.Name == focusName)
                .Select(range => Part(mesh, range, asset))
                .ToList();
            if (parameters is { Count: > 0 })
                foreach (ModelPart part in parts)
                    if (part.MaterialDefinition.Program is { } program)
                        part.MaterialDefinition = part.MaterialDefinition with
                        {
                            Program = program with
                            {
                                Passes = program.Passes.Select(pass => pass with
                                {
                                    Parameters = pass.Parameters.Select(parameter => parameters.TryGetValue(parameter.Name, out float value)
                                        ? parameter with { Value = new Vector4(value, parameter.Value.Y, parameter.Value.Z, parameter.Value.W) }
                                        : parameter).ToArray()
                                }).ToArray()
                            }
                        };
            int[] ranks = asset.Materials.DrawRanks(parts.Select(part => part.Name).ToArray());
            for (int at = 0; at < parts.Count; at++)
                parts[at].DrawRank = ranks[at];
            if (loadedModel == null)
            {
                model.AddParts(parts);
                model.GpuSkinningData = GpuSkinningData.TryCreate(asset.Skeleton, skin, parts, out string failure);
                if (model.GpuSkinningData == null) throw new InvalidOperationException(failure);
            }
            else
                foreach (ModelPart part in model.Parts) part.IsVisible = parts.Contains(part);

            MapCharacterMeshRange focus = mesh.Ranges.FirstOrDefault(range => string.Equals(range.Name, focusName, StringComparison.OrdinalIgnoreCase))
                ?? new MapCharacterMeshRange("*", 0, mesh.Indices.Length);
            MapCharacterMeshRange toward = mesh.Ranges.FirstOrDefault(range => string.Equals(range.Name, towardName, StringComparison.OrdinalIgnoreCase));
            Vector3[] focusPoints = Points(mesh, focus);
            Vector3 centre = focusPoints.Aggregate(Vector3.Zero, (sum, point) => sum + point) / focusPoints.Length;
            float radius = focusPoints.Max(point => Vector3.Distance(point, centre));
            Vector3 nearest = toward == null ? Vector3.Zero : Points(mesh, toward).OrderBy(point => Vector3.DistanceSquared(point, centre)).Take(64)
                .Aggregate(Vector3.Zero, (sum, point) => sum + point) / 64f;
            // "front" looks at the focus from +Z, where a character faces.
            Vector3 direction = toward == null ? Vector3.UnitZ : Vector3.Normalize(nearest - centre);
            Vector3 eye = centre + direction * radius * distance;

            Matrix4x4 view = Matrix4x4.CreateLookAt(eye, centre, Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, 1f, 1f, 5000f);

            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            uint colour = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, colour);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)size, (uint)size, 0, Silk.NET.OpenGL.PixelFormat.Rgba,
                PixelType.UnsignedByte, new ReadOnlySpan<byte>(new byte[size * size * 4]));
            uint depth = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.DepthComponent24, (uint)size, (uint)size);
            uint framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colour, 0);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, RenderbufferTarget.Renderbuffer, depth);

            using var renderer = new GlMeshRenderer(settings);
            renderer.Initialize(gl);
            gl.Viewport(0, 0, (uint)size, (uint)size);
            gl.ClearColor(0.07f, 0.08f, 0.12f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            var lighting = GlMeshRenderer.ReferenceCharacterLighting();
            renderer.Render(model, view * projection, view, projection, eye,
                lighting.LightDirection, lighting.LightColor, lighting.FillDirection, lighting.FillColor, lighting.AmbientColor, shadersEnabled: shaders, mirrorCharacterX: studio);
            if (studio) renderer.ComposeBloom();

            byte[] rgba = new byte[size * size * 4];
            gl.ReadPixels(0, 0, (uint)size, (uint)size, Silk.NET.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, rgba.AsSpan());
            Console.WriteLine($"[PartRender] glError={gl.GetError()} parts={parts.Count} eye={eye} centre={centre} selfIllumination={model.SelfIllumination} studio={studio}");

            int covered = 0;
            double luminance = 0;
            for (int at = 0; at < rgba.Length; at += 4)
                if (rgba[at] != 18 || rgba[at + 1] != 20 || rgba[at + 2] != 31)
                {
                    covered++;
                    luminance += 0.2126 * rgba[at] + 0.7152 * rgba[at + 1] + 0.0722 * rgba[at + 2];
                }
            Console.WriteLine($"[PartRender] covered={covered} meanLuminance={(covered == 0 ? 0 : luminance / covered):F3}");
            if (output == null) return covered;

            byte[] bgra = new byte[rgba.Length];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int from = ((size - 1 - y) * size + x) * 4, to = (y * size + x) * 4;
                    bgra[to] = rgba[from + 2]; bgra[to + 1] = rgba[from + 1]; bgra[to + 2] = rgba[from]; bgra[to + 3] = 255;
                }
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, bgra, size * 4)));
            using FileStream file = File.Create(output);
            encoder.Save(file);
            Console.WriteLine($"[PartRender] wrote {output} covered={covered}");
            return covered;
        }

        private static ModelPart Part(MapCharacterMeshData mesh, MapCharacterMeshRange range, MapCharacterAssetData asset)
        {
            var indices = new Int32Collection();
            for (int at = range.StartIndex; at < range.StartIndex + range.IndexCount; at++)
                indices.Add((int)mesh.Indices[at]);
            var geometry = new MeshGeometry3D
            {
                Positions = new Point3DCollection(mesh.Positions.Select(point => new Point3D(point.X, point.Y, point.Z))),
                Normals = new Vector3DCollection(mesh.Normals.Select(normal => new Vector3D(normal.X, normal.Y, normal.Z))),
                TextureCoordinates = new PointCollection(mesh.Uv.Select(uv => new System.Windows.Point(uv.X, uv.Y))),
                TriangleIndices = indices
            };
            ModelMaterialDefinition material = asset.Materials.ResolveMaterialDefinition(range.Name);
            return new ModelPart(range.Name, new GeometryModel3D(geometry, null))
            {
                AllTextures = asset.Textures.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                SelectedTextureName = material.BaseTextureName,
                SourceVertexIndices = Enumerable.Range(0, mesh.Positions.Length).ToArray(),
                MaterialDefinition = material
            };
        }

        private static Vector3[] Points(MapCharacterMeshData mesh, MapCharacterMeshRange range) =>
            Enumerable.Range(range.StartIndex, range.IndexCount).Select(at => mesh.Positions[mesh.Indices[at]]).Distinct().ToArray();
    }
}
