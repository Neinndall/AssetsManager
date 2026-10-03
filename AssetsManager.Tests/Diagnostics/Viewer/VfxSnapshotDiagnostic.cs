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
using AssetsManager.Services.Viewer.Rendering.GameShaders;
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
    /// `vfx-snapshot <bin-path-in-wad> <system-name|0xhash> <outDir> [--times 0.25,0.5,1] [--size 512] [--per-emitter] [--keep-resources] [--dump-emitter NAME] [--no-shader-definitions] [--trace-emitter NAME] [--no-owner] [--rig Still|Trail|Missile] [--trace-layout] [--frame-scale 1]`:
    /// plays one VFX system of an installed BIN the way VFX Studio does (VfxRenderSession, game particle shaders,
    /// resources extracted from the WADs) and writes a PNG per time over a mid-grey backdrop. With --per-emitter
    /// each root emitter is also drawn alone and measured: how much of the frame it darkens or brightens.
    /// A skin BIN also supplies its Character (SKN/SKL) to AttachedMesh emitters, drawn in bind pose.
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
            VfxRigPreset rig = Enum.TryParse(Option(args, "--rig"), ignoreCase: true, out VfxRigPreset parsedRig)
                ? parsedRig : VfxRigPreset.Still;

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
            VfxDiagnosticCatalog catalog = VfxDiagnosticCatalog.Load(binPath, tree, path => LoadBin(install, path), shaderTrees);
            IReadOnlyDictionary<uint, uint> resourceMap = catalog.ResourceMap;
            IReadOnlyDictionary<uint, VfxSystemDefinition> systems = catalog.Systems;
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
            VfxOwnerSceneContext owner = args.Contains("--no-owner") ? null : catalog.Owner;
            if (owner != null)
                Console.WriteLine($"[Snapshot] owner {owner.MeshPath} skeleton={owner.SkeletonPath} scale={owner.SkinScale}");
            using VfxSceneResourceContext resources = VfxSceneResourceContext
                .CreateAsync(reachable, null, resolver, null, log, ownerSceneContext: owner).GetAwaiter().GetResult();

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
                OwnerSceneContext = owner,
                SearchDirectory = resources.SearchDirectory,
                PlaybackSeed = VfxRenderSession.IdleEffectSeed,
                TotalDuration = VfxDurationCalculator.SystemSpan(system),
                Speed = 1
            });

            session.RigPreset = rig;

            // The League camera looks down at about 56 degrees; frame the system's authored bounds from there.
            float pitch = 56f * MathF.PI / 180f;
            VfxDefinitionBounds bounds = VfxSystemBounds.Calculate(system, rig);
            VfxCameraFrame frame = VfxSystemBounds.FramePerspective(bounds, 40f, 1f, new Vector3(0f, MathF.Sin(pitch), MathF.Cos(pitch)));
            if (float.TryParse(Option(args, "--frame-scale"), NumberStyles.Float, CultureInfo.InvariantCulture, out float frameScale) &&
                float.IsFinite(frameScale) && frameScale > 0f)
                frame = frame with { Position = frame.Target + (frame.Position - frame.Target) * frameScale };
            Matrix4x4 view = Matrix4x4.CreateLookAt(frame.Position, frame.Target, Vector3.UnitY);
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(40f * MathF.PI / 180f, 1f, VfxPreviewCamera.NearPlane, VfxPreviewCamera.FarPlane);
            Matrix4x4 viewProjection = view * projection;

            IReadOnlyList<VfxEmitterDefinition> emitters = system.Emitters ?? Array.Empty<VfxEmitterDefinition>();
            if (Option(args, "--dump-emitter") is { } dumped)
                foreach (VfxEmitterDefinition emitter in emitters.Where(item => dumped == "*" || string.Equals(item.Name, dumped, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"[Snapshot] parsed {emitter}");
                    if (emitter.ChildParticleSet is { } childSet)
                        foreach (VfxChildSystemReference child in childSet.Children)
                        {
                            VfxSystemDefinition target = VfxPlaybackGraphRuntime.ResolveSystem(child, systems, system.ResourceMap ?? resourceMap);
                            Console.WriteLine($"[Snapshot] child parent={emitter.Name} target={target?.Name ?? "unresolved"} reference={child} bones=[{string.Join(",", childSet.Bones ?? Array.Empty<string>())}]");
                        }
                }
            Console.WriteLine($"[Snapshot] {system.Name} emitters={emitters.Count} bounds={bounds.Min}..{bounds.Max} eye={frame.Position}");
            if (Option(args, "--trace-emitter") is { } traced)
            {
                // Simulation and GPU state of one emitter at each time: why it does not emit or draw, or when its
                // particles retire. Drawing the frame first uploads its resources.
                foreach (double time in times)
                {
                    Draw(gl, session, framebuffer, size, time, viewProjection, view);
                    foreach (var state in session.Graphs.SelectMany(graph => graph.Runtimes).SelectMany(runtime => runtime.Emitters)
                                 .Where(item => string.Equals(item.Def.Name, traced, StringComparison.OrdinalIgnoreCase)))
                        Console.WriteLine($"[Snapshot] trace t={time:0.00} {state.Def.Name} age={state.Age:0.000} burstDone={state.BurstDone} " +
                                          $"initial={state.InitialEmissionDone} finishedAt={state.FinishedAt:0.000} visible={state.IsVisible} particles={state.Particles.Count} " +
                                          $"texture={state.Texture} reflection={state.ReflectionTexture} meshVao={state.MeshVao} " +
                                          string.Join(" ", state.Particles.Take(3).Select(particle => $"[age={particle.Age:0.000} life={particle.Life:0.000}]")));
                    foreach (var state in session.Graphs.SelectMany(graph => graph.Runtimes).SelectMany(runtime => runtime.Emitters)
                                 .Where(item => string.Equals(item.Def.Name, traced, StringComparison.OrdinalIgnoreCase) && item.Def.HasResolvedCustomMaterial).Take(1))
                        Console.WriteLine($"[Snapshot] trace material " +
                                          $"parameters=[{string.Join(", ", state.Def.CustomMaterial.Program?.Passes.SelectMany(pass => pass.Parameters).Select(parameter => parameter.Name).Distinct() ?? Array.Empty<string>())}] " +
                                          $"shader members=[{string.Join(", ", ShaderMembers(state.Def.CustomMaterial.Program, settings))}]");
                }
            }
            if (args.Contains("--trace-layout"))
                foreach (double time in times)
                {
                    Draw(gl, session, framebuffer, size, time, viewProjection, view);
                    foreach (var runtime in session.Graphs.SelectMany(graph => graph.Runtimes))
                    {
                        Console.WriteLine($"[Layout] t={time:0.00} system={runtime.Definition.Name} origin={runtime.WorldTransform.Translation} up={Vector3.TransformNormal(Vector3.UnitY, runtime.WorldTransform)}");
                        foreach (var state in runtime.Emitters)
                        {
                            var instances = state.Instances;
                            if (state.Particles.Count == 0) continue;
                            var particle = state.Particles[0];
                            Console.WriteLine($"[Layout] emitter={state.Def.Name} root={state.RenderRootSourceOrder} path={state.RenderPath} base={state.BasePos} position={particle.Pos} age={particle.Age:0.000} drawn=<{instances[0]}, {instances[1]}, {instances[2]}> scale=<{instances[3]}, {instances[4]}, {instances[18]}> rotation={particle.BirthRotation} right=<{instances[36]}, {instances[37]}, {instances[38]}> up=<{instances[39]}, {instances[40]}, {instances[41]}> forward=<{instances[42]}, {instances[43]}, {instances[44]}>");
                            if (state.MeshAnimation?.TryGetJointTransform("Chest", particle.Age, out var joint) == true)
                                Console.WriteLine($"[Layout] Chest local={joint.Translation} up={Vector3.TransformNormal(Vector3.UnitY, joint)}");
                        }
                    }
                }
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

        /// <summary>Constant block members of every pass of a material program, read from the installed shader cache.</summary>
        private static IEnumerable<string> ShaderMembers(GameMaterialProgram program, AppSettings settings)
        {
            var bytecodes = program == null ? null : GameShaderProgramResolver.ReadProgram(program, settings);
            foreach (var pass in bytecodes?.Passes ?? Array.Empty<GameShaderProgramResolver.ShaderBytecodePassRead>())
            {
                if (!pass.Bytecode.Ready) continue;
                var translated = AssetsManager.Shaders.GameShaderTranslator.Translate(
                    pass.Bytecode.Program.Vertex, pass.Bytecode.Program.VertexReflection,
                    pass.Bytecode.Program.Pixel, pass.Bytecode.Program.PixelReflection);
                if (!translated.Ready) continue;
                foreach (var block in translated.Program.Vertex.Sidecar.Blocks.Concat(translated.Program.Pixel.Sidecar.Blocks))
                    foreach (var member in block.Members)
                        yield return $"{block.Name}.{member.Name}";
            }
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
