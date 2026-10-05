using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Explorer;
using AssetsManager.Services.Parsers;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Parsing;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Services.Viewer.Vfx.Loading;
using AssetsManager.Tests.Support;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    internal static class MapFrameBenchDiagnostic
    {
        internal static async Task Run(string[] args)
        {
            if (args.Length == 0 || !Directory.Exists(args[0]))
                throw new ArgumentException("Usage: map-frame-bench <extracted-map-root> [capture-directory | --cold-navigation | --wpf-navigation | --wpf-input-flood | --fire-lifetime | --binding-compare]");

            string root = Path.GetFullPath(args[0]);
            var settings = AppSettings.GetDefaultSettings();
            settings.LolPbeDirectory = @"C:\Riot Games\League of Legends (PBE)";
            settings.LolLiveDirectory = @"C:\Riot Games\League of Legends";
            settings.PreferredClient = PreferredClient.PBE;
            var log = new LogService(new Serilog.LoggerConfiguration().CreateLogger());
            var provider = new WadContentProvider(log, new WadNodeLoaderService(null, log), new DirectoriesCreator(), new SvgParser());
            var resolver = new MapAssetResolver(provider, settings);
            MapSceneSource source = StudioProjectCatalog.ScanBrowser(root, CancellationToken.None, null, null)
                .MapSources.First(scene => scene.Map.Value.Equals("Maps/MapGeometry/Map11/Base_SRX", StringComparison.OrdinalIgnoreCase));
            var loader = new MapSceneLoadingService(resolver, new MapGeometryDecoder(), new MapMaterialParser(),
                new MapPlaceableParser(), new MapCharacterParser(), new MapParticleParser(),
                new MapParticleSystemParser(), new MapTextureLoadingService(resolver, log), null, log);
            Console.WriteLine("[FrameBench] Loading Base_SRX and full-resolution resources.");
            MapSceneData scene = await loader.LoadBackdropAsync(source);
            var factory = new MapSceneRuntimeFactory(new MapCharacterLoadingService(resolver,
                new MapCharacterSkinParser(), new MapCharacterMeshDecoder(), null, log), resolver, null, log);
            using MapSceneRuntime runtime = await factory.CreateAsync(scene);
            if (args.Contains("--fire-lifetime"))
            {
                var fires = runtime.Particles.Runtimes
                    .Where(placed => new[] { "torch", "fire", "flame", "brazier" }.Any(word =>
                        (placed.System.Name ?? "").Contains(word, StringComparison.OrdinalIgnoreCase)))
                    .GroupBy(placed => placed.System).Select(group => group.First()).ToArray();
                if (fires.Length == 0) throw new InvalidOperationException("No named fire systems were found.");
                Console.WriteLine($"[FireBench] systems={fires.Length}; advancing authored graphs for 120 simulated seconds without rendering.");
                int[] peaks = new int[fires.Length];
                for (int frame = 1; frame <= 7200; frame++)
                {
                    for (int index = 0; index < fires.Length; index++)
                    {
                        fires[index].Advance(1f / 60);
                        int count = fires[index].Graph.Runtimes.Sum(graph => graph.LiveParticleCount);
                        peaks[index] = Math.Max(peaks[index], count);
                        if (frame is 600 or 1800 or 3600 or 7200)
                            Console.WriteLine($"[FireBench] time={frame / 60}s system={fires[index].System.Name} live={count} peak={peaks[index]}");
                    }
                }
                return;
            }
            var textures = await loader.LoadFullTexturesAsync(scene);
            var programs = await loader.LoadFullProgramTexturesAsync(scene);
            var lightmaps = await loader.LoadFullLightmapsAsync(scene);
            Console.WriteLine($"[FrameBench] meshes={scene.Geometry.Meshes.Count} materials={scene.Materials.Count} skins={runtime.CharacterGroups.Count} placements={runtime.Particles.Runtimes.Count}");

            if (args.Contains("--wpf-navigation") || args.Contains("--wpf-input-flood"))
            {
                await MapWpfFrameBenchDiagnostic.Run(settings, scene, runtime, textures, programs, lightmaps,
                    args.Contains("--wpf-input-flood"));
                return;
            }

            // All GL work stays on this thread after the asynchronous CPU loading finishes.
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            Console.WriteLine($"[FrameBench] GPU={gl.GetStringS(StringName.Renderer)} GL={gl.GetStringS(StringName.Version)}");
            int debugErrors = 0;
            DebugProc debug = (source, type, id, severity, length, message, user) =>
            {
                if (type == GLEnum.DebugTypeError && debugErrors++ < 8)
                    Console.WriteLine("[FrameBench.GL] " + System.Runtime.InteropServices.Marshal.PtrToStringAnsi(message, length) + (debugErrors == 1 ? Environment.StackTrace : ""));
            };
            gl.Enable(EnableCap.DebugOutput);
            gl.Enable(EnableCap.DebugOutputSynchronous);
            gl.DebugMessageCallback(debug, in System.IntPtr.Zero);
            const uint width = 1280, height = 720;
            uint color = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, color);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
            uint depth = gl.GenRenderbuffer();
            gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            gl.RenderbufferStorage(RenderbufferTarget.Renderbuffer, InternalFormat.Depth24Stencil8, width, height);
            uint framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, color, 0);
            gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthStencilAttachment, RenderbufferTarget.Renderbuffer, depth);
            if (gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
                throw new InvalidOperationException("Frame benchmark framebuffer is incomplete.");
            gl.Viewport(0, 0, width, height);
            using var geometry = new MapGeometryRenderer(settings);
            geometry.Initialize(gl);
            geometry.LoadScene(scene);
            geometry.UpdateTextures(textures);
            geometry.UpdateProgramTextures(programs);
            geometry.UpdateLightmaps(lightmaps);
            using var characters = new MapCharacterRenderer(settings);
            characters.Initialize(gl);
            using var particles = new MapParticleRenderer();
            particles.Initialize(gl, settings);
            particles.SetSun(scene.Sun);
            using var post = new MapPostEffectsRenderer();
            post.Initialize(gl);
            Vector3 center = scene.Origin ?? new Vector3(7500, 0, 7500);
            center.X = -center.X;
            Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, width / (float)height, 10, 80000);
            double[] stages = new double[6];
            (int Programs, int UniformBuffers) colorBindings = default;
            bool coldNavigation = args.Contains("--cold-navigation");
            int navigationFrame = 0;
            if (coldNavigation)
                GameShaderRuntime.PreparationMeasured = (stage, ms) =>
                {
                    if (ms > 15) Console.WriteLine($"[NavigationBench.Prepare] frame={navigationFrame} stage={stage} time={ms:F2} ms");
                };

            void Draw(int index, bool shaders, bool effects, bool close, bool moving)
            {
                Vector3 focus = close ? center + new Vector3(-2500, 0, -2500) : center;
                float angle = moving ? index * 0.012f : 0;
                if (coldNavigation)
                    focus = center + new Vector3(MathF.Sin(angle) * 5000, 0, MathF.Cos(angle) * 5000);
                Vector3 offset = close ? new Vector3(0, 2200, 2600) : new Vector3(0, 16000, 13000);
                offset = Vector3.Transform(offset, Matrix4x4.CreateRotationY(angle));
                Vector3 eye = focus + offset;
                Matrix4x4 view = Matrix4x4.CreateLookAt(eye, focus, Vector3.UnitY);
                Matrix4x4 vp = view * projection;
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
                gl.Viewport(0, 0, width, height);
                gl.DepthMask(true);
                gl.ColorMask(true, true, true, true);
                gl.ClearColor(0.08f, 0.09f, 0.12f, 1);
                gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
                long start = Stopwatch.GetTimestamp();
                runtime.Update(vp, 1f / 60);
                stages[0] += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                for (int phase = 0; phase < 2; phase++)
                {
                    start = Stopwatch.GetTimestamp();
                    geometry.Render(vp, view, projection, eye, index / 60f, shadersEnabled: shaders, transparentPass: phase == 1);
                    stages[1] += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    start = Stopwatch.GetTimestamp();
                    characters.Render(runtime.CharacterGroups, vp, view, projection, eye, index / 60f, scene.Sun,
                        runtime.Hidden, shadersEnabled: shaders, lightGrid: scene.LightGrid, transparentPass: phase == 1);
                    stages[2] += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
                post.CaptureSceneDepth(scene.PostEffects, scene.AmbientOcclusion, width, height);
                if (effects)
                {
                    start = Stopwatch.GetTimestamp();
                    bool prepared = particles.PrepareRenderFrame(runtime.Particles.VisibleRuntimes, vp, view, width, height);
                    stages[3] += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    start = Stopwatch.GetTimestamp();
                    if (prepared)
                    {
                        using IDisposable batch = particles.BeginPreparedRenderBatch();
                        particles.RenderPreparedColorPass();
                        colorBindings = particles.LastDrawBindingCounts;
                        particles.CapturePreparedDistortionFrame();
                        particles.RenderPreparedDistortionPass();
                    }
                    stages[4] += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                }
                start = Stopwatch.GetTimestamp();
                post.Render(scene.PostEffects, scene.AmbientOcclusion, view, projection, width, height);
                // Deliberate synchronization is diagnostic-only: report completed frames, including GPU cost.
                gl.Finish();
                stages[5] += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }

            try
            {
                if (args.Contains("--binding-compare"))
                {
                    runtime.ShowParticles = true;
                    foreach (bool shaders in new[] { false, true })
                    for (int round = 0; round < 3; round++)
                    foreach (bool cache in round % 2 == 0 ? new[] { false, true } : new[] { true, false })
                    {
                        particles.CacheDrawBindings = cache;
                        runtime.Particles.Restart();
                        for (int frame = 0; frame < 90; frame++) Draw(frame, shaders, true, false, true);
                        Array.Clear(stages);
                        var samples = new double[90];
                        long programsBound = 0, buffersBound = 0;
                        for (int frame = 0; frame < samples.Length; frame++)
                        {
                            long start = Stopwatch.GetTimestamp();
                            Draw(90 + frame, shaders, true, false, true);
                            samples[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                            programsBound += colorBindings.Programs;
                            buffersBound += colorBindings.UniformBuffers;
                        }
                        Array.Sort(samples);
                        Console.WriteLine($"[BindingBench] shaders={shaders} round={round} cache={cache} median={samples[45]:F2} p95={samples[85]:F2} max={samples[^1]:F2} ms vfxMean={stages[4] / samples.Length:F2} colorPrograms={programsBound / samples.Length} colorUniformBuffers={buffersBound / samples.Length}");
                        if (gl.GetError() != GLEnum.NoError || debugErrors != 0)
                            throw new InvalidOperationException("OpenGL error during binding comparison.");
                    }
                    return;
                }
                if (coldNavigation)
                {
                    runtime.ShowParticles = true;
                    for (int lap = 0; lap < 2; lap++)
                    {
                        runtime.Particles.Restart();
                        var samples = new double[540];
                        int spikes = 0;
                        for (int i = 0; i < samples.Length; i++)
                        {
                            navigationFrame = i;
                            Array.Clear(stages);
                            long start = Stopwatch.GetTimestamp();
                            Draw(i, true, true, close: true, moving: true);
                            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                            if (samples[i] > 50)
                            {
                                if (spikes++ < 20)
                                    Console.WriteLine($"[NavigationBench] lap={lap} frame={i} time={samples[i]:F2} ms stages={string.Join(",", stages.Select(ms => ms.ToString("F2")))} GC={GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
                            }
                        }
                        Array.Sort(samples);
                        Console.WriteLine($"[NavigationBench] lap={lap} median={samples[270]:F2} p95={samples[513]:F2} p99={samples[534]:F2} max={samples[^1]:F2} ms framesAbove50ms={spikes}");
                        if (gl.GetError() != GLEnum.NoError || debugErrors != 0)
                            throw new InvalidOperationException("OpenGL error during navigation benchmark.");
                    }
                    return;
                }
                foreach (bool close in new[] { false, true })
                foreach (bool moving in new[] { false, true })
                foreach ((bool shaders, bool effects) in new[] { (false, false), (true, false), (false, true), (true, true) })
                {
                    runtime.ShowParticles = effects;
                    runtime.Particles.Restart();
                    for (int i = 0; i < 90; i++) Draw(i, shaders, effects, close, moving);
                    Array.Clear(stages);
                    var samples = new double[90];
                    long allocated = GC.GetAllocatedBytesForCurrentThread();
                    int gen0 = GC.CollectionCount(0);
                    for (int i = 0; i < samples.Length; i++)
                    {
                        long start = Stopwatch.GetTimestamp();
                        Draw(90 + i, shaders, effects, close, moving);
                        samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    }
                    allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    Array.Sort(samples);
                    string label = $"{(close ? "close" : "overview")}-{(moving ? "moving" : "still")}-s{(shaders ? 1 : 0)}-v{(effects ? 1 : 0)}";
                    Console.WriteLine($"[FrameBench] {label} median={samples[45]:F2} p95={samples[85]:F2} max={samples[^1]:F2} ms alloc={allocated / samples.Length} B/frame gen0={GC.CollectionCount(0) - gen0} visible={runtime.Particles.VisibleRuntimes.Count} stages={string.Join(",", stages.Select(ms => (ms / samples.Length).ToString("F2")))}");
                    if (effects)
                    {
                        var emitters = runtime.Particles.VisibleRuntimes.SelectMany(placement => placement.Graph.Runtimes)
                            .SelectMany(graph => graph.Emitters).Where(emitter => emitter.IsVisible && emitter.InstanceCount > 0).ToArray();
                        var meshes = emitters.Where(emitter => emitter.Def.IsMeshPrimitive).ToArray();
                        Console.WriteLine($"[FrameBench] meshEmitters={meshes.Length} meshInstances={meshes.Sum(emitter => emitter.InstanceCount)} maxMeshInstances={meshes.Select(emitter => emitter.InstanceCount).DefaultIfEmpty().Max()} otherEmitters={emitters.Length - meshes.Length}");
                    }
                    if (args.Length > 1 && !moving)
                    {
                        Directory.CreateDirectory(args[1]);
                        byte[] pixels = new byte[width * height * 4];
                        gl.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
                        File.WriteAllBytes(Path.Combine(args[1], label + ".rgba"), pixels);
                    }
                    GLEnum error = gl.GetError();
                    if (error != GLEnum.NoError && !args.Contains("--allow-gl-errors"))
                        throw new InvalidOperationException($"OpenGL error after {label}: {error}");
                }
                // Exercise native-to-stock transitions after the timed runs, including VFX wire twins.
                if (!args.Contains("--allow-gl-errors"))
                {
                    Vector3 eye = center + new Vector3(0, 2200, 2600);
                    Matrix4x4 view = Matrix4x4.CreateLookAt(eye, center, Vector3.UnitY);
                    Matrix4x4 vp = view * projection;
                    runtime.ShowParticles = true;
                    runtime.Update(vp, 1f / 60);
                    foreach (var mode in new[] { StudioViewMode.Lit, StudioViewMode.Wireframe })
                    {
                        bool overlay = mode == StudioViewMode.Lit;
                        geometry.Render(vp, view, projection, eye, 3f, viewMode: mode, wireOverlay: overlay);
                        characters.Render(runtime.CharacterGroups, vp, view, projection, eye, 3f, scene.Sun,
                            runtime.Hidden, viewMode: mode, wireOverlay: overlay, lightGrid: scene.LightGrid);
                        particles.Render(runtime.Particles.VisibleRuntimes, vp, view, width, height, mode, overlay);
                        gl.Finish();
                        if (gl.GetError() != GLEnum.NoError || debugErrors != 0)
                            throw new InvalidOperationException($"OpenGL error while checking {mode} wire rendering.");
                    }
                    Console.WriteLine("[FrameBench] Native/stock wire transitions passed without OpenGL errors.");
                }
            }
            finally
            {
                GameShaderRuntime.PreparationMeasured = null;
                gl.DebugMessageCallback(null, in System.IntPtr.Zero);
                GC.KeepAlive(debug);
                gl.DeleteFramebuffer(framebuffer);
                gl.DeleteRenderbuffer(depth);
                gl.DeleteTexture(color);
            }
        }
    }
}
