using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Shaders;
using System.Numerics;
using System.Text.RegularExpressions;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Utils;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Wad;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    /// <summary>
    /// OpenGL execution layer for translated game material programs. Resolution/translation stay in
    /// GameShaderProgramResolver/Translator; this class owns only GL programs, UBOs and bindings.
    /// </summary>
    internal sealed class GameShaderRuntime : IDisposable
    {
        private const string Globals = "$Globals";
        private const string MaterialTextureSuffix = "__TX";
        private const string SharedTextureSuffix = "_SharedTexture";
        private const string BakedLightTexture = "BAKED_LIGHT__TX";
        private const string StationaryLightTexture = "STATIONARY_LIGHT__TX";
        private const string BakedLightTransform = "BAKED_LIGHT_SCALE_AND_BIAS";
        private const string StationaryLightTransform = "STATIONARY_LIGHT_SCALE_AND_BIAS";
        private const int WriteDepth = 16;
        private const int WriteColor = 15;
        private const float FogStart = -100_000f;
        private const float FogEnd = -100_001f;

        internal readonly record struct Frame(
            Matrix4x4 View,
            Matrix4x4 Projection,
            Vector3 Eye,
            float TimeSeconds,
            MapSunData Sun);

        private readonly record struct CharacterDraw(
            Matrix4x4 World,
            IReadOnlyList<Matrix4x4> Bones,
            float SelfIllumination = 0f);

        private sealed class BlockRuntime
        {
            internal GameShaderTranslator.UniformBlock Block;
            internal uint Buffer;
            internal uint Binding;
            internal float[] Data;
        }

        private sealed record SamplerRuntime(
            string TextureName,
            GameShaderTranslator.TextureDimension Dimension,
            string LogicalSampler,
            int Location,
            uint Unit);

        private sealed class ProgramRuntime : IDisposable
        {
            private readonly GL _gl;
            internal readonly uint Program;
            internal readonly IReadOnlyList<BlockRuntime> Blocks;
            internal readonly IReadOnlyList<SamplerRuntime> Samplers;
            internal readonly IReadOnlyDictionary<uint, string> Attributes;

            internal ProgramRuntime(
                GL gl,
                uint program,
                IReadOnlyList<BlockRuntime> blocks,
                IReadOnlyList<SamplerRuntime> samplers,
                IReadOnlyDictionary<uint, string> attributes)
            {
                _gl = gl;
                Program = program;
                Blocks = blocks;
                Samplers = samplers;
                Attributes = attributes;
            }

            public void Dispose()
            {
                foreach (BlockRuntime block in Blocks)
                    if (block.Buffer != 0)
                        _gl.DeleteBuffer(block.Buffer);
                if (Program != 0)
                    _gl.DeleteProgram(Program);
            }
        }

        private sealed class PassGlobals
        {
            internal readonly Dictionary<string, Vector4> Parameters = new(StringComparer.Ordinal);
            internal readonly Dictionary<string, bool> RuntimeSwitches = new(StringComparer.Ordinal);

            internal PassGlobals(GameMaterialPass pass)
            {
                foreach (GameMaterialParameter parameter in pass?.Parameters ?? Array.Empty<GameMaterialParameter>())
                {
                    if (!string.IsNullOrEmpty(parameter?.Name))
                        Parameters[parameter.Name] = parameter.Value;
                }
                foreach (KeyValuePair<string, bool> pair in pass?.RuntimeSwitches ?? Array.Empty<KeyValuePair<string, bool>>())
                {
                    if (!string.IsNullOrEmpty(pair.Key))
                        RuntimeSwitches[pair.Key] = pair.Value;
                }
            }
        }

        private sealed record PassRuntimeEntry(
            int PassIndex,
            ProgramRuntime Program,
            GameMaterialPass Pass,
            PassGlobals Globals);

        private sealed record CacheEntry(
            IReadOnlyList<PassRuntimeEntry> Passes,
            string Failure);

        private readonly GL _gl;
        private readonly bool _gles;
        private readonly AppSettings _settings;
        private readonly string _shaderCachePath;
        private readonly WadFile _shaderCache;
        private readonly Dictionary<object, CacheEntry> _programs =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, ProgramRuntime> _sharedPrograms =
            new(StringComparer.Ordinal);
        private readonly Dictionary<(MapTextureWrap U, MapTextureWrap V, bool Min, bool Mag, string Shared), uint> _samplers = new();
        private uint _neutralGrey2D;
        private uint _neutralBlack2D;
        private uint _neutralWhite2D;
        private uint _neutralBuffer2D;
        private readonly Dictionary<(GameShaderTranslator.TextureDimension Dimension, bool Black), uint> _neutralTypedTextures = new();
        private int _maxTextureUnits;
        private bool _disposed;

        internal GameShaderRuntime(GL gl, bool gles, AppSettings settings)
        {
            _gl = gl ?? throw new ArgumentNullException(nameof(gl));
            _gles = gles;
            _settings = settings;
            _shaderCachePath = GameShaderProgramResolver.FindShaderCachePath(settings);
            if (!string.IsNullOrWhiteSpace(_shaderCachePath))
            {
                try
                {
                    _shaderCache = new WadFile(_shaderCachePath);
                }
                catch
                {
                    _shaderCache = null;
                }
            }
        }

        internal int GetStaticPassCount(MapMaterialDefinition material)
        {
            if (_disposed || material?.Program == null || material.Program.Kind != GameMaterialKind.StaticMesh)
                return 0;
            CacheEntry entry = GetOrCreate(material, material.Program);
            return entry?.Passes?.Count ?? 0;
        }

        internal bool TryBind(
            MapMaterialDefinition material,
            MapGeometryMeshData mesh,
            bool meshDoubleSided,
            in Frame frame,
            Func<string, uint?> programTexture,
            Func<string, uint?> lightmapTexture) =>
            TryBind(material, 0, mesh, meshDoubleSided, in frame, programTexture, lightmapTexture);

        internal bool TryBind(
            MapMaterialDefinition material,
            int passIndex,
            MapGeometryMeshData mesh,
            bool meshDoubleSided,
            in Frame frame,
            Func<string, uint?> programTexture,
            Func<string, uint?> lightmapTexture)
        {
            if (_disposed || material?.Program == null || material.Program.Kind != GameMaterialKind.StaticMesh)
                return false;

            CacheEntry entry = GetOrCreate(material, material.Program);
            if (entry?.Passes == null || passIndex < 0 || passIndex >= entry.Passes.Count)
                return false;

            PassRuntimeEntry passEntry = entry.Passes[passIndex];
            ProgramRuntime runtime = passEntry.Program;
            if (runtime == null)
                return false;

            _gl.UseProgram(runtime.Program);
            ApplyGenericAttributeDefaults(runtime.Attributes, GameMaterialKind.StaticMesh, hasTangents: false);
            UpdateBlocks(runtime, passEntry.Globals, mesh, frame, null);
            BindTextures(runtime, passEntry.Pass, passIndex, material, mesh, programTexture, lightmapTexture);
            ApplyPassState(passEntry.Pass.State, meshDoubleSided);
            return true;
        }

        internal int GetSkinnedPassCount(ModelMaterialDefinition material)
        {
            if (_disposed || material?.Program == null || material.Program.Kind != GameMaterialKind.SkinnedMesh)
                return 0;
            CacheEntry entry = GetOrCreate(material, material.Program);
            return entry?.Passes?.Count ?? 0;
        }

        internal bool TryBindSkinned(
            ModelMaterialDefinition material,
            Matrix4x4 world,
            IReadOnlyList<Matrix4x4> bones,
            bool hasTangents,
            in Frame frame,
            Func<string, uint?> programTexture,
            float selfIllumination = 0f) =>
            TryBindSkinned(material, 0, world, bones, hasTangents, in frame, programTexture, selfIllumination);

        internal bool TryBindSkinned(
            ModelMaterialDefinition material,
            int passIndex,
            Matrix4x4 world,
            IReadOnlyList<Matrix4x4> bones,
            bool hasTangents,
            in Frame frame,
            Func<string, uint?> programTexture,
            float selfIllumination = 0f,
            int gearIndex = 0)
        {
            if (_disposed || material?.Program == null || material.Program.Kind != GameMaterialKind.SkinnedMesh)
                return false;

            CacheEntry entry = GetOrCreate(material, material.Program);
            if (entry?.Passes == null || passIndex < 0 || passIndex >= entry.Passes.Count)
                return false;

            PassRuntimeEntry passEntry = entry.Passes[passIndex];
            ProgramRuntime runtime = passEntry.Program;
            if (runtime == null)
                return false;

            _gl.UseProgram(runtime.Program);
            ApplyGenericAttributeDefaults(runtime.Attributes, GameMaterialKind.SkinnedMesh, hasTangents);
            UpdateBlocks(runtime, passEntry.Globals, null, frame, new CharacterDraw(world, bones, selfIllumination));
            BindSkinnedTextures(runtime, passEntry.Pass, programTexture, material, gearIndex);
            ApplyPassState(passEntry.Pass.State, material.RenderState.DoubleSided);
            return true;
        }

        internal string FailureFor(MapMaterialDefinition material) =>
            material != null && _programs.TryGetValue(material, out CacheEntry entry)
                ? entry.Failure
                : null;

        internal string FailureFor(ModelMaterialDefinition material) =>
            material != null && _programs.TryGetValue(material, out CacheEntry entry)
                ? entry.Failure
                : null;

        private CacheEntry GetOrCreate(object owner, GameMaterialProgram program)
        {
            if (owner != null && _programs.TryGetValue(owner, out CacheEntry cached))
                return cached;

            CacheEntry created;
            try
            {
                if (_shaderCache == null)
                {
                    created = new CacheEntry(
                        Array.Empty<PassRuntimeEntry>(),
                        string.IsNullOrWhiteSpace(_shaderCachePath)
                            ? "ShaderCache.dx11.wad.client was not found in the configured game installs."
                            : "ShaderCache.dx11.wad.client could not be opened.");
                }
                else
                {
                    GameShaderProgramResolver.ShaderBytecodeMaterialProgram bytecodes =
                        GameShaderProgramResolver.ReadProgram(
                            program,
                            _shaderCache,
                            _shaderCachePath);
                    if (bytecodes == null)
                    {
                        created = new CacheEntry(Array.Empty<PassRuntimeEntry>(), "Material has no resolved game program.");
                    }
                    else
                    {
                        var readyPasses = new List<PassRuntimeEntry>();
                        var failures = new List<string>();
                        for (int passIndex = 0; passIndex < bytecodes.Passes.Count; passIndex++)
                        {
                            GameShaderProgramResolver.ShaderBytecodePassRead passRead = bytecodes.Passes[passIndex];
                            if (passRead?.Bytecode?.Ready != true)
                            {
                                if (!string.IsNullOrWhiteSpace(passRead?.Bytecode?.Failure))
                                    failures.Add(passRead.Bytecode.Failure);
                                continue;
                            }

                            string key = ProgramKey(passRead.Pass, passRead.Bytecode.Program);
                            if (!_sharedPrograms.TryGetValue(key, out ProgramRuntime ready))
                            {
                                GameShaderTranslator.TranslationRead translated =
                                    GameShaderTranslator.Translate(
                                        passRead.Bytecode.Program.Vertex,
                                        passRead.Bytecode.Program.VertexReflection,
                                        passRead.Bytecode.Program.Pixel,
                                        passRead.Bytecode.Program.PixelReflection);
                                if (!translated.Ready)
                                {
                                    if (!string.IsNullOrWhiteSpace(translated.Failure))
                                        failures.Add(translated.Failure);
                                    continue;
                                }

                                try
                                {
                                    ready = CreateProgram(translated.Program, program.Kind);
                                    _sharedPrograms[key] = ready;
                                }
                                catch (Exception ex)
                                {
                                    ready = null;
                                    failures.Add(ex.Message);
                                    continue;
                                }
                            }

                            if (ready != null)
                            {
                                readyPasses.Add(new PassRuntimeEntry(
                                    passIndex,
                                    ready,
                                    passRead.Pass,
                                    new PassGlobals(passRead.Pass)));
                            }
                        }

                        created = readyPasses.Count > 0
                            ? new CacheEntry(readyPasses, null)
                            : new CacheEntry(Array.Empty<PassRuntimeEntry>(), failures.FirstOrDefault() ?? "No material pass translated and linked.");
                    }
                }
            }
            catch (Exception ex)
            {
                created = new CacheEntry(Array.Empty<PassRuntimeEntry>(), ex.Message);
            }

            if (owner != null)
                _programs[owner] = created;
            return created;
        }

        private ProgramRuntime CreateProgram(
            GameShaderTranslator.TranslatedProgram translated,
            GameMaterialKind kind)
        {
            IReadOnlyDictionary<uint, string> attributes = AttributeLocations(translated.Vertex.Sidecar.Attributes, kind);
            string vertex = SourceForProfile(translated.Vertex.Glsl, vertexStage: true);
            string pixel = SourceForProfile(translated.Pixel.Glsl, vertexStage: false);
            uint program = GlShaderCompiler.CreateRawProgram(_gl, vertex, pixel, attributes);

            try
            {
                var blocks = new List<BlockRuntime>();
                uint binding = 0;
                foreach (GameShaderTranslator.UniformBlock block in translated.Vertex.Sidecar.Blocks
                             .Concat(translated.Pixel.Sidecar.Blocks))
                {
                    uint index = _gl.GetUniformBlockIndex(program, block.GlslName);
                    if (index == uint.MaxValue)
                        continue;

                    _gl.UniformBlockBinding(program, index, binding);
                    uint buffer = _gl.GenBuffer();
                    float[] data = new float[checked((int)(block.Size / sizeof(float)))];
                    _gl.BindBuffer(BufferTargetARB.UniformBuffer, buffer);
                    _gl.BufferData(
                        BufferTargetARB.UniformBuffer,
                        new ReadOnlySpan<float>(data),
                        BufferUsageARB.DynamicDraw);
                    _gl.BindBufferBase(BufferTargetARB.UniformBuffer, binding, buffer);
                    blocks.Add(new BlockRuntime
                    {
                        Block = block,
                        Buffer = buffer,
                        Binding = binding,
                        Data = data
                    });
                    binding++;
                }
                _gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);

                var samplers = new List<SamplerRuntime>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                _gl.UseProgram(program);
                foreach (GameShaderTranslator.TextureBinding texture in translated.Vertex.Sidecar.Textures
                             .Concat(translated.Pixel.Sidecar.Textures))
                {
                    foreach (GameShaderTranslator.SamplerBinding sampler in texture.Samplers)
                    {
                        if (!seen.Add(sampler.GlslName))
                            continue;
                        int location = _gl.GetUniformLocation(program, sampler.GlslName);
                        if (location < 0)
                            continue;
                        uint unit = checked((uint)samplers.Count);
                        _gl.Uniform1(location, checked((int)unit));
                        samplers.Add(new SamplerRuntime(
                            texture.Name,
                            texture.Dimension,
                            sampler.Sampler,
                            location,
                            unit));
                        _maxTextureUnits = Math.Max(_maxTextureUnits, checked((int)unit + 1));
                    }
                }
                _gl.UseProgram(0);
                return new ProgramRuntime(_gl, program, blocks, samplers, attributes);
            }
            catch
            {
                _gl.DeleteProgram(program);
                throw;
            }
        }

        private string SourceForProfile(string source, bool vertexStage)
        {
            string result = source ?? string.Empty;
            if (vertexStage)
            {
                // SPIRV-Cross may preserve Vulkan interface locations. The renderer owns stable
                // engine semantics, so remove only vertex-input locations and let
                // glBindAttribLocation apply the translated name-to-stream mapping.
                result = Regex.Replace(
                    result,
                    @"(?m)^\s*layout\(location\s*=\s*\d+\)\s+(?=in\s)",
                    string.Empty,
                    RegexOptions.CultureInvariant);
            }
            if (_gles)
                return result;

            result = Regex.Replace(
                result,
                @"^#version\s+300\s+es\s*$",
                "#version 330 core",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            result = Regex.Replace(
                result,
                @"^\s*precision\s+(?:lowp|mediump|highp)\s+\w+\s*;\s*$\r?\n?",
                string.Empty,
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            result = Regex.Replace(
                result,
                @"\b(?:lowp|mediump|highp)\s+",
                string.Empty,
                RegexOptions.CultureInvariant);
            return result;
        }

        private static string ProgramKey(
            GameMaterialPass pass,
            GameShaderProgramResolver.ShaderBytecodeProgram bytecode)
        {
            string shader = pass?.ShaderPath ?? string.Empty;
            string defines = string.Join(
                ";",
                (bytecode?.Defines ?? Array.Empty<GameMaterialDefine>())
                    .Select(define => define.Name + "=" + define.Value));
            return shader + "|" + defines;
        }

        private static IReadOnlyDictionary<uint, string> AttributeLocations(
            IReadOnlyList<GameShaderTranslator.AttributeBinding> attributes,
            GameMaterialKind kind)
        {
            var result = new Dictionary<uint, string>();
            uint next = kind == GameMaterialKind.SkinnedMesh ? 7u : 7u;
            foreach (GameShaderTranslator.AttributeBinding attribute in attributes ?? Array.Empty<GameShaderTranslator.AttributeBinding>())
            {
                string semantic = attribute.Semantic.ToUpperInvariant();
                uint location = kind == GameMaterialKind.SkinnedMesh
                    ? (semantic, attribute.Index) switch
                    {
                        ("POSITION", _) => 0,
                        ("NORMAL", _) => 1,
                        ("TEXCOORD", 0) => 2,
                        ("TANGENT", _) => 3,
                        ("COLOR", _) => 4,
                        ("BLENDINDICES", _) => 5,
                        ("BLENDWEIGHT", _) => 6,
                        _ => next++
                    }
                    : (semantic, attribute.Index) switch
                    {
                        ("POSITION", _) => 0,
                        ("NORMAL", _) => 1,
                        ("TEXCOORD", 0) => 2,
                        ("TEXCOORD", 7) => 3,
                        ("COLOR", _) => 4,
                        ("TEXCOORD", 5) => 5,
                        ("TEXCOORD", 6) => 6,
                        _ => next++
                    };
                result[location] = attribute.GlslName;
            }
            return result;
        }

        private void ApplyGenericAttributeDefaults(
            IReadOnlyDictionary<uint, string> attributes,
            GameMaterialKind kind,
            bool hasTangents)
        {
            foreach ((uint location, string name) in attributes)
            {
                bool provided = kind == GameMaterialKind.SkinnedMesh
                    ? location is 0 or 1 or 2 or 5 or 6 || (location == 3 && hasTangents)
                    : location <= 3;
                if (provided)
                    continue;

                _gl.DisableVertexAttribArray(location);
                if (name.Contains("COLOR", StringComparison.OrdinalIgnoreCase))
                    _gl.VertexAttrib4(location, 1f, 1f, 1f, 1f);
                else if (name.Contains("TEXCOORD6", StringComparison.OrdinalIgnoreCase))
                    _gl.VertexAttrib4(location, 0f, 0f, 0f, 0f);
                else
                    _gl.VertexAttrib4(location, 0f, 0f, 0f, 1f);
            }
        }

        private void UpdateBlocks(
            ProgramRuntime runtime,
            PassGlobals globals,
            MapGeometryMeshData mesh,
            in Frame frame,
            CharacterDraw? character)
        {
            bool skinned = character.HasValue;
            foreach (BlockRuntime block in runtime.Blocks)
            {
                Array.Clear(block.Data, 0, block.Data.Length);
                switch (block.Block.Name)
                {
                    case Globals:
                        WriteGlobals(block.Data, block.Block, globals, mesh);
                        break;
                    case "PerFrameVertexCB":
                        WritePerFrameVertex(block.Data, frame, skinned);
                        break;
                    case "PerFramePixelCB":
                        WritePerFramePixel(block.Data, frame, skinned);
                        break;
                    case "CharacterPerDrawVertexCB" when character.HasValue:
                        WriteCharacterPerDrawVertex(block.Data, frame);
                        break;
                    case "CharacterPerDrawPS" when character.HasValue:
                        WriteCharacterPerDrawPixel(block.Data, character.Value);
                        break;
                    case "BonesCB" when character.HasValue:
                        WriteBones(block.Data, character.Value);
                        break;
                }

                _gl.BindBuffer(BufferTargetARB.UniformBuffer, block.Buffer);
                _gl.BufferSubData(BufferTargetARB.UniformBuffer, 0, new ReadOnlySpan<float>(block.Data));
                _gl.BindBufferBase(BufferTargetARB.UniformBuffer, block.Binding, block.Buffer);
            }
            _gl.BindBuffer(BufferTargetARB.UniformBuffer, 0);
        }

        private static void WriteGlobals(
            float[] data,
            GameShaderTranslator.UniformBlock block,
            PassGlobals globals,
            MapGeometryMeshData mesh)
        {
            IReadOnlyDictionary<string, Vector4> parameters = globals?.Parameters;
            IReadOnlyDictionary<string, bool> switches = globals?.RuntimeSwitches;

            foreach (GameShaderTranslator.BlockMember member in block.Members)
            {
                int at = checked((int)(member.Offset / sizeof(float)));
                int count = checked((int)(member.Size / sizeof(float)));
                if (member.Name is "WORLD_MATRIX" or "WORLD_MATRIX_INV")
                {
                    WriteIdentityRows(data, at, count);
                    continue;
                }

                if (member.Name == BakedLightTransform)
                {
                    WriteLightTransform(data, at, mesh?.BakedLight);
                    continue;
                }
                if (member.Name == StationaryLightTransform)
                {
                    WriteLightTransform(data, at, mesh?.StationaryLight);
                    continue;
                }

                if (parameters != null && parameters.TryGetValue(member.Name, out Vector4 value))
                {
                    WriteVector4(data, at, count, value);
                    continue;
                }

                if (member.Name.StartsWith("switch_", StringComparison.Ordinal) &&
                    switches != null && switches.TryGetValue(member.Name[7..], out bool enabled) && at < data.Length)
                {
                    data[at] = enabled ? 1f : 0f;
                }
            }
        }

        private static void WriteLightTransform(float[] data, int at, MapGeometryLightChannelData channel)
        {
            Set(data, at, channel?.Scale.X ?? 1f);
            Set(data, at + 1, channel?.Scale.Y ?? 1f);
            Set(data, at + 2, channel?.Bias.X ?? 0f);
            Set(data, at + 3, channel?.Bias.Y ?? 0f);
        }

        private static void WritePerFrameVertex(float[] data, in Frame frame, bool skinned)
        {
            Matrix4x4 mirror = Matrix4x4.CreateScale(-1f, 1f, 1f);
            Matrix4x4 clip = skinned
                ? frame.View * frame.Projection
                : mirror * frame.View * frame.Projection;
            Matrix4x4.Invert(frame.View, out Matrix4x4 cameraWorld);
            Vector3 eye = skinned
                ? frame.Eye
                : new Vector3(-frame.Eye.X, frame.Eye.Y, frame.Eye.Z);
            Vector3 direction = ResolveSunDirection(frame.Sun, skinned);

            WriteClipRows(data, 0, clip);
            WriteVector3(data, 16, eye);
            Set(data, 20, frame.TimeSeconds);
            WriteClipRows(data, 28, clip);
            WriteMatrixRows(data, 96, frame.View);
            WriteMatrixRows(data, 112, cameraWorld);
            WriteVector3(data, 132, direction);
        }

        private static void WritePerFramePixel(float[] data, in Frame frame, bool skinned)
        {
            ResolveSun(frame.Sun, out Vector3 color, out float intensity, out Vector3 sky, out float skyScale,
                out Vector3 direction, out float lightMapScale, out bool fogEnabled, out Vector3 fog,
                out Vector3 fogAlternate, out Vector2 fogStartEnd, out float fogEmissive);
            Matrix4x4.Invert(frame.View, out Matrix4x4 cameraWorld);
            Vector3 eye = skinned
                ? frame.Eye
                : new Vector3(-frame.Eye.X, frame.Eye.Y, frame.Eye.Z);
            if (skinned)
                direction.X = -direction.X;
            Vector3 sun = color * intensity;
            Vector3 shadow = sky * skyScale;
            Vector3 complement = Vector3.Max(sun - shadow, Vector3.Zero);

            WriteVector3(data, 0, eye);
            Set(data, 4, frame.TimeSeconds);
            WriteVector3(data, 12, shadow);
            Set(data, 15, 1f);
            WriteVector3(data, 16, complement);
            Set(data, 19, 1f);
            WriteVector3(data, 24, sun);
            Set(data, 27, 1f);
            WriteVector3(data, 29, direction);
            Set(data, 32, lightMapScale);
            if (fogEnabled)
            {
                WriteVector3(data, 33, fog);
                WriteVector3(data, 36, fogAlternate);
                Set(data, 40, fogStartEnd.X);
                Set(data, 41, fogStartEnd.Y);
                Set(data, 42, 1f);
                Set(data, 43, fogEmissive);
            }
            else
            {
                WriteVector3(data, 33, sky);
                WriteVector3(data, 36, sky);
                Set(data, 40, FogStart);
                Set(data, 41, FogEnd);
            }
            WriteMatrixRows(data, 68, frame.View);
            WriteMatrixRows(data, 104, cameraWorld);
        }

        internal static void WriteCharacterPerDrawVertex(float[] data, in Frame frame)
        {
            WriteIdentityRows(data, 0, 16);
            ResolveSun(
                frame.Sun,
                out Vector3 color,
                out float intensity,
                out Vector3 sky,
                out float skyScale,
                out Vector3 direction,
                out _,
                out _,
                out _,
                out _,
                out _,
                out _);
            direction.X = -direction.X;
            Vector3 ground = frame.Sun == null
                ? sky
                : new Vector3(frame.Sun.GroundColor.X, frame.Sun.GroundColor.Y, frame.Sun.GroundColor.Z);
            Vector3 horizon = frame.Sun == null
                ? sky
                : new Vector3(frame.Sun.HorizonColor.X, frame.Sun.HorizonColor.Y, frame.Sun.HorizonColor.Z);
            Vector3 sun = color * intensity;
            Vector3[] faces =
            {
                Vector3.UnitX,
                -Vector3.UnitX,
                Vector3.UnitY,
                -Vector3.UnitY,
                Vector3.UnitZ,
                -Vector3.UnitZ
            };

            for (int face = 0; face < faces.Length; face++)
            {
                Vector3 axis = faces[face];
                Vector3 basis = axis.Y > 0f ? sky : axis.Y < 0f ? ground : horizon;
                Vector3 shaded = basis * skyScale;
                float facing = MathF.Max(Vector3.Dot(axis, direction), 0f);
                Vector3 lit = shaded + sun * facing;
                int at = 16 + face * 4;
                WriteVector3(data, at, lit);
                Set(data, at + 3, 1f);
            }
            WriteIdentityRows(data, 44, 16);
        }

        private static void WriteCharacterPerDrawPixel(float[] data, in CharacterDraw character)
        {
            Set(data, 0, character.SelfIllumination);
            Set(data, 1, character.SelfIllumination);
            Set(data, 2, character.SelfIllumination);
            Set(data, 7, 1f);
            Set(data, 8, 1f);
            Set(data, 9, 1f);
            WriteIdentityRows(data, 16, 16);
            WriteIdentityRows(data, 32, 16);
        }

        private static void WriteBones(float[] data, in CharacterDraw character)
        {
            IReadOnlyList<Matrix4x4> bones = character.Bones;
            if (bones == null || bones.Count == 0)
                return;

            int count = Math.Min(256, Math.Min(bones.Count, data.Length / 12));
            for (int bone = 0; bone < count; bone++)
            {
                Matrix4x4 matrix = bones[bone] * character.World;
                int at = bone * 12;
                Set(data, at + 0, matrix.M11); Set(data, at + 1, matrix.M21); Set(data, at + 2, matrix.M31); Set(data, at + 3, matrix.M41);
                Set(data, at + 4, matrix.M12); Set(data, at + 5, matrix.M22); Set(data, at + 6, matrix.M32); Set(data, at + 7, matrix.M42);
                Set(data, at + 8, matrix.M13); Set(data, at + 9, matrix.M23); Set(data, at + 10, matrix.M33); Set(data, at + 11, matrix.M43);
            }
        }

        private void BindSkinnedTextures(
            ProgramRuntime runtime,
            GameMaterialPass pass,
            Func<string, uint?> programTexture,
            ModelMaterialDefinition material,
            int gearIndex)
        {
            foreach (SamplerRuntime sampler in runtime.Samplers)
            {
                string name = sampler.TextureName;
                uint texture;
                TextureTarget target;
                uint samplerObject;

                if (name.EndsWith(SharedTextureSuffix, StringComparison.Ordinal))
                {
                    (texture, target) = NeutralFor(sampler.Dimension, black: true);
                    samplerObject = ResolveNeutralSampler(clamp: true, sampler.Dimension);
                }
                else
                {
                    string own = name.EndsWith(MaterialTextureSuffix, StringComparison.Ordinal)
                        ? name[..^MaterialTextureSuffix.Length]
                        : name;
                    GameMaterialTexture declared = pass.Textures?
                        .FirstOrDefault(item => string.Equals(item.Name, own, StringComparison.Ordinal));
                    string authoredPath = material.ResolveTextureSwap(own, gearIndex) ?? declared?.Texture?.VirtualPath;
                    if (string.IsNullOrWhiteSpace(authoredPath) && declared?.Texture?.PathHash > 0)
                        authoredPath = declared.Texture.PathHash.ToString("x16");
                    uint? loaded = sampler.Dimension == GameShaderTranslator.TextureDimension.Texture2D &&
                                   !string.IsNullOrWhiteSpace(authoredPath)
                        ? programTexture?.Invoke(authoredPath)
                        : null;
                    if (loaded.HasValue && loaded.Value != 0)
                    {
                        texture = loaded.Value;
                        target = TextureTarget.Texture2D;
                        samplerObject = declared != null
                            ? ResolveSampler(declared.Sampler)
                            : ResolveNeutralSampler(clamp: false);
                    }
                    else
                    {
                        bool isBlackDefault = string.Equals(own, "EMISSIVE_MAP", StringComparison.OrdinalIgnoreCase) ||
                                              own.IndexOf("emissive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              own.IndexOf("glow", StringComparison.OrdinalIgnoreCase) >= 0;
                        (texture, target) = NeutralFor(sampler.Dimension, black: isBlackDefault);
                        samplerObject = ResolveNeutralSampler(clamp: true, sampler.Dimension);
                    }
                }

                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + sampler.Unit));
                _gl.BindTexture(target, texture);
                _gl.BindSampler(sampler.Unit, samplerObject);
            }
            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        private void BindTextures(
            ProgramRuntime runtime,
            GameMaterialPass pass,
            int passIndex,
            MapMaterialDefinition material,
            MapGeometryMeshData mesh,
            Func<string, uint?> programTexture,
            Func<string, uint?> lightmapTexture)
        {
            foreach (SamplerRuntime sampler in runtime.Samplers)
            {
                string name = sampler.TextureName;
                uint texture;
                TextureTarget target;
                uint samplerObject;

                if (name == BakedLightTexture)
                {
                    uint? loaded = ResolveLightmap(mesh?.BakedLight, lightmapTexture);
                    texture = loaded ?? NeutralWhite2D();
                    target = TextureTarget.Texture2D;
                    samplerObject = loaded.HasValue ? ResolveLightmapSampler() : ResolveNeutralSampler(clamp: true);
                }
                else if (name == StationaryLightTexture)
                {
                    uint? loaded = ResolveLightmap(mesh?.StationaryLight, lightmapTexture);
                    texture = loaded ?? NeutralWhite2D();
                    target = TextureTarget.Texture2D;
                    samplerObject = loaded.HasValue ? ResolveLightmapSampler() : ResolveNeutralSampler(clamp: true);
                }
                else if (name.EndsWith(SharedTextureSuffix, StringComparison.Ordinal))
                {
                    (texture, target) = NeutralFor(sampler.Dimension, black: true);
                    samplerObject = ResolveNeutralSampler(clamp: true, sampler.Dimension);
                }
                else
                {
                    string own = name.EndsWith(MaterialTextureSuffix, StringComparison.Ordinal)
                        ? name[..^MaterialTextureSuffix.Length]
                        : name;
                    GameMaterialTexture declared = pass.Textures?
                        .FirstOrDefault(item => string.Equals(item.Name, own, StringComparison.Ordinal));
                    uint? loaded = sampler.Dimension == GameShaderTranslator.TextureDimension.Texture2D
                        ? (programTexture?.Invoke(MapTextureLoadingService.ProgramTextureKey(material.Name, passIndex, own))
                           ?? programTexture?.Invoke(MapTextureLoadingService.ProgramTextureKey(material.Name, own)))
                        : null;
                    if (loaded.HasValue && loaded.Value != 0)
                    {
                        texture = loaded.Value;
                        target = TextureTarget.Texture2D;
                        samplerObject = declared != null ? ResolveSampler(declared.Sampler) : ResolveNeutralSampler(clamp: false);
                    }
                    else
                    {
                        (texture, target) = NeutralFor(sampler.Dimension, black: false);
                        samplerObject = ResolveNeutralSampler(clamp: true, sampler.Dimension);
                    }
                }

                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + sampler.Unit));
                _gl.BindTexture(target, texture);
                _gl.BindSampler(sampler.Unit, samplerObject);
            }
            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        private static uint? ResolveLightmap(
            MapGeometryLightChannelData channel,
            Func<string, uint?> lookup) =>
            channel?.IsEmpty == false ? lookup?.Invoke(channel.Texture) : null;

        private uint ResolveSampler(GameMaterialSamplerState state)
        {
            if (state == null)
                return ResolveNeutralSampler(clamp: false);

            MapTextureWrap u = state.WrapU;
            MapTextureWrap v = state.WrapV;
            bool min = state.FilterMin;
            bool mag = state.FilterMag;
            string shared = state.SharedSampler ?? string.Empty;
            if (shared.Contains("Clamp", StringComparison.Ordinal))
                u = v = MapTextureWrap.Clamp;
            else if (shared.Contains("Wrap", StringComparison.Ordinal))
                u = v = MapTextureWrap.Repeat;
            if (shared.Contains("No_Mip", StringComparison.Ordinal))
                min = true;

            var key = (u, v, min, mag, shared);
            if (_samplers.TryGetValue(key, out uint cached))
                return cached;

            uint sampler = _gl.GenSampler();
            _gl.SamplerParameter(
                sampler,
                SamplerParameterI.MinFilter,
                shared.Contains("No_Mip", StringComparison.Ordinal)
                    ? (int)TextureMinFilter.Linear
                    : min ? (int)TextureMinFilter.LinearMipmapLinear : (int)TextureMinFilter.NearestMipmapNearest);
            _gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, mag ? (int)TextureMagFilter.Linear : (int)TextureMagFilter.Nearest);
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)ToWrap(u));
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)ToWrap(v));
            _samplers[key] = sampler;
            return sampler;
        }

        private uint ResolveLightmapSampler()
        {
            var key = (MapTextureWrap.Clamp, MapTextureWrap.Clamp, true, true, "lightmap-mips");
            if (_samplers.TryGetValue(key, out uint sampler))
                return sampler;
            sampler = _gl.GenSampler();
            _gl.SamplerParameter(sampler, SamplerParameterI.MinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Linear);
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)TextureWrapMode.ClampToEdge);
            _samplers[key] = sampler;
            return sampler;
        }

        private uint ResolveNeutralSampler(
            bool clamp,
            GameShaderTranslator.TextureDimension dimension = GameShaderTranslator.TextureDimension.Texture2D)
        {
            bool integerBuffer = RequiresIntegerNeutral(dimension);
            var key = (
                clamp ? MapTextureWrap.Clamp : MapTextureWrap.Repeat,
                clamp ? MapTextureWrap.Clamp : MapTextureWrap.Repeat,
                !integerBuffer,
                !integerBuffer,
                integerBuffer ? "neutral-buffer" : "neutral-no-mip");
            if (_samplers.TryGetValue(key, out uint sampler))
                return sampler;
            sampler = _gl.GenSampler();
            _gl.SamplerParameter(
                sampler,
                SamplerParameterI.MinFilter,
                (int)(integerBuffer ? TextureMinFilter.Nearest : TextureMinFilter.Linear));
            _gl.SamplerParameter(
                sampler,
                SamplerParameterI.MagFilter,
                (int)(integerBuffer ? TextureMagFilter.Nearest : TextureMagFilter.Linear));
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)(clamp ? TextureWrapMode.ClampToEdge : TextureWrapMode.Repeat));
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)(clamp ? TextureWrapMode.ClampToEdge : TextureWrapMode.Repeat));
            _samplers[key] = sampler;
            return sampler;
        }

        internal static bool RequiresIntegerNeutral(GameShaderTranslator.TextureDimension dimension) =>
            dimension == GameShaderTranslator.TextureDimension.Buffer;

        private (uint Texture, TextureTarget Target) NeutralFor(
            GameShaderTranslator.TextureDimension dimension,
            bool black)
        {
            return dimension switch
            {
                GameShaderTranslator.TextureDimension.Texture2DArray or
                GameShaderTranslator.TextureDimension.CubeArray =>
                    (NeutralTyped(dimension, black), TextureTarget.Texture2DArray),
                GameShaderTranslator.TextureDimension.Texture3D =>
                    (NeutralTyped(dimension, black), TextureTarget.Texture3D),
                GameShaderTranslator.TextureDimension.Cube =>
                    (NeutralTyped(dimension, black), TextureTarget.TextureCubeMap),
                // Hexshade lowers texel buffers to integer sampler2D data textures. Their neutral
                // must therefore be R32UI/nearest rather than an RGBA colour texture.
                GameShaderTranslator.TextureDimension.Buffer =>
                    (NeutralBuffer2D(), TextureTarget.Texture2D),
                // Unknown dimensions follow the neutral fallback rather than binding a real asset to a wrong target.
                _ => (black ? NeutralBlack2D() : NeutralGrey2D(), TextureTarget.Texture2D)
            };
        }

        private uint NeutralTyped(GameShaderTranslator.TextureDimension dimension, bool black)
        {
            var key = (dimension, black);
            if (_neutralTypedTextures.TryGetValue(key, out uint cached))
                return cached;

            byte value = black ? (byte)0 : (byte)128;
            uint texture = dimension switch
            {
                GameShaderTranslator.TextureDimension.Texture2DArray => CreateNeutralArray(value, value, value, black ? (byte)0 : (byte)255, 1),
                GameShaderTranslator.TextureDimension.CubeArray => CreateNeutralArray(value, value, value, black ? (byte)0 : (byte)255, 6),
                GameShaderTranslator.TextureDimension.Texture3D => CreateNeutral3D(value, value, value, black ? (byte)0 : (byte)255),
                GameShaderTranslator.TextureDimension.Cube => CreateNeutralCube(value, value, value, black ? (byte)0 : (byte)255),
                _ => black ? NeutralBlack2D() : NeutralGrey2D()
            };
            _neutralTypedTextures[key] = texture;
            return texture;
        }

        private uint NeutralGrey2D() => _neutralGrey2D != 0 ? _neutralGrey2D : (_neutralGrey2D = CreateNeutral(128, 128, 128, 255));
        private uint NeutralBlack2D() => _neutralBlack2D != 0 ? _neutralBlack2D : (_neutralBlack2D = CreateNeutral(0, 0, 0, 0));
        private uint NeutralWhite2D() => _neutralWhite2D != 0 ? _neutralWhite2D : (_neutralWhite2D = CreateNeutral(255, 255, 255, 255));

        private uint NeutralBuffer2D()
        {
            if (_neutralBuffer2D != 0)
                return _neutralBuffer2D;

            _neutralBuffer2D = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _neutralBuffer2D);
            uint[] zero = { 0u };
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.R32ui,
                1,
                1,
                0,
                PixelFormat.RedInteger,
                PixelType.UnsignedInt,
                new ReadOnlySpan<uint>(zero));
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return _neutralBuffer2D;
        }

        private uint CreateNeutral(byte r, byte g, byte b, byte a)
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            byte[] pixel = { r, g, b, a };
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                1,
                1,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(pixel));
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }

        private uint CreateNeutralArray(byte r, byte g, byte b, byte a, int layers)
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2DArray, texture);
            byte[] pixels = new byte[Math.Max(1, layers) * 4];
            for (int layer = 0; layer < Math.Max(1, layers); layer++)
            {
                int at = layer * 4;
                pixels[at] = r;
                pixels[at + 1] = g;
                pixels[at + 2] = b;
                pixels[at + 3] = a;
            }
            _gl.TexImage3D(
                TextureTarget.Texture2DArray,
                0,
                InternalFormat.Rgba8,
                1,
                1,
                (uint)Math.Max(1, layers),
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(pixels));
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.BindTexture(TextureTarget.Texture2DArray, 0);
            return texture;
        }

        private uint CreateNeutral3D(byte r, byte g, byte b, byte a)
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture3D, texture);
            byte[] pixel = { r, g, b, a };
            _gl.TexImage3D(
                TextureTarget.Texture3D,
                0,
                InternalFormat.Rgba8,
                1,
                1,
                1,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(pixel));
            _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.BindTexture(TextureTarget.Texture3D, 0);
            return texture;
        }

        private uint CreateNeutralCube(byte r, byte g, byte b, byte a)
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.TextureCubeMap, texture);
            byte[] pixel = { r, g, b, a };
            TextureTarget[] faces =
            {
                TextureTarget.TextureCubeMapPositiveX,
                TextureTarget.TextureCubeMapNegativeX,
                TextureTarget.TextureCubeMapPositiveY,
                TextureTarget.TextureCubeMapNegativeY,
                TextureTarget.TextureCubeMapPositiveZ,
                TextureTarget.TextureCubeMapNegativeZ
            };
            foreach (TextureTarget face in faces)
            {
                _gl.TexImage2D(
                    face,
                    0,
                    InternalFormat.Rgba8,
                    1,
                    1,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    new ReadOnlySpan<byte>(pixel));
            }
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.BindTexture(TextureTarget.TextureCubeMap, 0);
            return texture;
        }

        internal void ResetBindings()
        {
            // A translated pass owns color/depth comparison state. Restore the stock-preview
            // defaults before another material or renderer takes over, just as the reference
            // renderer reapplies material state on every draw.
            _gl.ColorMask(true, true, true, true);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.DepthMask(true);
            _gl.Enable(EnableCap.DepthTest);
            _gl.Disable(EnableCap.Blend);
            _gl.Disable(EnableCap.CullFace);
            for (uint unit = 0; unit < (uint)_maxTextureUnits; unit++)
            {
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                _gl.BindSampler(unit, 0);
                _gl.BindTexture(TextureTarget.Texture2D, 0);
                _gl.BindTexture(TextureTarget.Texture2DArray, 0);
                _gl.BindTexture(TextureTarget.Texture3D, 0);
                _gl.BindTexture(TextureTarget.TextureCubeMap, 0);
            }
            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        private void ApplyPassState(GameMaterialPassState state, bool meshDoubleSided)
        {
            if (state.BlendEnabled)
            {
                _gl.Enable(EnableCap.Blend);
                _gl.BlendEquation(GLEnum.FuncAdd);
                _gl.BlendFuncSeparate(
                    ToBlend(state.SourceColor),
                    ToBlend(state.DestinationColor),
                    ToBlend(state.SourceAlpha),
                    ToBlend(state.DestinationAlpha));
            }
            else
            {
                _gl.Disable(EnableCap.Blend);
            }

            if (state.DepthEnabled) _gl.Enable(EnableCap.DepthTest);
            else _gl.Disable(EnableCap.DepthTest);
            _gl.DepthFunc(ToDepth(state.DepthCompareFunc));
            _gl.DepthMask((state.WriteMask & WriteDepth) != 0);
            bool colorWrite = ColorWriteEnabled(state.WriteMask);
            _gl.ColorMask(colorWrite, colorWrite, colorWrite, colorWrite);

            if (meshDoubleSided || !state.CullEnabled)
            {
                _gl.Disable(EnableCap.CullFace);
            }
            else
            {
                _gl.Enable(EnableCap.CullFace);
                _gl.CullFace(state.WindingToCull == GameMaterialWinding.CounterClockwise
                    ? TriangleFace.Back
                    : TriangleFace.Front);
            }
        }

        internal static bool ColorWriteEnabled(uint writeMask) => (writeMask & 15u) != 0;

        private static BlendingFactor ToBlend(MapBlendFactor factor) =>
            factor switch
            {
                MapBlendFactor.Zero => BlendingFactor.Zero,
                MapBlendFactor.One => BlendingFactor.One,
                MapBlendFactor.SourceColor => BlendingFactor.SrcColor,
                MapBlendFactor.OneMinusSourceColor => BlendingFactor.OneMinusSrcColor,
                MapBlendFactor.DestinationColor => BlendingFactor.DstColor,
                MapBlendFactor.OneMinusDestinationColor => BlendingFactor.OneMinusDstColor,
                MapBlendFactor.SourceAlpha => BlendingFactor.SrcAlpha,
                MapBlendFactor.OneMinusSourceAlpha => BlendingFactor.OneMinusSrcAlpha,
                _ => BlendingFactor.One
            };

        internal static DepthFunction ToDepth(uint value) =>
            value switch
            {
                // 0 reads as class default (Lequal) rather than Never: VFX materials
                // that write it (such as HKG_Eyes_Blink_Mat) draw in the game.
                0 => DepthFunction.Lequal,
                1 => DepthFunction.Less,
                2 => DepthFunction.Equal,
                3 => DepthFunction.Lequal,
                4 => DepthFunction.Greater,
                5 => DepthFunction.Notequal,
                6 => DepthFunction.Gequal,
                7 => DepthFunction.Always,
                _ => DepthFunction.Lequal
            };

        private static TextureWrapMode ToWrap(MapTextureWrap wrap) =>
            wrap switch
            {
                MapTextureWrap.Clamp => TextureWrapMode.ClampToEdge,
                MapTextureWrap.Mirror => TextureWrapMode.MirroredRepeat,
                MapTextureWrap.Border => TextureWrapMode.ClampToEdge,
                _ => TextureWrapMode.Repeat
            };

        private static void ResolveSun(
            MapSunData sun,
            out Vector3 color,
            out float intensity,
            out Vector3 sky,
            out float skyScale,
            out Vector3 direction,
            out float lightMapScale,
            out bool fogEnabled,
            out Vector3 fog,
            out Vector3 fogAlternate,
            out Vector2 fogStartEnd,
            out float fogEmissive)
        {
            if (sun == null)
            {
                color = Vector3.One;
                intensity = 0.8f;
                sky = Vector3.One;
                skyScale = 1.2f;
                direction = Vector3.Normalize(new Vector3(-0.25f, 0.75f, -0.05f));
                lightMapScale = 1f;
                fogEnabled = false;
                fog = sky;
                fogAlternate = sky;
                fogStartEnd = new Vector2(FogStart, FogEnd);
                fogEmissive = 0f;
                return;
            }

            color = new Vector3(sun.Color.X, sun.Color.Y, sun.Color.Z);
            intensity = MathF.Max(sun.Intensity, 0f);
            sky = new Vector3(sun.SkyColor.X, sun.SkyColor.Y, sun.SkyColor.Z);
            skyScale = MathF.Max(sun.SkyScale, 0f);
            direction = ResolveSunDirection(sun);
            lightMapScale = MathF.Max(sun.LightMapColorScale, 0f);
            fogEnabled = sun.FogEnabled;
            fog = new Vector3(sun.FogColor.X, sun.FogColor.Y, sun.FogColor.Z);
            fogAlternate = new Vector3(sun.FogAlternateColor.X, sun.FogAlternateColor.Y, sun.FogAlternateColor.Z);
            fogStartEnd = sun.FogStartEnd;
            fogEmissive = sun.FogEmissiveRemap;
        }

        private static Vector3 ResolveSunDirection(MapSunData sun, bool skinned = false)
        {
            Vector3 direction = sun?.Direction ?? new Vector3(-0.25f, 0.75f, -0.05f);
            direction = float.IsFinite(direction.X) && float.IsFinite(direction.Y) && float.IsFinite(direction.Z) &&
                        direction.LengthSquared() > 1e-12f
                ? Vector3.Normalize(direction)
                : Vector3.Normalize(new Vector3(-0.25f, 0.75f, -0.05f));
            if (skinned)
                direction.X = -direction.X;
            return direction;
        }

        private static void WriteVector4(float[] data, int at, int count, Vector4 value)
        {
            if (count > 0) Set(data, at, value.X);
            if (count > 1) Set(data, at + 1, value.Y);
            if (count > 2) Set(data, at + 2, value.Z);
            if (count > 3) Set(data, at + 3, value.W);
        }

        private static void WriteVector3(float[] data, int at, Vector3 value)
        {
            Set(data, at, value.X);
            Set(data, at + 1, value.Y);
            Set(data, at + 2, value.Z);
        }

        private static void WriteIdentityRows(float[] data, int at, int floats)
        {
            int rows = Math.Min(4, floats / 4);
            for (int row = 0; row < rows; row++)
                Set(data, at + row * 4 + row, 1f);
        }

        private static void WriteClipRows(float[] data, int at, Matrix4x4 matrix)
        {
            WriteMatrixRows(data, at, matrix);
            for (int column = 0; column < 4; column++)
            {
                int z = at + 8 + column;
                int w = at + 12 + column;
                if ((uint)z < (uint)data.Length && (uint)w < (uint)data.Length)
                    data[z] = (data[z] + data[w]) * 0.5f;
            }
        }

        /// <summary>
        /// System.Numerics matrices are row-vector matrices. The game cbuffer stores the rows of
        /// the equivalent column-vector matrix, so each written row is one CPU matrix column.
        /// </summary>
        private static void WriteMatrixRows(float[] data, int at, Matrix4x4 matrix)
        {
            Set(data, at + 0, matrix.M11); Set(data, at + 1, matrix.M21); Set(data, at + 2, matrix.M31); Set(data, at + 3, matrix.M41);
            Set(data, at + 4, matrix.M12); Set(data, at + 5, matrix.M22); Set(data, at + 6, matrix.M32); Set(data, at + 7, matrix.M42);
            Set(data, at + 8, matrix.M13); Set(data, at + 9, matrix.M23); Set(data, at + 10, matrix.M33); Set(data, at + 11, matrix.M43);
            Set(data, at + 12, matrix.M14); Set(data, at + 13, matrix.M24); Set(data, at + 14, matrix.M34); Set(data, at + 15, matrix.M44);
        }

        private static void Set(float[] data, int at, float value)
        {
            if (at >= 0 && at < data.Length)
                data[at] = value;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _programs.Clear();
            foreach (ProgramRuntime program in _sharedPrograms.Values)
                program?.Dispose();
            _sharedPrograms.Clear();
            _shaderCache?.Dispose();
            foreach (uint sampler in _samplers.Values)
                if (sampler != 0)
                    _gl.DeleteSampler(sampler);
            _samplers.Clear();
            if (_neutralGrey2D != 0) _gl.DeleteTexture(_neutralGrey2D);
            if (_neutralBlack2D != 0) _gl.DeleteTexture(_neutralBlack2D);
            if (_neutralWhite2D != 0) _gl.DeleteTexture(_neutralWhite2D);
            if (_neutralBuffer2D != 0) _gl.DeleteTexture(_neutralBuffer2D);
            foreach (uint texture in _neutralTypedTextures.Values)
                if (texture != 0)
                    _gl.DeleteTexture(texture);
            _neutralTypedTextures.Clear();
            _neutralGrey2D = _neutralBlack2D = _neutralWhite2D = _neutralBuffer2D = 0;
        }
    }
}

