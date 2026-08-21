using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Views.Models.Viewer;
using Serilog;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    internal static class MapGeometryMemoryDiagnostic
    {
        public static void Run(string mapGeoPath, string materialsPath, string gameDataPath)
        {
            if (string.IsNullOrWhiteSpace(mapGeoPath))
            {
                Console.WriteLine(
                    "Usage: dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj -- " +
                    "mapgeo-memory <mapgeo> [materials.bin|-] [game-data-root]");
                return;
            }

            mapGeoPath = System.IO.Path.GetFullPath(mapGeoPath);
            materialsPath = NormalizeOptionalPath(materialsPath);
            gameDataPath = string.IsNullOrWhiteSpace(gameDataPath)
                ? System.IO.Path.GetDirectoryName(mapGeoPath)
                : System.IO.Path.GetFullPath(gameDataPath);

            if (!System.IO.File.Exists(mapGeoPath))
            {
                Console.WriteLine($"[MapGeoMemory] MapGeo not found: {mapGeoPath}");
                return;
            }

            if (materialsPath != null && !System.IO.File.Exists(materialsPath))
            {
                Console.WriteLine($"[MapGeoMemory] materials.bin not found: {materialsPath}");
                return;
            }

            ExceptionDispatchInfo failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    RunOnDispatcher(mapGeoPath, materialsPath, gameDataPath);
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            failure?.Throw();
        }

        private static string NormalizeOptionalPath(string path) =>
            string.IsNullOrWhiteSpace(path) || path == "-"
                ? null
                : System.IO.Path.GetFullPath(path);

        private static void RunOnDispatcher(string mapGeoPath, string materialsPath, string gameDataPath)
        {
            var application = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            var dispatcher = application.Dispatcher;
            Task diagnosticTask = RunDiagnosticAsync(
                dispatcher,
                mapGeoPath,
                materialsPath,
                gameDataPath);

            diagnosticTask.ContinueWith(
                _ => dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Normal),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            System.Windows.Threading.Dispatcher.Run();
            application.Shutdown();
            diagnosticTask.GetAwaiter().GetResult();
        }

        private static async Task RunDiagnosticAsync(
            System.Windows.Threading.Dispatcher dispatcher,
            string mapGeoPath,
            string materialsPath,
            string gameDataPath)
        {
            var process = Process.GetCurrentProcess();
            PrintMemory("before load", process);
            Console.WriteLine($"[MapGeoMemory] MapGeo: {mapGeoPath}");
            Console.WriteLine($"[MapGeoMemory] Materials: {materialsPath ?? "<none>"}");
            Console.WriteLine($"[MapGeoMemory] Game data: {gameDataPath}");

            var logger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.Console()
                .CreateLogger();
            var service = new MapGeometryLoadingService(new LogService(logger));
            SceneModel model = await service.LoadMapGeometry(
                mapGeoPath,
                materialsPath,
                gameDataPath,
                CancellationToken.None);

            PrintMemory("after WPF scene", process);
            if (model == null)
            {
                Console.WriteLine("[MapGeoMemory] The loader returned no scene model.");
                return;
            }

            await dispatcher.InvokeAsync(() => PrintSceneEstimate(model));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            PrintMemory("after full GC (scene retained)", process);

            await dispatcher.InvokeAsync(() => RemoveWpfMaterials(model));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            PrintMemory("after WPF materials stripped + full GC", process);

            await dispatcher.InvokeAsync(model.Dispose);
            model = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            PrintMemory("after scene dispose + full GC", process);

            SceneModel secondModel = await service.LoadMapGeometry(
                mapGeoPath,
                materialsPath,
                gameDataPath,
                CancellationToken.None);
            await dispatcher.InvokeAsync(() => { });
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            PrintMemory("after second load + full GC", process);

            if (secondModel != null)
            {
                await dispatcher.InvokeAsync(secondModel.Dispose);
                secondModel = null;
                await dispatcher.InvokeAsync(() => { });
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                PrintMemory("after second dispose + full GC", process);
            }

            SceneModel thirdModel = await service.LoadMapGeometry(
                mapGeoPath,
                materialsPath,
                gameDataPath,
                CancellationToken.None);
            await dispatcher.InvokeAsync(() => { });
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            PrintMemory("after third load + full GC", process);

            if (thirdModel != null)
            {
                await dispatcher.InvokeAsync(thirdModel.Dispose);
                thirdModel = null;
                await dispatcher.InvokeAsync(() => { });
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                PrintMemory("after third dispose + full GC", process);
            }

            Console.WriteLine(
                "[MapGeoMemory] Note: this diagnostic does not create an OpenGL context; " +
                "the estimated renderer CPU copy is reported separately.");
        }

        private static void RemoveWpfMaterials(SceneModel model)
        {
            foreach (ModelPart part in model.Parts)
            {
                if (part.Geometry == null)
                    continue;

                part.Geometry.Material = null;
                part.Geometry.BackMaterial = null;
            }
        }

        private static void PrintSceneEstimate(SceneModel model)
        {
            int partCount = 0;
            long vertexCount = 0;
            long indexCount = 0;
            long textureCoordinateCount = 0;
            long lightmapCoordinateCount = 0;
            long vertexColorBytes = 0;
            long dynamicVertexCount = 0;
            long estimatedTextureBytes = 0;
            long estimatedLightmapTextureBytes = 0;
            int lightmappedPartCount = 0;
            var lightmapTextures = new HashSet<BitmapSource>(ReferenceEqualityComparer.Instance);
            var lightmapKeys = new HashSet<string>(StringComparer.Ordinal);
            var textures = new HashSet<BitmapSource>(ReferenceEqualityComparer.Instance);
            var textureDictionaries = new HashSet<Dictionary<string, BitmapSource>>(ReferenceEqualityComparer.Instance);
            var textureNameCollections = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var meshNameValues = new HashSet<string>(StringComparer.Ordinal);
            var meshNameInstances = new HashSet<string>(ReferenceEqualityComparer.Instance);
            var selectedTextureValues = new HashSet<string>(StringComparer.Ordinal);
            var selectedTextureInstances = new HashSet<string>(ReferenceEqualityComparer.Instance);
            var textureDimensions = new Dictionary<(int Width, int Height), int>();

            foreach (ModelPart part in model.Parts)
            {
                partCount++;
                if (part.Name != null)
                {
                    meshNameValues.Add(part.Name);
                    meshNameInstances.Add(part.Name);
                }
                if (part.SelectedTextureName != null)
                {
                    selectedTextureValues.Add(part.SelectedTextureName);
                    selectedTextureInstances.Add(part.SelectedTextureName);
                }
                if (part.AllTextures != null)
                    textureDictionaries.Add(part.AllTextures);
                if (part.AvailableTextureNames != null)
                    textureNameCollections.Add(part.AvailableTextureNames);

                if (part.Geometry?.Geometry is MeshGeometry3D mesh)
                {
                    vertexCount += mesh.Positions?.Count ?? 0;
                    indexCount += mesh.TriangleIndices?.Count ?? 0;
                    textureCoordinateCount += mesh.TextureCoordinates?.Count ?? 0;
                    if (part.SourceVertexIndices != null)
                        dynamicVertexCount += mesh.Positions?.Count ?? 0;
                }

                if (part.Lightmap?.UvCoordinates is { Length: > 0 } lightmapCoordinates)
                {
                    lightmappedPartCount++;
                    lightmapCoordinateCount += lightmapCoordinates.Length;
                    if (!string.IsNullOrEmpty(part.Lightmap.TextureKey))
                        lightmapKeys.Add(part.Lightmap.TextureKey);

                    if (part.AllTextures?.TryGetValue(part.Lightmap.TextureKey, out BitmapSource lightmap) == true &&
                        lightmap != null && lightmapTextures.Add(lightmap))
                    {
                        estimatedLightmapTextureBytes += (long)lightmap.PixelWidth * lightmap.PixelHeight * 4;
                    }
                }

                vertexColorBytes += part.VertexColors?.LongLength ?? 0;

                if (part.AllTextures == null)
                    continue;

                foreach (BitmapSource texture in part.AllTextures.Values)
                {
                    if (texture == null || !textures.Add(texture))
                        continue;

                    estimatedTextureBytes += (long)texture.PixelWidth * texture.PixelHeight * 4;
                    var dimensions = (texture.PixelWidth, texture.PixelHeight);
                    textureDimensions[dimensions] = textureDimensions.TryGetValue(dimensions, out int count)
                        ? count + 1
                        : 1;
                }
            }

            long estimatedWpfBytes =
                vertexCount * 24L +
                indexCount * sizeof(int) +
                textureCoordinateCount * 16L +
                lightmapCoordinateCount * sizeof(float) +
                vertexColorBytes;
            long estimatedRendererUploadStagingBytes =
                vertexCount * 8L * sizeof(float) +
                vertexCount * 3L * sizeof(float) +
                indexCount * sizeof(uint);
            long estimatedRendererRetainedCpuBytes =
                dynamicVertexCount * 8L * sizeof(float) +
                dynamicVertexCount * 3L * sizeof(float);
            long estimatedRendererGpuGeometryBytes =
                vertexCount * 8L * sizeof(float) +
                indexCount * sizeof(uint) +
                lightmapCoordinateCount * sizeof(float);
            long estimatedRendererGpuTextureBytes = estimatedTextureBytes * 4 / 3;

            Console.WriteLine("[MapGeoMemory] Scene estimate:");
            Console.WriteLine($"  Parts: {partCount:N0}");
            Console.WriteLine($"  Vertices: {vertexCount:N0}");
            Console.WriteLine($"  Indices: {indexCount:N0}");
            Console.WriteLine($"  Lightmapped parts: {lightmappedPartCount:N0}");
            Console.WriteLine($"  Lightmap UV floats: {lightmapCoordinateCount:N0} ({ToMb(lightmapCoordinateCount * sizeof(float)):N1} MB)");
            Console.WriteLine($"  Vertex colors: {ToMb(vertexColorBytes):N1} MB");
            Console.WriteLine($"  Unique lightmap textures: {lightmapTextures.Count:N0} ({ToMb(estimatedLightmapTextureBytes):N1} MB pixels)");
            Console.WriteLine($"  Lightmap texture keys: {lightmapKeys.Count:N0}");
            Console.WriteLine($"  Unique BitmapSource textures: {textures.Count:N0}");
            Console.WriteLine($"  Shared texture dictionaries: {textureDictionaries.Count:N0}");
            Console.WriteLine($"  Shared texture-name collections: {textureNameCollections.Count:N0}");
            Console.WriteLine($"  Mesh-name values/instances: {meshNameValues.Count:N0}/{meshNameInstances.Count:N0}");
            Console.WriteLine($"  Selected-texture values/instances: {selectedTextureValues.Count:N0}/{selectedTextureInstances.Count:N0}");
            Console.WriteLine(
                $"  Textures at 2048 cap: " +
                $"{textures.Count(texture => texture.PixelWidth >= 2048 || texture.PixelHeight >= 2048):N0}; " +
                $"below cap: {textures.Count(texture => texture.PixelWidth < 2048 && texture.PixelHeight < 2048):N0}");
            Console.WriteLine(
                "  Texture dimensions: " +
                string.Join(", ", textureDimensions
                    .OrderByDescending(pair => pair.Value)
                    .Take(8)
                    .Select(pair => $"{pair.Key.Width}x{pair.Key.Height} ({pair.Value})")));
            Console.WriteLine($"  WPF geometry/UV estimate: {ToMb(estimatedWpfBytes):N1} MB");
            Console.WriteLine($"  Texture pixel estimate: {ToMb(estimatedTextureBytes):N1} MB");
            Console.WriteLine($"  OpenGL renderer upload staging estimate: {ToMb(estimatedRendererUploadStagingBytes):N1} MB");
            Console.WriteLine($"  OpenGL renderer retained CPU-copy estimate: {ToMb(estimatedRendererRetainedCpuBytes):N1} MB");
            Console.WriteLine($"  OpenGL geometry buffer estimate: {ToMb(estimatedRendererGpuGeometryBytes):N1} MB");
            Console.WriteLine($"  OpenGL texture+mipmap estimate: {ToMb(estimatedRendererGpuTextureBytes):N1} MB");
            Console.WriteLine(
                $"  OpenGL GPU estimate (geometry + textures): " +
                $"{ToMb(estimatedRendererGpuGeometryBytes + estimatedRendererGpuTextureBytes):N1} MB");
        }

        private static void PrintMemory(string phase, Process process)
        {
            process.Refresh();
            long managedBytes = GC.GetTotalMemory(false);
            Console.WriteLine(
                $"[MapGeoMemory] {phase}: " +
                $"WorkingSet={ToMb(process.WorkingSet64):N1} MB, " +
                $"Private={ToMb(process.PrivateMemorySize64):N1} MB, " +
                $"Managed={ToMb(managedBytes):N1} MB");
        }

        private static double ToMb(long bytes) => bytes / (1024d * 1024d);
    }
}
