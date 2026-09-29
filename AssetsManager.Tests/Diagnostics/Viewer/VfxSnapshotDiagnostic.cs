using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Vfx.Parsing;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Services.Viewer.Vfx.Session;
using AssetsManager.Tests.Support;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;
using Silk.NET.OpenGL;
using PixelFormat = Silk.NET.OpenGL.PixelFormat;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// `vfx-snapshot <bin-path-in-wad> <system-name|0xhash> <outDir> [--times 0.25,0.5,1] [--size 512] [--per-emitter] [--keep-resources] [--dump-emitter NAME] [--no-shader-definitions]`:
    /// plays one VFX system of an installed BIN the way VFX Studio does (VfxRenderSession, game particle shaders,
    /// resources extracted from the WADs) and writes a PNG per time over a mid-grey backdrop. With --per-emitter
    /// each root emitter is also drawn alone and measured: how much of the frame it darkens or brightens.
    /// </summary>
    internal static class VfxSnapshotDiagnostic
    {
        private const float Backdrop = 0.35f;

        public static void Run(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: vfx-snapshot <bin-path-in-wad> <system-name|0xhash> <outDir> [--times 0.25,0.5,1] [--size 512] [--per-emitter]");
                return;
            }
            string binPath = args[0].Replace('\\', '/').ToLowerInvariant();
            string systemKey = args[1];
            string output = Directory.CreateDirectory(args[2]).FullName;
            double[] times = Option(args, "--times")?.Split(',').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray()
                             ?? new[] { 0.25, 0.5, 1.0 };
            uint size = uint.TryParse(Option(args, "--size"), out uint parsedSize) ? parsedSize : 512u;
            bool perEmitter = args.Contains("--per-emitter");

            string install = InstalledSkins.FindInstall();
            AppSettings settings = InstalledSkins.Settings(install);
            var log = new LogService(new Serilog.LoggerConfiguration().CreateLogger());
            BinTree tree = LoadBin(install, binPath);
            if (tree == null)
            {
                Console.WriteLine($"[Snapshot] {binPath} not found in the installed WADs.");
                return;
            }

            // VFX custom materials find their CustomShaderDefs in the global shader BIN, as the app loads them.
            BinTree shaders = args.Contains("--no-shader-definitions") ? null : LoadBin(install, "data/shaders/shaders.bin");
            BinTree[] shaderTrees = shaders == null ? null : new[] { shaders };
            IReadOnlyDictionary<uint, uint> resourceMap = VfxResourceParser.ExtractResourceMap(tree);
            var systems = VfxSystemParser.ExtractAll(tree).ToDictionary(
                pair => pair.Key,
                pair => VfxGraphParser.ResolveCustomMaterials(pair.Value, tree, shaderTrees: shaderTrees) with { ResourceMap = resourceMap });
            VfxSystemDefinition system = FindSystem(systems, systemKey);
            if (system == null)
            {
                Console.WriteLine($"[Snapshot] no system matches '{systemKey}' in {binPath} ({systems.Count} systems).");
                return;
            }

            var wadProvider = new WadContentProvider(log, new WadNodeLoaderService(null, log), new DirectoriesCreator(), new SvgParser());
            var resolver = new MapAssetResolver(wadProvider, settings);
            IReadOnlyDictionary<uint, VfxSystemDefinition> reachable =
                VfxSceneResourceContext.ReachableSystems(systems, resourceMap, new[] { system });
            using VfxSceneResourceContext resources = VfxSceneResourceContext
                .CreateAsync(reachable, null, resolver, null, log).GetAwaiter().GetResult();

            if (args.Contains("--keep-resources"))
            {
                // A copy of the extracted textures and meshes, for tex-to-png.
                foreach (string file in Directory.EnumerateFiles(resources.SearchDirectory, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(output, "resources", Path.GetRelativePath(resources.SearchDirectory, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(file, target, overwrite: true);
                }
            }

            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            (uint framebuffer, uint colour, uint depth) = CreateTarget(gl, size);
            var session = new VfxRenderSession(log);
            session.Initialize(gl, settings);
            session.SetWorldTransform(Matrix4x4.Identity);
            session.SetViewportSize(size, size);
            session.SetSystem(new VfxSystemModel
            {
                Name = system.Name,
                Definition = system,
                SystemCatalog = systems,
                ResourceMap = resourceMap,
                SearchDirectory = resources.SearchDirectory,
                PlaybackSeed = VfxRenderSession.IdleEffectSeed,
                TotalDuration = VfxDurationCalculator.SystemSpan(system),
                Speed = 1
            });

            // The League camera looks down at about 56 degrees; frame the system's authored bounds from there.
            float pitch = 56f * MathF.PI / 180f;
            VfxDefinitionBounds bounds = VfxSystemBounds.Calculate(system, VfxRigPreset.Still);
            VfxCameraFrame frame = VfxSystemBounds.FramePerspective(bounds, 40f, 1f, new Vector3(0f, MathF.Sin(pitch), MathF.Cos(pitch)));
            Matrix4x4 view = Matrix4x4.CreateLookAt(frame.Position, frame.Target, Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(40f * MathF.PI / 180f, 1f, VfxPreviewCamera.NearPlane, VfxPreviewCamera.FarPlane);
            Matrix4x4 viewProjection = view * projection;

            IReadOnlyList<VfxEmitterDefinition> emitters = system.Emitters ?? Array.Empty<VfxEmitterDefinition>();
            if (Option(args, "--dump-emitter") is { } dumped)
                foreach (VfxEmitterDefinition emitter in emitters.Where(item => string.Equals(item.Name, dumped, StringComparison.OrdinalIgnoreCase)))
                    Console.WriteLine($"[Snapshot] parsed {emitter}");
            Console.WriteLine($"[Snapshot] {system.Name} emitters={emitters.Count} bounds={bounds.Min}..{bounds.Max} eye={frame.Position}");
            string stem = Sanitize(system.Name);
            foreach (double time in times)
            {
                session.SetAllEmittersVisibility(true);
                float[] pixels = Draw(gl, session, framebuffer, size, time, viewProjection, view);
                string file = Path.Combine(output, $"{stem}_t{time.ToString("0.00", CultureInfo.InvariantCulture)}.png");
                SavePng(pixels, size, file);
                (float darker, float brighter) = Coverage(pixels);
                Console.WriteLine($"[Snapshot] t={time:0.00}s live={session.LiveParticleCount} darker={darker:P1} brighter={brighter:P1} -> {file}");

                if (!perEmitter) continue;
                for (int order = 0; order < emitters.Count; order++)
                {
                    session.SetAllEmittersVisibility(false);
                    if (!session.SetEmitterVisibility(order, true)) continue;
                    float[] alone = Draw(gl, session, framebuffer, size, time, viewProjection, view);
                    (float emitterDarker, float emitterBrighter) = Coverage(alone);
                    int live = session.GetEmitterLiveCount(order);
                    if (live == 0 && emitterDarker + emitterBrighter < 0.001f) continue;
                    VfxEmitterDefinition emitter = emitters[order];
                    (float faintDarker, float faintBrighter) = Coverage(alone, 0.01f);
                    Console.WriteLine($"[Snapshot]   #{order} {emitter.Name} live={live} blend={emitter.BlendMode} " +
                                      $"darker={emitterDarker:P1} brighter={emitterBrighter:P1} touched={faintDarker + faintBrighter:P1} " +
                                      $"soft={emitter.SoftParticle != null} erosion={emitter.AlphaErosion != null} mult={emitter.TextureMultPath != null} " +
                                      $"program={session.GameParticleFallback(emitter, emitter.IsMeshPrimitive) ?? "game"}");
                    SavePng(alone, size, Path.Combine(output, $"{stem}_t{time.ToString("0.00", CultureInfo.InvariantCulture)}_e{order:00}_{Sanitize(emitter.Name)}.png"));
                }
            }

            session.Dispose();
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteTexture(colour);
            gl.DeleteRenderbuffer(depth);
        }

        private static float[] Draw(GL gl, VfxRenderSession session, uint framebuffer, uint size, double time,
            Matrix4x4 viewProjection, Matrix4x4 view)
        {
            session.Seek(time);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.Viewport(0, 0, size, size);
            // Uploads are budgeted per frame; pump them before the measured frame.
            for (int pump = 0; pump < 256 && VfxGpuResourceUploader.HasPendingResources(session.Graphs); pump++)
                session.PrepareRenderFrame(viewProjection, view);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.Viewport(0, 0, size, size);
            gl.ClearColor(Backdrop, Backdrop, Backdrop, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
            session.Render(viewProjection, view);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            var pixels = new float[size * size * 4];
            gl.ReadPixels(0, 0, size, size, PixelFormat.Rgba, PixelType.Float, new Span<float>(pixels));
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            return pixels;
        }

        /// <summary>
        /// Share of pixels that differ from the backdrop by at least `threshold` in some channel, split by whether
        /// their luminance is darker or brighter; a tint of the backdrop's own brightness still counts.
        /// </summary>
        private static (float Darker, float Brighter) Coverage(float[] pixels, float threshold = 0.08f)
        {
            int darker = 0, brighter = 0, count = pixels.Length / 4;
            for (int at = 0; at < pixels.Length; at += 4)
            {
                float change = MathF.Max(MathF.Abs(pixels[at] - Backdrop), MathF.Max(MathF.Abs(pixels[at + 1] - Backdrop), MathF.Abs(pixels[at + 2] - Backdrop)));
                if (change < threshold) continue;
                float luminance = pixels[at] * 0.2126f + pixels[at + 1] * 0.7152f + pixels[at + 2] * 0.0722f;
                if (luminance < Backdrop) darker++;
                else brighter++;
            }
            return (darker / (float)count, brighter / (float)count);
        }

        private static (uint Framebuffer, uint Colour, uint Depth) CreateTarget(GL gl, uint size)
        {
            uint colour = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, colour);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, size, size, 0, PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
            uint depth = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, size, size);
            uint framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colour, 0);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, depth);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            return (framebuffer, colour, depth);
        }

        private static BinTree LoadBin(string install, string binPath)
        {
            ulong hash = XxHash64Ext.Hash(binPath);
            foreach (string wadPath in Directory.GetFiles(Path.Combine(install, @"Game\DATA\FINAL"), "*.wad.client", SearchOption.AllDirectories))
            {
                using var wad = new WadFile(wadPath);
                if (!wad.Chunks.ContainsKey(hash)) continue;
                using var data = wad.LoadChunkDecompressed(hash);
                using var stream = new MemoryStream(data.Span.ToArray(), writable: false);
                return new BinTree(stream);
            }
            return null;
        }

        private static VfxSystemDefinition FindSystem(IReadOnlyDictionary<uint, VfxSystemDefinition> systems, string key)
        {
            if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                uint.TryParse(key[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint hash))
                return systems.GetValueOrDefault(hash);
            return systems.GetValueOrDefault(Fnv1a.HashLower(key))
                   ?? systems.Values.FirstOrDefault(system => string.Equals(system.Name, key, StringComparison.OrdinalIgnoreCase))
                   ?? systems.Values.FirstOrDefault(system => system.Name?.Contains(key, StringComparison.OrdinalIgnoreCase) == true);
        }

        private static string Option(string[] args, string name)
        {
            int at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }

        private static string Sanitize(string name) =>
            string.Concat((name ?? "system").Select(character => char.IsLetterOrDigit(character) ? character : '_'));

        private static void SavePng(float[] pixels, uint size, string path)
        {
            int width = (int)size;
            var bytes = new byte[width * width * 4];
            for (int y = 0; y < width; y++)
            for (int x = 0; x < width; x++)
            {
                int source = ((width - 1 - y) * width + x) * 4, target = (y * width + x) * 4;
                bytes[target] = ToByte(pixels[source + 2]);
                bytes[target + 1] = ToByte(pixels[source + 1]);
                bytes[target + 2] = ToByte(pixels[source]);
                bytes[target + 3] = 255;
            }
            var bitmap = BitmapSource.Create(width, width, 96, 96, PixelFormats.Bgra32, null, bytes, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        private static byte ToByte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
    }
}
