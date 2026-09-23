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

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Resolves the exact DX11 shader-cache permutation used by current LTK MAIN.
    /// Translation to SPIR-V/GLSL is intentionally a separate capability boundary.
    /// </summary>
    internal static class MapGameShaderProgramResolver
    {
        private const string ShaderCacheRelativePath = @"Game\DATA\FINAL\ShaderCache.dx11.wad.client";
        private const int RecordsPerBundle = 100;
        internal sealed record ShaderBytecodeProgram(
            IReadOnlyList<MapMaterialDefineData> Defines,
            byte[] Vertex,
            byte[] Pixel,
            ShaderReflectionData VertexReflection,
            ShaderReflectionData PixelReflection,
            string ShaderCachePath);

        internal sealed record ShaderBytecodeRead(ShaderBytecodeProgram Program, string Failure)
        {
            internal bool Ready => Program != null;
        }

        internal sealed record ShaderBytecodePassRead(
            MapResolvedMaterialPassData Pass,
            ShaderBytecodeRead Bytecode);

        internal sealed record ShaderBytecodeMaterialProgram(
            MapMaterialKind Kind,
            bool Animated,
            IReadOnlyList<ShaderBytecodePassRead> Passes)
        {
            internal bool Ready => Passes != null && Passes.Count > 0 && Passes.All(pass => pass.Bytecode.Ready);
        }

        internal static ShaderBytecodeRead Read(
            MapResolvedMaterialPassData pass,
            MapMaterialKind kind,
            AppSettings settings,
            bool lowQuality = false)
        {
            if (pass == null || string.IsNullOrWhiteSpace(pass.ShaderPath))
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
            MapResolvedMaterialProgramData program,
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
            MapResolvedMaterialProgramData program,
            WadFile wad,
            string cachePath,
            bool lowQuality = false)
        {
            if (program == null)
                return null;

            IReadOnlyList<MapResolvedMaterialPassData> passes = program.Passes ?? Array.Empty<MapResolvedMaterialPassData>();
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
                    MapResolvedMaterialPassData pass = passes[index];
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
            MapResolvedMaterialProgramData program,
            string failure)
        {
            IReadOnlyList<MapResolvedMaterialPassData> passes = program?.Passes ?? Array.Empty<MapResolvedMaterialPassData>();
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
            MapResolvedMaterialPassData pass,
            string failure) =>
            pass == null || string.IsNullOrWhiteSpace(pass.ShaderPath)
                ? new ShaderBytecodeRead(null, "The pass links no shader the defs declare.")
                : new ShaderBytecodeRead(null, failure);

        private static ShaderBytecodeRead ReadFromWad(
            MapResolvedMaterialPassData pass,
            MapMaterialKind kind,
            WadFile wad,
            string cachePath,
            bool lowQuality)
        {
            if (pass == null || string.IsNullOrWhiteSpace(pass.ShaderPath))
                return new ShaderBytecodeRead(null, "The pass links no shader the defs declare.");

            IReadOnlyList<MapMaterialDefineData> defines = BuildDefineList(pass, kind, lowQuality);
            try
            {
                byte[] vertex = ReadStage(wad, pass.ShaderPath, "vs", defines);
                byte[] pixel = ReadStage(wad, pass.ShaderPath, "ps", defines);
                ShaderReflectionData vertexReflection = DxbcReflection.Reflect(vertex);
                ShaderReflectionData pixelReflection = DxbcReflection.Reflect(pixel);
                return new ShaderBytecodeRead(
                    new ShaderBytecodeProgram(
                        defines,
                        vertex,
                        pixel,
                        vertexReflection,
                        pixelReflection,
                        cachePath),
                    null);
            }
            catch (Exception ex)
            {
                return new ShaderBytecodeRead(null, ex.Message);
            }
        }

        internal static IReadOnlyList<MapMaterialDefineData> BuildDefineList(
            MapResolvedMaterialPassData pass,
            MapMaterialKind kind,
            bool lowQuality)
        {
            var byName = new Dictionary<string, MapMaterialDefineData>(StringComparer.Ordinal);
            foreach (MapMaterialDefineData define in pass?.Defines ?? Array.Empty<MapMaterialDefineData>())
                byName[define.Name] = define;

            void AddMissing(string name, string value)
            {
                if (!byName.ContainsKey(name))
                    byName[name] = new MapMaterialDefineData(name, value, MapMaterialDefineSource.Feature);
            }

            AddMissing("DISABLE_FOW", "1");
            AddMissing("DISABLE_SHADOWS", "1");
            if (kind == MapMaterialKind.SkinnedMesh)
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

        internal static string TocPath(string shaderObjectPath, string stage) =>
            $"assets/shaders/generated/{shaderObjectPath.ToLowerInvariant()}.{stage}-dx11";

        internal static string BundlePath(string tocPath, uint shaderId) =>
            $"{tocPath}_{RecordsPerBundle * (shaderId / RecordsPerBundle)}";

        private static byte[] ReadStage(
            WadFile wad,
            string shaderObjectPath,
            string stage,
            IReadOnlyList<MapMaterialDefineData> defines)
        {
            string tocPath = TocPath(shaderObjectPath, stage);
            using var tocBytes = wad.LoadChunkDecompressed(XxHash64Ext.Hash(tocPath));
            using var tocStream = new MemoryStream(tocBytes.Span.ToArray(), writable: false);
            var toc = new ShaderToc(tocStream);

            var requested = defines
                .Select(define => new ShaderMacroDefinition(define.Name, define.Value))
                .Where(define => toc.BaseDefines.Any(baseDefine => baseDefine.Hash == define.Hash))
                .OrderBy(define => define.Name, StringComparer.Ordinal)
                .ToArray();
            string key = string.Concat(requested.Select(define => define.ToString()));
            ulong permutationHash = XxHash64Ext.Hash(key);
            int permutationIndex = -1;
            for (int index = 0; index < toc.ShaderHashes.Count; index++)
            {
                if (toc.ShaderHashes[index] == permutationHash)
                {
                    permutationIndex = index;
                    break;
                }
            }
            if (permutationIndex < 0)
                throw new InvalidOperationException($"Shader permutation not found for {shaderObjectPath}.{stage}: {key}");

            uint shaderId = toc.ShaderIds[permutationIndex];
            string bundlePath = BundlePath(tocPath, shaderId);
            using var bundleBytes = wad.LoadChunkDecompressed(XxHash64Ext.Hash(bundlePath));
            return ReadBundleRecord(bundleBytes.Span, shaderId % RecordsPerBundle);
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

    }
}

