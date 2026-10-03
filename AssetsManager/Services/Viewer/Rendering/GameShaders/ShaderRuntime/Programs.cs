using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Shaders;
using System.Text.RegularExpressions;
using AssetsManager.Utils.Rendering;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    internal sealed partial class GameShaderRuntime
    {
        private CacheEntry GetOrCreate(object owner, GameMaterialProgram program, bool? particleMesh = null)
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

                            string key = program.Kind + "|" + particleMesh + "|" + ProgramKey(passRead.Pass, passRead.Bytecode.Program);
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
                                    ready = CreateProgram(particleMesh.HasValue
                                        ? GameParticleShaderPrelude.Compose(translated.Program, particleMesh.Value)
                                        : translated.Program, program.Kind, particleMesh.HasValue);
                                    ready.WritesBloom = WritesSecondTarget(translated.Program.Pixel.Glsl);
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
            GameMaterialKind kind, bool particle = false)
        {
            IReadOnlyDictionary<uint, string> attributes = particle ? new Dictionary<uint, string>() :
                AttributeLocations(translated.Vertex.Sidecar.Attributes, kind);
            string vertex = SourceForProfile(translated.Vertex.Glsl, vertexStage: true, preserveInputs: particle);
            // Particle programs are composed with the screen-copy helpers already; material programs
            // (map water refraction) read the same framebuffer captures and need the same V flip.
            string pixel = SourceForProfile(
                particle ? translated.Pixel.Glsl : GameParticleShaderPrelude.WithScreenCopy(translated.Pixel.Glsl),
                vertexStage: false);
            uint program = GlShaderCompiler.CreateRawProgram(_gl, vertex, pixel, attributes);

            try
            {
                var blocks = new List<BlockRuntime>();
                uint boneBlock = particle ? _gl.GetUniformBlockIndex(program, "VfxBoneTransforms") : uint.MaxValue;
                bool hasParticleBones = boneBlock != uint.MaxValue;
                // The geometry prelude's palette is uploaded by the VFX renderer, outside
                // the translated shader sidecar. Reserve its slot across every native pass.
                if (hasParticleBones)
                    _gl.UniformBlockBinding(program, boneBlock, VfxShaderSource.BoneTransformsBinding);
                uint binding = 0;
                foreach (GameShaderTranslator.UniformBlock block in translated.Vertex.Sidecar.Blocks
                             .Concat(translated.Pixel.Sidecar.Blocks))
                {
                    uint index = _gl.GetUniformBlockIndex(program, block.GlslName);
                    if (index == uint.MaxValue)
                        continue;

                    if (hasParticleBones && binding == VfxShaderSource.BoneTransformsBinding) binding++;
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

        private string SourceForProfile(string source, bool vertexStage, bool preserveInputs = false)
        {
            string result = source ?? string.Empty;
            if (vertexStage && !preserveInputs)
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
            return shader + "|" + pass?.VertexShaderPath + "|" + pass?.PixelShaderPath + "|" + defines;
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
            bool hasTangents,
            bool hasColors = false,
            bool hasPivots = false)
        {
            foreach ((uint location, string name) in attributes)
            {
                bool provided = kind == GameMaterialKind.SkinnedMesh
                    ? location is 0 or 1 or 2 or 5 or 6 || (location == 3 && hasTangents) || (location == 4 && hasColors)
                    : location <= 3 ||
                      (location == StaticColorLocation && hasColors) ||
                      (location == StaticPivotLocation && hasPivots);
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
    }
}
