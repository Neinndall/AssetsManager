using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Shaders;
using AssetsManager.Services.Viewer.Resolvers;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Settings;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Renderer;
using LeagueToolkit.Core.Wad;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    /// <summary>
    /// Resolves the exact DX11 shader-cache permutation required by a resolved game material pass.
    /// Translation to SPIR-V/GLSL is intentionally a separate capability boundary.
    /// </summary>
    internal static class GameShaderProgramResolver
    {
        public const string LitUberShaderName = "SkinnedMesh/LIT_UBER";
        public const string LitUberVertexPath = "ASSETS/Shaders/HLSL/SkinnedMesh/LIT_UBER_VS.vs";
        public const string LitUberPixelPath = "ASSETS/Shaders/HLSL/SkinnedMesh/LIT_UBER_PS.ps";
        public const string LitUberDiffuseTexture = "DIFFUSE_MAP";
        public const string LitUberEmissiveTexture = "EMISSIVE_MAP";

        private const string ShaderCacheRelativePath = @"Game\DATA\FINAL\ShaderCache.dx11.wad.client";
        private const int RecordsPerBundle = 100;
        internal sealed record ShaderBytecodeProgram(
            IReadOnlyList<GameMaterialDefine> Defines,
            byte[] Vertex,
            byte[] Pixel,
            ShaderReflectionData VertexReflection,
            ShaderReflectionData PixelReflection,
            string ShaderCachePath,
            string Fallback = null);

        internal sealed record ShaderBytecodeRead(ShaderBytecodeProgram Program, string Failure)
        {
            internal bool Ready => Program != null;
        }

        internal sealed record ShaderBytecodePassRead(
            GameMaterialPass Pass,
            ShaderBytecodeRead Bytecode);

        internal sealed record ShaderBytecodeMaterialProgram(
            GameMaterialKind Kind,
            bool Animated,
            IReadOnlyList<ShaderBytecodePassRead> Passes)
        {
            internal bool Ready => Passes != null && Passes.Count > 0 && Passes.All(pass => pass.Bytecode.Ready);
        }

        internal static ShaderBytecodeRead Read(
            GameMaterialPass pass,
            GameMaterialKind kind,
            AppSettings settings,
            bool lowQuality = false)
        {
            if (pass == null || !HasShaderPath(pass))
                return new ShaderBytecodeRead(null, "The pass links no shader the defs declare.");

            string cachePath = FindShaderCachePath(settings);
            if (cachePath == null)
                return new ShaderBytecodeRead(null, "ShaderCache.dx11.wad.client was not found in the configured game installs.");

            try
            {
                using var wad = new WadFile(cachePath);
                return ReadFromWad(pass, kind, wad, cachePath, lowQuality);
            }
            catch (Exception ex)
            {
                return new ShaderBytecodeRead(null, ex.Message);
            }
        }

        internal static ShaderBytecodeMaterialProgram ReadProgram(
            GameMaterialProgram program,
            AppSettings settings,
            bool lowQuality = false)
        {
            if (program == null)
                return null;

            string cachePath = FindShaderCachePath(settings);
            if (cachePath == null)
                return UnavailableProgram(program, "ShaderCache.dx11.wad.client was not found in the configured game installs.");

            try
            {
                using var wad = new WadFile(cachePath);
                return ReadProgram(program, wad, cachePath, lowQuality);
            }
            catch (Exception ex)
            {
                return UnavailableProgram(program, ex.Message);
            }
        }

        /// <summary>
        /// Reads every pass through an already-open shader cache. The MAP renderer keeps one WAD
        /// open for a scene instead of reopening the 300+ MB cache once per material.
        /// </summary>
        internal static ShaderBytecodeMaterialProgram ReadProgram(
            GameMaterialProgram program,
            WadFile wad,
            string cachePath,
            bool lowQuality = false)
        {
            if (program == null)
                return null;

            IReadOnlyList<GameMaterialPass> passes = program.Passes ?? Array.Empty<GameMaterialPass>();
            if (passes.Count == 0)
            {
                return new ShaderBytecodeMaterialProgram(
                    program.Kind,
                    program.Animated,
                    Array.Empty<ShaderBytecodePassRead>());
            }
            if (wad == null)
                return UnavailableProgram(program, "ShaderCache.dx11.wad.client is not open.");

            try
            {
                var reads = new ShaderBytecodePassRead[passes.Count];
                for (int index = 0; index < passes.Count; index++)
                {
                    GameMaterialPass pass = passes[index];
                    reads[index] = new ShaderBytecodePassRead(
                        pass,
                        ReadFromWad(pass, program.Kind, wad, cachePath, lowQuality));
                }
                return new ShaderBytecodeMaterialProgram(program.Kind, program.Animated, reads);
            }
            catch (Exception ex)
            {
                return UnavailableProgram(program, ex.Message);
            }
        }

        private static ShaderBytecodeMaterialProgram UnavailableProgram(
            GameMaterialProgram program,
            string failure)
        {
            IReadOnlyList<GameMaterialPass> passes = program?.Passes ?? Array.Empty<GameMaterialPass>();
            return program == null
                ? null
                : new ShaderBytecodeMaterialProgram(
                    program.Kind,
                    program.Animated,
                    passes.Select(pass => new ShaderBytecodePassRead(
                        pass,
                        UnavailableRead(pass, failure))).ToArray());
        }

        private static ShaderBytecodeRead UnavailableRead(
            GameMaterialPass pass,
            string failure) =>
            pass == null || !HasShaderPath(pass)
                ? new ShaderBytecodeRead(null, "The pass links no shader the defs declare.")
                : new ShaderBytecodeRead(null, failure);

        private static bool HasShaderPath(GameMaterialPass pass) =>
            !string.IsNullOrWhiteSpace(ShaderPathForStage(pass, vertexStage: true)) &&
            !string.IsNullOrWhiteSpace(ShaderPathForStage(pass, vertexStage: false));

        private static string ShaderPathForStage(GameMaterialPass pass, bool vertexStage)
        {
            if (pass == null)
                return null;

            string stagePath = vertexStage ? pass.VertexShaderPath : pass.PixelShaderPath;
            return !string.IsNullOrWhiteSpace(stagePath) ? stagePath : pass.ShaderPath;
        }

        internal static ShaderBytecodeRead ReadFromWad(
            GameMaterialPass pass,
            GameMaterialKind kind,
            WadFile wad,
            string cachePath,
            bool lowQuality)
        {
            if (pass == null || !HasShaderPath(pass))
                return new ShaderBytecodeRead(null, "The pass links no shader the defs declare.");

            IReadOnlyList<GameMaterialDefine> defines = BuildDefineList(pass, kind, lowQuality);
            string vertexPath = ShaderPathForStage(pass, vertexStage: true);
            string pixelPath = ShaderPathForStage(pass, vertexStage: false);
            try
            {
                string vertexTocPath = TocPath(vertexPath, "vs");
                string pixelTocPath = TocPath(pixelPath, "ps");
                ShaderToc vertexToc = ReadToc(wad, vertexTocPath);
                ShaderToc pixelToc = ReadToc(wad, pixelTocPath);
                var pairs = defines.Select(d => new KeyValuePair<string, string>(d.Name, d.Value));
                // Native HLSL pairs require their authored features. Only generated materials use nearest fallback.
                bool fallback = vertexTocPath.StartsWith("assets/shaders/generated/", StringComparison.Ordinal) &&
                                pixelTocPath.StartsWith("assets/shaders/generated/", StringComparison.Ordinal);
                var match = GameShaderPermutationLookup.FindPair(vertexToc, pixelToc, pairs, fallback);
                if (match == null)
                    return new ShaderBytecodeRead(null, $"No common shader permutation for {vertexPath} / {pixelPath}.");
                IReadOnlyList<GameMaterialDefine> resolved = Apply(defines, match.Selection);
                byte[] vertex = ReadStage(wad, vertexTocPath, vertexToc.ShaderIds[match.VertexIndex]);
                byte[] pixel = ReadStage(wad, pixelTocPath, pixelToc.ShaderIds[match.PixelIndex]);

                ShaderReflectionData vertexReflection = DxbcReflection.Reflect(vertex);
                ShaderReflectionData pixelReflection = DxbcReflection.Reflect(pixel);
                string[] changes = match.Selection.Changes
                    .Select(change => change.ToString())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                return new ShaderBytecodeRead(
                    new ShaderBytecodeProgram(
                        resolved,
                        vertex,
                        pixel,
                        vertexReflection,
                        pixelReflection,
                        cachePath,
                        changes.Length == 0 ? null : string.Join(" ", changes)),
                    null);
            }
            catch (Exception ex)
            {
                return new ShaderBytecodeRead(null, ex.Message);
            }
        }

        internal static IReadOnlyList<GameMaterialDefine> BuildDefineList(
            GameMaterialPass pass,
            GameMaterialKind kind,
            bool lowQuality)
        {
            var byName = new Dictionary<string, GameMaterialDefine>(StringComparer.Ordinal);
            foreach (GameMaterialDefine define in pass?.Defines ?? Array.Empty<GameMaterialDefine>())
                byName[define.Name] = define;

            void AddMissing(string name, string value)
            {
                if (!byName.ContainsKey(name))
                    byName[name] = new GameMaterialDefine(name, value, GameMaterialDefineSource.Feature);
            }

            AddMissing("DISABLE_FOW", "1");
            AddMissing("DISABLE_SHADOWS", "1");
            if (kind == GameMaterialKind.SkinnedMesh)
                AddMissing("NUM_BLEND_WEIGHTS", "4");
            if (lowQuality)
                AddMissing("LOW_QUALITY_MODE", "1");

            return byName.Values.OrderBy(define => define.Name, StringComparer.Ordinal).ToArray();
        }

        internal static string FindShaderCachePath(AppSettings settings)
        {
            foreach (string root in MapAssetResolver.GetInstallationRoots(settings))
            {
                string candidate = Path.Combine(root, ShaderCacheRelativePath);
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }

        internal static string TocPath(string shaderObjectPath, string stage)
        {
            if (string.Equals(shaderObjectPath, LitUberShaderName, StringComparison.OrdinalIgnoreCase))
            {
                string hlslFile = string.Equals(stage, "vs", StringComparison.OrdinalIgnoreCase)
                    ? LitUberVertexPath
                    : LitUberPixelPath;
                return $"{hlslFile.ToLowerInvariant()}-dx11";
            }

            if (shaderObjectPath.EndsWith(".vs", StringComparison.OrdinalIgnoreCase) ||
                shaderObjectPath.EndsWith(".ps", StringComparison.OrdinalIgnoreCase) ||
                shaderObjectPath.IndexOf("hlsl/", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return $"{shaderObjectPath.ToLowerInvariant()}-dx11";
            }

            return $"assets/shaders/generated/{shaderObjectPath.ToLowerInvariant()}.{stage}-dx11";
        }

        internal static string BundlePath(string tocPath, uint shaderId) =>
            $"{tocPath}_{RecordsPerBundle * (shaderId / RecordsPerBundle)}";

        private static ShaderToc ReadToc(WadFile wad, string tocPath)
        {
            using var tocBytes = wad.LoadChunkDecompressed(XxHash64Ext.Hash(tocPath));
            using var tocStream = new MemoryStream(tocBytes.Span.ToArray(), writable: false);
            return new ShaderToc(tocStream);
        }

        private static byte[] ReadStage(WadFile wad, string tocPath, uint shaderId)
        {
            string bundlePath = BundlePath(tocPath, shaderId);
            using var bundleBytes = wad.LoadChunkDecompressed(XxHash64Ext.Hash(bundlePath));
            return ReadBundleRecord(bundleBytes.Span, shaderId % RecordsPerBundle);
        }

        /// <returns><paramref name="defines"/> with the define changes a permutation fallback made.</returns>
        private static IReadOnlyList<GameMaterialDefine> Apply(
            IReadOnlyList<GameMaterialDefine> defines,
            GameShaderPermutationLookup.Match match)
        {
            if (match.Exact)
                return defines;

            var byName = defines.ToDictionary(define => define.Name, StringComparer.Ordinal);
            foreach (GameShaderPermutationLookup.Change change in match.Changes)
            {
                if (change.Value == null)
                    byName.Remove(change.Name);
                else
                    byName[change.Name] = new GameMaterialDefine(change.Name, change.Value, GameMaterialDefineSource.Feature);
            }
            return byName.Values.OrderBy(define => define.Name, StringComparer.Ordinal).ToArray();
        }

        internal static byte[] ReadBundleRecord(ReadOnlySpan<byte> bundle, uint index)
        {
            int at = 0;
            for (uint record = 0; record <= index; record++)
            {
                if (at > bundle.Length - sizeof(uint))
                    throw new InvalidDataException($"Shader bundle ends before record {index}.");
                int recordSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bundle.Slice(at, sizeof(uint))));
                int body = checked(at + sizeof(uint));
                if (recordSize < 0 || body > bundle.Length - recordSize)
                    throw new InvalidDataException($"Shader bundle record {record} is truncated.");
                if (record == index)
                    return DxbcReflection.TrimContainer(bundle.Slice(body, recordSize));
                at = checked(body + recordSize);
            }
            throw new InvalidDataException($"Shader bundle ends before record {index}.");
        }

        // The engine's default sampler state wraps on every axis (LTK SamplerState::default); critter UVs span 0..2.
        public static GameMaterialProgram CreateDefaultSkinnedProgram(
            string diffuseTextureKey,
            string emissiveTextureKey = null, string rmaTextureKey = null, string normalTextureKey = null)
        {
            var textures = new List<GameMaterialTexture>
            {
                new(
                    LitUberDiffuseTexture,
                    !string.IsNullOrWhiteSpace(diffuseTextureKey)
                        ? new MapTextureReference(diffuseTextureKey, XxHash64Ext.Hash(diffuseTextureKey))
                        : null,
                    GameMaterialTextureSource.Fallback,
                    new GameMaterialSamplerState(
                        null,
                        MapTextureWrap.Repeat,
                        MapTextureWrap.Repeat,
                        MapTextureWrap.Repeat,
                        FilterMin: true,
                        FilterMag: true))
            };

            if (string.IsNullOrWhiteSpace(rmaTextureKey) && !string.IsNullOrWhiteSpace(emissiveTextureKey))
            {
                textures.Add(new(
                    LitUberEmissiveTexture,
                    new MapTextureReference(emissiveTextureKey, XxHash64Ext.Hash(emissiveTextureKey)),
                    GameMaterialTextureSource.Fallback,
                    new GameMaterialSamplerState(
                        null,
                        MapTextureWrap.Repeat,
                        MapTextureWrap.Repeat,
                        MapTextureWrap.Repeat,
                        FilterMin: true,
                        FilterMag: true)));
            }

            var defines = new List<GameMaterialDefine>();
            void Map(string name, string path)
            {
                textures.Add(new(name, new MapTextureReference(path, XxHash64Ext.Hash(path)),
                    GameMaterialTextureSource.Fallback, new GameMaterialSamplerState(null,
                        MapTextureWrap.Repeat, MapTextureWrap.Repeat, MapTextureWrap.Repeat,
                        FilterMin: true, FilterMag: true)));
            }
            if (!string.IsNullOrWhiteSpace(rmaTextureKey))
            {
                defines.Add(new("FEATURE_ROUGHNESS_METALLIC", "1", GameMaterialDefineSource.Skin));
                Map("RMA_MAP", rmaTextureKey);
                if (!string.IsNullOrWhiteSpace(normalTextureKey))
                {
                    defines.Add(new("FEATURE_TANGENT", "1", GameMaterialDefineSource.Skin));
                    Map("NORMAL_MAP", normalTextureKey);
                }
            }
            var pass = new GameMaterialPass(
                ShaderHash: 0,
                ShaderPath: LitUberShaderName,
                Defines: defines,
                RuntimeSwitches: Array.Empty<KeyValuePair<string, bool>>(),
                Textures: textures,
                Parameters: Array.Empty<GameMaterialParameter>(),
                // LIT_UBER writes the diffuse alpha, which the engine blends, as ModelMaterialRenderState.TextureOnly.
                State: GameMaterialPassState.Default with
                {
                    BlendEnabled = true,
                    SourceColor = MapBlendFactor.SourceAlpha,
                    DestinationColor = MapBlendFactor.OneMinusSourceAlpha,
                    SourceAlpha = MapBlendFactor.One,
                    DestinationAlpha = MapBlendFactor.OneMinusSourceAlpha
                });

            return new GameMaterialProgram(
                GameMaterialKind.SkinnedMesh,
                Animated: false,
                Passes: new[] { pass });
        }

    }
}
