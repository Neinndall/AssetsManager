using System;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Tests.Support;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `skin-part-render <skin-path> <focus-submesh> <toward-submesh> <out.png> [size]`: draws the skin's visible
    /// submeshes in bind pose through the viewer's GlMeshRenderer with the Studio reference lighting and shaders off,
    /// framed on the focus submesh and seen from the side the toward submesh sits on.
    /// </summary>
    internal static class SkinPartRenderDiagnostic
    {
        public static void Run(string[] args)
        {
            if (args.Length < 4)
            {
                Console.WriteLine("Usage: skin-part-render <skin-path> <focus-submesh> <toward-submesh> <out.png> [size]");
                return;
            }

            string install = InstalledSkins.FindInstall();
            AppSettings settings = InstalledSkins.Settings(install);
            var loader = InstalledSkins.CreateLoader(settings, new LogService(new Serilog.LoggerConfiguration().CreateLogger()));
            string projectRoot = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "am-skin-part-render")).FullName;
            MapCharacterAssetData asset = loader.LoadAsync(args[0], projectRoot, CancellationToken.None).GetAwaiter().GetResult();
            int size = args.Length > 4 ? int.Parse(args[4]) : 512;

            Exception failure = null;
            var thread = new Thread(() =>
            {
                try { Render(asset, args[1], args[2], args[3], size, args.Length > 5 ? float.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture) : 3.2f); }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) Console.WriteLine($"[PartRender] failed: {failure}");
        }

        private static void Render(MapCharacterAssetData asset, string focusName, string towardName, string output, int size, float distance)
        {
            MapCharacterMeshData mesh = asset.Mesh;
            var hidden = asset.Materials.InitialHiddenSubmeshes?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? new();
            var model = new SceneModel { Name = "render" };
            var parts = mesh.Ranges
                .Where(range => !hidden.Contains(range.Name))
                .Select(range => Part(mesh, range, asset))
                .ToList();
            int[] ranks = asset.Materials.DrawRanks(parts.Select(part => part.Name).ToArray());
            for (int at = 0; at < parts.Count; at++)
                parts[at].DrawRank = ranks[at];
            model.AddParts(parts);

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

            var renderer = new GlMeshRenderer();
            renderer.Initialize(gl);
            gl.Viewport(0, 0, (uint)size, (uint)size);
            gl.ClearColor(0.07f, 0.08f, 0.12f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            var lighting = GlMeshRenderer.ReferenceCharacterLighting();
            renderer.Render(model, view * projection, view, projection, eye,
                lighting.LightDirection, lighting.LightColor, lighting.FillDirection, lighting.FillColor, lighting.AmbientColor);

            byte[] rgba = new byte[size * size * 4];
            gl.ReadPixels(0, 0, (uint)size, (uint)size, Silk.NET.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, rgba.AsSpan());
            Console.WriteLine($"[PartRender] glError={gl.GetError()} parts={parts.Count} eye={eye} centre={centre}");

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
            Console.WriteLine($"[PartRender] wrote {output}");
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
                MaterialDefinition = material
            };
        }

        private static Vector3[] Points(MapCharacterMeshData mesh, MapCharacterMeshRange range) =>
            Enumerable.Range(range.StartIndex, range.IndexCount).Select(at => mesh.Positions[mesh.Indices[at]]).Distinct().ToArray();
    }
}
