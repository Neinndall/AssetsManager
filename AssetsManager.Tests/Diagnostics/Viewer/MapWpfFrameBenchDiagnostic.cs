using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AssetsManager.Services.Viewer.Interaction;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Runtime;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using OpenTK.Wpf;
using Silk.NET.OpenGL;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    internal static class MapWpfFrameBenchDiagnostic
    {
        internal static Task Run(AppSettings settings, MapSceneData scene, MapSceneRuntime runtime,
            IReadOnlyDictionary<string, MapTextureImage> textures,
            IReadOnlyDictionary<string, MapTextureImage> programs,
            IReadOnlyDictionary<string, MapTextureImage> lightmaps, bool inputFlood = false)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { RunHost(settings, scene, runtime, textures, programs, lightmaps, inputFlood); done.SetResult(); }
                catch (Exception error) { done.SetException(error); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return done.Task;
        }

        private static void RunHost(AppSettings settings, MapSceneData scene, MapSceneRuntime runtime,
            IReadOnlyDictionary<string, MapTextureImage> textures,
            IReadOnlyDictionary<string, MapTextureImage> programs,
            IReadOnlyDictionary<string, MapTextureImage> lightmaps, bool inputFlood)
        {
            // This standalone Windows diagnostic owns all GLFW/GL work on its dedicated STA.
            OpenTK.Windowing.Desktop.GLFWProvider.CheckForMainThread = false;
            var host = new GLWpfControl();
            var window = new Window
            {
                Content = host, Width = 1280, Height = 720, ResizeMode = ResizeMode.NoResize,
                ShowActivated = false, ShowInTaskbar = false, Left = 80, Top = 80, Topmost = true,
                Title = "AssetsManager viewport performance diagnostic"
            };
            var dispatcher = window.Dispatcher;
            using var scheduler = new ViewportFrameScheduler(dispatcher, host.InvalidateVisual);
            GL gl = null;
            MapGeometryRenderer geometry = null;
            MapCharacterRenderer characters = null;
            MapParticleRenderer particles = null;
            MapPostEffectsRenderer post = null;
            FxaaPostEffectsRenderer aa = null;
            GlSceneCapture terrain = null;
            Exception failure = null;
            int phase = inputFlood ? 2 : 0, frame = 0, pendingInput = 0;
            long floodUpdates = 0;
            bool finished = false;
            float angle = 0;
            long lastFrame = 0;
            var drawTimes = new List<double>();
            var frameIntervals = new List<double>();
            var inputTimes = new List<double>();
            double coldMax = 0;
            var stages = new double[5];
            Vector3 center = scene.Origin ?? new Vector3(7500, 0, 7500);
            center.X = -center.X;

            void Fail(Exception error)
            {
                failure ??= error;
                finished = true;
                dispatcher.InvokeAsync(window.Close, DispatcherPriority.Send);
            }

            void FloodInput()
            {
                if (finished || dispatcher.HasShutdownStarted) return;
                floodUpdates++;
                dispatcher.InvokeAsync(FloodInput, DispatcherPriority.Input);
            }

            host.Ready += () =>
            {
                try
                {
                    gl = GL.GetApi(ProcAddress);
                    geometry = new MapGeometryRenderer(settings);
                    geometry.Initialize(gl);
                    geometry.LoadScene(scene);
                    geometry.UpdateTextures(textures);
                    geometry.UpdateProgramTextures(programs);
                    geometry.UpdateLightmaps(lightmaps);
                    characters = new MapCharacterRenderer(settings);
                    characters.Initialize(gl);
                    particles = new MapParticleRenderer();
                    particles.Initialize(gl, settings);
                    particles.SetSun(scene.Sun);
                    post = new MapPostEffectsRenderer();
                    post.Initialize(gl);
                    aa = new FxaaPostEffectsRenderer();
                    aa.Initialize(gl);
                    terrain = new GlSceneCapture(gl);
                    Console.WriteLine($"[WpfBench] GPU={gl.GetStringS(StringName.Renderer)} visible-window=1280x720 (presentation FPS are not measured)");
                }
                catch (Exception error) { Fail(error); }
            };

            host.Render += delta =>
            {
                if (gl == null || finished || failure != null) return;
                try
                {
                    long started = Stopwatch.GetTimestamp();
                    double interval = lastFrame == 0 ? 0 : Stopwatch.GetElapsedTime(lastFrame, started).TotalMilliseconds;
                    if (frame >= 30 && lastFrame != 0)
                        frameIntervals.Add(Stopwatch.GetElapsedTime(lastFrame, started).TotalMilliseconds);
                    lastFrame = started;
                    bool effects = phase != 0;
                    uint width = (uint)Math.Max(1, host.FrameBufferWidth), height = (uint)Math.Max(1, host.FrameBufferHeight);
                    Vector3 eye = center + Vector3.Transform(new Vector3(0, 16000, 13000), Matrix4x4.CreateRotationY(angle));
                    Matrix4x4 view = Matrix4x4.CreateLookAt(eye, center, Vector3.UnitY);
                    Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, width / (float)height, 10, 80000);
                    Matrix4x4 vp = view * projection;
                    gl.DepthMask(true);
                    gl.ColorMask(true, true, true, true);
                    gl.ClearColor(0.08f, 0.09f, 0.12f, 1);
                    gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit | ClearBufferMask.StencilBufferBit);
                    runtime.ShowParticles = effects;
                    long stage = Stopwatch.GetTimestamp();
                    runtime.Update(vp, 1f / 60);
                    stages[0] = Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
                    stage = Stopwatch.GetTimestamp();
                    for (int pass = 0; pass < 2; pass++)
                    {
                        geometry.Render(vp, view, projection, eye, frame / 60f, transparentPass: pass == 1);
                        if (pass == 0) terrain.Capture(width, height, captureColor: false, captureDepth: true);
                        characters.Render(runtime.CharacterGroups, vp, view, projection, eye, frame / 60f, scene.Sun,
                            runtime.Hidden, lightGrid: scene.LightGrid, transparentPass: pass == 1);
                    }
                    post.CaptureSceneDepth(scene.PostEffects, scene.AmbientOcclusion, width, height);
                    stages[1] = Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
                    stage = Stopwatch.GetTimestamp();
                    if (effects)
                    {
                        particles.SetTerrainDepth(terrain.DepthTexture, width, height);
                        particles.Render(runtime.Particles.VisibleRuntimes, vp, view, width, height);
                    }
                    stages[2] = Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
                    stage = Stopwatch.GetTimestamp();
                    post.Render(scene.PostEffects, scene.AmbientOcclusion, view, projection, width, height);
                    stages[3] = Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
                    stage = Stopwatch.GetTimestamp();
                    aa.Render((int)width, (int)height);
                    stages[4] = Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
                    double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    coldMax = Math.Max(coldMax, elapsed);
                    if (frame >= 30) drawTimes.Add(elapsed);
                    if (frame % 60 == 0) Console.WriteLine($"[WpfBench.Frame] phase={phase} frame={frame} draw={elapsed:F2} interval={interval:F2} stages={string.Join(",", stages.Select(ms => ms.ToString("F2")))}");
                    if (++frame < 240) return;
                    string label = phase switch { 0 => "automatic-shaders", 1 => "automatic-shaders-vfx", _ => "input-priority-shaders-vfx" };
                    Console.WriteLine($"[WpfBench] {label} draw={Distribution(drawTimes)} interval={Distribution(frameIntervals)} input={Distribution(inputTimes)} warmupMax={coldMax:F2} ms");
                    if (gl.GetError() != GLEnum.NoError)
                        throw new InvalidOperationException("OpenGL error in WPF host benchmark.");
                    if (++phase == 3)
                    {
                        if (inputFlood) Console.WriteLine($"[WpfBench] sustained-input updates={floodUpdates}; completed all 240 render frames.");
                        finished = true;
                        dispatcher.InvokeAsync(window.Close, DispatcherPriority.Send);
                        return;
                    }
                    frame = 0; angle = 0; lastFrame = 0; coldMax = 0;
                    drawTimes.Clear(); frameIntervals.Clear(); inputTimes.Clear();
                    runtime.Particles.Restart();
                    if (phase == 2)
                    {
                        host.RenderContinuously = false;
                        scheduler.Start();
                    }
                }
                catch (Exception error) { Fail(error); }
            };

            using var input = new Timer(_ =>
            {
                if (dispatcher.HasShutdownStarted || Interlocked.Exchange(ref pendingInput, 1) != 0) return;
                long queued = Stopwatch.GetTimestamp();
                dispatcher.InvokeAsync(() =>
                {
                    Interlocked.Exchange(ref pendingInput, 0);
                    if (finished) return;
                    if (frame >= 30) inputTimes.Add(Stopwatch.GetElapsedTime(queued).TotalMilliseconds);
                    angle += 0.003f;
                }, DispatcherPriority.Input);
            }, null, 16, 16);
            var timeout = new DispatcherTimer(DispatcherPriority.Send, dispatcher) { Interval = TimeSpan.FromMinutes(2) };
            timeout.Tick += (_, _) => Fail(new TimeoutException($"WPF host benchmark timed out at phase={phase}, frame={frame}."));
            window.Closed += (_, _) => dispatcher.InvokeShutdown();
            try
            {
                timeout.Start();
                host.Start(new GLWpfControlSettings { MajorVersion = 3, MinorVersion = 3, RenderContinuously = !inputFlood });
                window.Show();
                if (inputFlood)
                {
                    dispatcher.InvokeAsync(FloodInput, DispatcherPriority.Input);
                    scheduler.Start();
                }
                Dispatcher.Run();
            }
            finally
            {
                finished = true;
                input.Change(Timeout.Infinite, Timeout.Infinite);
                timeout.Stop();
                scheduler.Stop();
                terrain?.Dispose(); aa?.Dispose(); post?.Dispose(); particles?.Dispose(); characters?.Dispose(); geometry?.Dispose();
                gl?.Dispose(); host.Dispose();
            }
            if (failure != null) throw new InvalidOperationException("WPF host benchmark failed.", failure);
        }

        private static string Distribution(List<double> samples)
        {
            if (samples.Count == 0) return "no samples";
            double[] sorted = samples.Order().ToArray();
            return $"n:{sorted.Length}/median:{sorted[sorted.Length / 2]:F2}/p95:{sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * 0.95))]:F2}/max:{sorted[^1]:F2} ms";
        }

        private static IntPtr ProcAddress(string name)
        {
            IntPtr address = WglGetProcAddress(name);
            long value = address.ToInt64();
            return address == IntPtr.Zero || value is 1 or 2 or 3 or -1 ? NativeGetProcAddress(GetModuleHandle("opengl32.dll"), name) : address;
        }

        [DllImport("opengl32.dll", EntryPoint = "wglGetProcAddress", CharSet = CharSet.Ansi)]
        private static extern IntPtr WglGetProcAddress(string name);
        [DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = CharSet.Ansi)]
        private static extern IntPtr NativeGetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);
    }
}
