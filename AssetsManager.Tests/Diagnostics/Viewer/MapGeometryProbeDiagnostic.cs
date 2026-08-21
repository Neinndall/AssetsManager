using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Media3D;
using AssetsManager.Services.Core;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Environment;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Memory;
using LeagueToolkit.Core.Primitives;
using Serilog;

namespace AssetsManager.Tests.Diagnostics.Viewer
{
    /// <summary>
    /// Inspects a MapGeo through LeagueToolkit and the AssetsManager scene conversion.
    /// </summary>
    internal static class MapGeometryProbeDiagnostic
    {
        public static void Run(string mapGeoPath, string materialsPath, string gameDataPath)
        {
            if (string.IsNullOrWhiteSpace(mapGeoPath))
            {
                Console.WriteLine(
                    "Usage: dotnet run --project AssetsManager.Tests/AssetsManager.Tests.csproj -- " +
                    "mapgeo-probe <mapgeo> [materials.bin|-] [game-data-root]");
                return;
            }

            mapGeoPath = Path.GetFullPath(mapGeoPath);
            materialsPath = NormalizeOptionalPath(materialsPath);
            gameDataPath = string.IsNullOrWhiteSpace(gameDataPath)
                ? Path.GetDirectoryName(mapGeoPath)
                : Path.GetFullPath(gameDataPath);

            if (!File.Exists(mapGeoPath))
            {
                Console.WriteLine($"[MapGeoProbe] MapGeo not found: {mapGeoPath}");
                return;
            }

            if (materialsPath != null && !File.Exists(materialsPath))
            {
                Console.WriteLine($"[MapGeoProbe] materials.bin not found: {materialsPath}");
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
                : Path.GetFullPath(path);

        private static void RunOnDispatcher(string mapGeoPath, string materialsPath, string gameDataPath)
        {
            var application = new Application
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            var dispatcher = application.Dispatcher;
            Task probeTask = RunProbeAsync(dispatcher, mapGeoPath, materialsPath, gameDataPath);

            probeTask.ContinueWith(
                _ => dispatcher.BeginInvokeShutdown(System.Windows.Threading.DispatcherPriority.Normal),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            System.Windows.Threading.Dispatcher.Run();
            application.Shutdown();
            probeTask.GetAwaiter().GetResult();
        }

        private static async Task RunProbeAsync(
            System.Windows.Threading.Dispatcher dispatcher,
            string mapGeoPath,
            string materialsPath,
            string gameDataPath)
        {
            PrintFileHeader(mapGeoPath);
            using (var stream = File.OpenRead(mapGeoPath))
            using (var mapGeometry = new EnvironmentAsset(stream))
            {
                PrintRawMapGeometry(mapGeometry);

                if (materialsPath != null)
                    PrintMaterialDefinitions(mapGeometry, materialsPath);
            }

            var logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.Console()
                .CreateLogger();
            var service = new MapGeometryLoadingService(new LogService(logger));
            SceneModel model = await service.LoadMapGeometry(
                mapGeoPath,
                materialsPath,
                gameDataPath,
                CancellationToken.None);

            if (model == null)
            {
                Console.WriteLine("[MapGeoProbe] AssetsManager returned no SceneModel.");
                return;
            }

            await dispatcher.InvokeAsync(() => PrintConvertedScene(model));
            await dispatcher.InvokeAsync(model.Dispose);
        }

        private static void PrintFileHeader(string path)
        {
            byte[] header = new byte[8];
            using (FileStream stream = File.OpenRead(path))
            {
                int read = stream.Read(header, 0, header.Length);
                if (read < header.Length)
                    throw new InvalidDataException("MapGeo header is truncated.");
            }

            string magic = Encoding.ASCII.GetString(header, 0, 4);
            int version = BitConverter.ToInt32(header, 4);
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            var info = new FileInfo(path);

            Console.WriteLine("[MapGeoProbe] File");
            Console.WriteLine($"  Path: {path}");
            Console.WriteLine($"  Length: {info.Length:N0} bytes");
            Console.WriteLine($"  SHA256: {hash}");
            Console.WriteLine($"  Header: {magic} / version {version}");
        }

        private static void PrintRawMapGeometry(EnvironmentAsset mapGeometry)
        {
            var localBounds = new BoundsAccumulator();
            var transformedBounds = new BoundsAccumulator();
            var translationBounds = new BoundsAccumulator();
            var visibilityCounts = new Dictionary<EnvironmentVisibility, int>();
            var materials = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var channelTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int totalVertices = 0;
            int totalIndices = 0;
            int totalSubmeshes = 0;
            int invalidRanges = 0;
            int invalidIndices = 0;
            int nonFinitePositions = 0;
            int nonFiniteTransforms = 0;
            int meshesWithTransforms = 0;

            foreach (EnvironmentAssetMesh mesh in mapGeometry.Meshes)
            {
                totalVertices += mesh.VerticesView.VertexCount;
                totalIndices += mesh.Indices.Count;
                totalSubmeshes += mesh.Submeshes.Count;
                localBounds.Add(mesh.BoundingBox);

                if (!IsIdentity(mesh.Transform))
                    meshesWithTransforms++;
                if (!IsFinite(mesh.Transform))
                    nonFiniteTransforms++;
                else
                {
                    transformedBounds.Add(mesh.BoundingBox, mesh.Transform);
                    translationBounds.Add(new Vector3(
                        mesh.Transform.M41,
                        mesh.Transform.M42,
                        mesh.Transform.M43));
                }

                visibilityCounts[mesh.VisibilityFlags] = visibilityCounts.TryGetValue(
                    mesh.VisibilityFlags,
                    out int visibilityCount)
                    ? visibilityCount + 1
                    : 1;

                AddChannelTexture(channelTextures, mesh.StationaryLight.Texture);
                AddChannelTexture(channelTextures, mesh.BakedLight.Texture);
                foreach (EnvironmentAssetMeshTextureOverride textureOverride in mesh.TextureOverrides)
                    AddChannelTexture(channelTextures, textureOverride.Texture);

                var positions = mesh.VerticesView
                    .GetAccessor(VertexElement.POSITION.Name)
                    .AsVector3Array();
                for (int i = 0; i < positions.Count; i++)
                {
                    if (!IsFinite(positions[i]))
                        nonFinitePositions++;
                }

                foreach (EnvironmentAssetMeshPrimitive submesh in mesh.Submeshes)
                {
                    string material = submesh.Material ?? "<null>";
                    materials[material] = materials.TryGetValue(material, out int count)
                        ? count + 1
                        : 1;

                    long end = (long)submesh.StartIndex + submesh.IndexCount;
                    if (submesh.StartIndex < 0 || submesh.IndexCount < 0 || end > mesh.Indices.Count)
                    {
                        invalidRanges++;
                        continue;
                    }

                    for (int index = submesh.StartIndex; index < end; index++)
                    {
                        uint value = mesh.Indices[index];
                        if (value >= positions.Count ||
                            value < submesh.MinVertex ||
                            value > submesh.MaxVertex)
                        {
                            invalidIndices++;
                        }
                    }
                }
            }

            EnvironmentVisibility? baseVisibility = ResolveBaseVisibility(mapGeometry.Meshes.Select(x => x.VisibilityFlags));
            int baseVisibleMeshes = mapGeometry.Meshes.Count(x => IsVisible(x.VisibilityFlags, baseVisibility));

            Console.WriteLine("[MapGeoProbe] LeagueToolkit parse");
            Console.WriteLine($"  Meshes: {mapGeometry.Meshes.Count:N0}");
            Console.WriteLine($"  Scene graphs: {mapGeometry.SceneGraphs.Count:N0}");
            Console.WriteLine($"  Planar reflectors: {mapGeometry.PlanarReflectors.Count:N0}");
            Console.WriteLine($"  Shader texture overrides: {mapGeometry.ShaderTextureOverrides.Count:N0}");
            if (mapGeometry.ShaderTextureOverrides.Count > 0)
            {
                Console.WriteLine(
                    $"  Shader override values: {string.Join(", ", mapGeometry.ShaderTextureOverrides.Select(x => $"{x.Index}:{x.Name}"))}");
            }
            Console.WriteLine($"  Vertices / indices / submeshes: {totalVertices:N0} / {totalIndices:N0} / {totalSubmeshes:N0}");
            Console.WriteLine($"  Base visibility: {baseVisibility?.ToString() ?? "<none>"} ({baseVisibleMeshes:N0}/{mapGeometry.Meshes.Count:N0} meshes)");
            Console.WriteLine($"  Mesh transforms: {meshesWithTransforms:N0}; non-finite transforms: {nonFiniteTransforms:N0}");
            Console.WriteLine($"  Non-finite positions: {nonFinitePositions:N0}");
            Console.WriteLine($"  Invalid submesh ranges / indices: {invalidRanges:N0} / {invalidIndices:N0}");
            Console.WriteLine($"  Visibility flags: {FormatCounts(visibilityCounts)}");
            Console.WriteLine($"  Material count/top: {materials.Count:N0} / {FormatTop(materials)}");
            Console.WriteLine($"  Channel textures: {channelTextures.Count:N0}");
            PrintBounds("  Local bounds", localBounds);
            PrintBounds("  Transform bounds", transformedBounds);
            PrintBounds("  Transform translation", translationBounds);
        }

        private static void PrintConvertedScene(SceneModel model)
        {
            var localBounds = new BoundsAccumulator();
            var transformedBounds = new BoundsAccumulator();
            var materials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var textures = new HashSet<object>(ReferenceEqualityComparer.Instance);
            int visibleParts = 0;
            int transformedParts = 0;
            int nonFinitePositions = 0;
            int invalidIndices = 0;
            int totalVertices = 0;
            int totalIndices = 0;

            foreach (ModelPart part in model.Parts)
            {
                if (part.IsVisible)
                    visibleParts++;
                if (!string.IsNullOrWhiteSpace(part.Name))
                    materials.Add(part.Name);
                if (part.AllTextures != null)
                {
                    foreach (var texture in part.AllTextures.Values)
                    {
                        if (texture != null)
                            textures.Add(texture);
                    }
                }

                if (part.Geometry?.Geometry is not MeshGeometry3D mesh)
                    continue;

                totalVertices += mesh.Positions?.Count ?? 0;
                totalIndices += mesh.TriangleIndices?.Count ?? 0;
                if (mesh.Positions != null)
                {
                    foreach (Point3D position in mesh.Positions)
                    {
                        if (!IsFinite(position))
                            nonFinitePositions++;
                    }
                }
                if (mesh.TriangleIndices != null && mesh.Positions != null)
                {
                    foreach (int index in mesh.TriangleIndices)
                    {
                        if (index < 0 || index >= mesh.Positions.Count)
                            invalidIndices++;
                    }
                }

                if (mesh.Bounds.IsEmpty)
                    continue;

                localBounds.Add(mesh.Bounds);
                Transform3D transform = part.Geometry.Transform ?? Transform3D.Identity;
                transformedBounds.Add(mesh.Bounds, transform);
                if (!IsIdentity(transform))
                    transformedParts++;
            }

            Console.WriteLine("[MapGeoProbe] AssetsManager scene conversion");
            Console.WriteLine($"  SceneModel: {model.Name}; parts: {model.Parts.Count:N0}; visible: {visibleParts:N0}");
            Console.WriteLine($"  Vertices / indices: {totalVertices:N0} / {totalIndices:N0}");
            Console.WriteLine($"  Non-finite positions / invalid indices: {nonFinitePositions:N0} / {invalidIndices:N0}");
            Console.WriteLine($"  Parts with geometry transforms: {transformedParts:N0}");
            Console.WriteLine($"  Unique part materials: {materials.Count:N0}");
            Console.WriteLine($"  Unique decoded textures: {textures.Count:N0}");
            PrintBounds("  Local WPF bounds", localBounds);
            PrintBounds("  Geometry-transformed bounds", transformedBounds);
            Console.WriteLine("  Camera diagnostic: ResetCamera currently frames local WPF bounds, while OpenGL renders GeometryModel3D transforms.");
        }

        private static void PrintMaterialDefinitions(
            EnvironmentAsset mapGeometry,
            string materialsPath)
        {
            using var stream = File.OpenRead(materialsPath);
            var materialsBin = new BinTree(stream);
            var resolver = new MapGeometryMaterialResolver(materialsBin);
            var materialNames = mapGeometry.Meshes
                .SelectMany(mesh => mesh.Submeshes)
                .Select(submesh => submesh.Material)
                .Where(material => !string.IsNullOrWhiteSpace(material))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(material => material, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            int resolved = 0;
            int samplerDefinitions = 0;
            var samplerNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string materialName in materialNames)
            {
                if (!resolver.TryResolve(materialName, out MapGeometryMaterialDefinition definition))
                    continue;

                resolved++;
                samplerDefinitions += definition.Samplers.Count;
                foreach (MapGeometryTextureSampler sampler in definition.Samplers)
                {
                    string samplerName = string.IsNullOrWhiteSpace(sampler.TextureName)
                        ? sampler.SamplerName
                        : sampler.TextureName;
                    samplerName = string.IsNullOrWhiteSpace(samplerName) ? "<empty>" : samplerName;
                    samplerNames[samplerName] = samplerNames.TryGetValue(samplerName, out int count)
                        ? count + 1
                        : 1;
                }
            }

            Console.WriteLine("[MapGeoProbe] Materials metadata");
            Console.WriteLine($"  Distinct MapGeo materials / resolved: {materialNames.Length:N0} / {resolved:N0}");
            Console.WriteLine($"  Sampler definitions: {samplerDefinitions:N0}");
            Console.WriteLine($"  Sampler names: {FormatTop(samplerNames)}");

            foreach (string materialName in materialNames.Take(8))
            {
                if (!resolver.TryResolve(materialName, out MapGeometryMaterialDefinition definition))
                    continue;

                MapGeometryMaterialPlan plan = MapGeometryMaterialResolver.CreateRenderPlan(definition);
                string samplers = definition.Samplers.Count == 0
                    ? "<none>"
                    : string.Join(", ", definition.Samplers.Select(sampler =>
                        $"{sampler.TextureName ?? "<null>"}={sampler.TexturePath ?? "<null>"}"));
                Console.WriteLine(
                    $"  Material: {materialName} | plan={plan.Kind} | tint={definition.TintColor?.ToString() ?? "<none>"} | samplers={samplers}");
            }
        }

        private static EnvironmentVisibility? ResolveBaseVisibility(IEnumerable<EnvironmentVisibility> visibilities)
        {
            EnvironmentVisibility usedLayers = EnvironmentVisibility.NoLayer;
            foreach (EnvironmentVisibility visibility in visibilities)
            {
                if (visibility is not (EnvironmentVisibility.NoLayer or EnvironmentVisibility.AllLayers))
                    usedLayers |= visibility;
            }

            for (int bit = 0; bit < 8; bit++)
            {
                var layer = (EnvironmentVisibility)(1 << bit);
                if ((usedLayers & layer) != 0)
                    return layer;
            }

            return null;
        }

        private static bool IsVisible(EnvironmentVisibility visibility, EnvironmentVisibility? baseVisibility) =>
            baseVisibility == null ||
            visibility is EnvironmentVisibility.NoLayer or EnvironmentVisibility.AllLayers ||
            (visibility & baseVisibility.Value) != 0;

        private static void AddChannelTexture(ISet<string> textures, string texture)
        {
            if (!string.IsNullOrWhiteSpace(texture))
                textures.Add(texture);
        }

        private static string FormatCounts<TKey>(IReadOnlyDictionary<TKey, int> counts) =>
            string.Join(", ", counts.OrderByDescending(x => x.Value).Select(x => $"{x.Key}={x.Value:N0}"));

        private static string FormatTop(IReadOnlyDictionary<string, int> counts) =>
            string.Join(", ", counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key).Take(8)
                .Select(x => $"{x.Key} ({x.Value:N0})"));

        private static void PrintBounds(string label, BoundsAccumulator bounds)
        {
            Console.WriteLine(bounds.HasValue
                ? $"{label}: min={FormatVector(bounds.Min)} max={FormatVector(bounds.Max)} size={FormatVector(bounds.Size)} center={FormatVector(bounds.Center)}"
                : $"{label}: <empty>");
        }

        private static bool IsIdentity(Matrix4x4 matrix) => matrix == Matrix4x4.Identity;

        private static bool IsIdentity(Transform3D transform)
        {
            if (transform == null)
                return true;

            Point3D[] points =
            {
                new Point3D(0, 0, 0),
                new Point3D(1, 2, 3)
            };
            return points.All(point => transform.Transform(point) == point);
        }

        private static bool IsFinite(Matrix4x4 matrix) =>
            IsFinite(matrix.M11) && IsFinite(matrix.M12) && IsFinite(matrix.M13) && IsFinite(matrix.M14) &&
            IsFinite(matrix.M21) && IsFinite(matrix.M22) && IsFinite(matrix.M23) && IsFinite(matrix.M24) &&
            IsFinite(matrix.M31) && IsFinite(matrix.M32) && IsFinite(matrix.M33) && IsFinite(matrix.M34) &&
            IsFinite(matrix.M41) && IsFinite(matrix.M42) && IsFinite(matrix.M43) && IsFinite(matrix.M44);

        private static bool IsFinite(Vector3 value) =>
            IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);

        private static bool IsFinite(Point3D value) =>
            IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);

        private static bool IsFinite(float value) => float.IsFinite(value);
        private static bool IsFinite(double value) => double.IsFinite(value);

        private static string FormatVector(Vector3 value) =>
            $"({value.X:G9}, {value.Y:G9}, {value.Z:G9})";

        private static string FormatVector(Point3D value) =>
            $"({value.X:G9}, {value.Y:G9}, {value.Z:G9})";

        private sealed class BoundsAccumulator
        {
            public bool HasValue { get; private set; }
            public Vector3 Min { get; private set; }
            public Vector3 Max { get; private set; }

            public Vector3 Size => Max - Min;
            public Vector3 Center => (Min + Max) / 2f;

            public void Add(Box box)
            {
                Add(box.Min);
                Add(box.Max);
            }

            public void Add(BoundsAccumulator other)
            {
                if (other.HasValue)
                {
                    Add(other.Min);
                    Add(other.Max);
                }
            }

            public void Add(Rect3D bounds)
            {
                Add(new Vector3((float)bounds.X, (float)bounds.Y, (float)bounds.Z));
                Add(new Vector3(
                    (float)(bounds.X + bounds.SizeX),
                    (float)(bounds.Y + bounds.SizeY),
                    (float)(bounds.Z + bounds.SizeZ)));
            }

            public void Add(Box box, Matrix4x4 transform)
            {
                for (int i = 0; i < Box.VERTEX_COUNT; i++)
                    Add(Vector3.Transform(box.GetVertex(i), transform));
            }

            public void Add(Rect3D bounds, Transform3D transform)
            {
                if (bounds.IsEmpty)
                    return;

                for (int i = 0; i < 8; i++)
                {
                    Point3D point = i switch
                    {
                        0 => new Point3D(bounds.X, bounds.Y, bounds.Z),
                        1 => new Point3D(bounds.X, bounds.Y + bounds.SizeY, bounds.Z),
                        2 => new Point3D(bounds.X + bounds.SizeX, bounds.Y, bounds.Z),
                        3 => new Point3D(bounds.X + bounds.SizeX, bounds.Y + bounds.SizeY, bounds.Z),
                        4 => new Point3D(bounds.X, bounds.Y, bounds.Z + bounds.SizeZ),
                        5 => new Point3D(bounds.X, bounds.Y + bounds.SizeY, bounds.Z + bounds.SizeZ),
                        6 => new Point3D(bounds.X + bounds.SizeX, bounds.Y, bounds.Z + bounds.SizeZ),
                        _ => new Point3D(bounds.X + bounds.SizeX, bounds.Y + bounds.SizeY, bounds.Z + bounds.SizeZ)
                    };
                    Point3D transformed = transform.Transform(point);
                    Add(new Vector3((float)transformed.X, (float)transformed.Y, (float)transformed.Z));
                }
            }

            public void Add(Vector3 value)
            {
                if (!IsFinite(value))
                    return;

                if (!HasValue)
                {
                    Min = value;
                    Max = value;
                    HasValue = true;
                    return;
                }

                Min = Vector3.Min(Min, value);
                Max = Vector3.Max(Max, value);
            }
        }
    }
}
