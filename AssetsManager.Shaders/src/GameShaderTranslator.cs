using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Vortice.SpirvCross;
using static Vortice.SpirvCross.SpirvCrossApi;

namespace AssetsManager.Shaders
{
    /// <summary>
    /// Translates DXBC shader stages into OpenGL-compatible GLSL through the
    /// dxbc-spirv/SPIRV-Cross pipeline used by the game-shader preview path.
    /// </summary>
    public static class GameShaderTranslator
    {
        private const uint OpName = 5;
        private const uint OpMemoryModel = 14;
        private const uint OpCapability = 17;
        private const uint OpTypePointer = 32;
        private const uint OpVariable = 59;
        private const uint OpDecorate = 71;
        private const uint OpLabel = 248;
        private const uint OpKill = 252;
        private const uint OpDemoteToHelperInvocation = 5380;
        private const uint CapabilityDerivativeControl = 51;
        private const uint CapabilityPhysicalStorageBufferAddresses = 5347;
        private const uint CapabilityDemoteToHelperInvocation = 5379;
        private const uint DecorationBuiltIn = 11;
        private const uint DecorationComponent = 31;
        private const uint DecorationNoContraction = 42;
        private const uint AddressingLogical = 0;
        private const uint StorageInput = 1;
        private const uint StorageOutput = 3;

        public enum Stage
        {
            Vertex,
            Pixel
        }

        public enum AppliedPatch
        {
            BitBuiltin,
            TexelBuffer,
            CubeArray,
            ShadowLevelZero
        }

        public enum MemberScalar
        {
            Float,
            Int,
            UInt,
            Bool
        }

        public enum TextureDimension
        {
            Texture2D,
            Texture2DArray,
            Texture3D,
            Cube,
            CubeArray,
            Buffer,
            Other
        }

        public sealed record BlockMember(
            string Name,
            uint Offset,
            uint Size,
            bool Used,
            MemberScalar Scalar,
            ushort Rows,
            ushort Columns,
            ushort Elements,
            bool RowMajor);

        public sealed record UniformBlock(
            string Name,
            string GlslName,
            uint Size,
            IReadOnlyList<BlockMember> Members);

        public sealed record SamplerBinding(string Sampler, string GlslName);

        public sealed record TextureBinding(
            string Name,
            TextureDimension Dimension,
            IReadOnlyList<SamplerBinding> Samplers);

        public sealed record AttributeBinding(
            string Semantic,
            uint Index,
            string GlslName,
            byte Mask);

        public sealed record ShaderSidecar(
            IReadOnlyList<UniformBlock> Blocks,
            IReadOnlyList<TextureBinding> Textures,
            IReadOnlyList<AttributeBinding> Attributes);

        public sealed record TranslatedStage(
            string Glsl,
            ShaderSidecar Sidecar,
            IReadOnlyList<AppliedPatch> Applied,
            IReadOnlyList<(string From, string To)> Renames,
            uint[] Spirv);

        public sealed record TranslatedProgram(TranslatedStage Vertex, TranslatedStage Pixel);

        public sealed record TranslationRead(TranslatedProgram Program, string Failure)
        {
            public bool Ready => Program != null;
        }

        public static TranslationRead Translate(
            byte[] vertex,
            ShaderReflectionData vertexReflection,
            byte[] pixel,
            ShaderReflectionData pixelReflection)
        {
            if (vertex == null || pixel == null || vertexReflection == null || pixelReflection == null)
                return new TranslationRead(null, "Shader bytecode or reflection is unavailable.");

            try
            {
                TranslatedStage translatedVertex = TranslateStage(vertex, vertexReflection, Stage.Vertex);
                TranslatedStage translatedPixel = TranslateStage(pixel, pixelReflection, Stage.Pixel);
                return new TranslationRead(new TranslatedProgram(translatedVertex, translatedPixel), null);
            }
            catch (Exception ex)
            {
                return new TranslationRead(null, ex.Message);
            }
        }

        public static TranslatedStage TranslateStage(
            byte[] dxbc,
            ShaderReflectionData reflection,
            Stage stage)
        {
            ArgumentNullException.ThrowIfNull(dxbc);
            ArgumentNullException.ThrowIfNull(reflection);

            uint[] compiled = CompileSpirv(dxbc);
            PatchedSpirv patched = PatchSpirv(compiled, reflection, stage);
            CrossCompileResult crossed = CrossCompile(patched.Words, stage);

            var extents = reflection.ConstantBuffers
                .Select(buffer => (Ident(buffer.Name), (buffer.Size + 15u) / 16u))
                .ToArray();
            string normalizedGlsl = NormalizeConstantBuffers(crossed.Glsl, reflection);
            (string glsl, IReadOnlyList<AppliedPatch> applied) = PatchGlsl(normalizedGlsl, stage, extents);
            if (stage == Stage.Vertex)
            {
                string[] labels = reflection.Outputs
                    .Where(entry => entry.SystemValue == 0)
                    .Select(BareLabel)
                    .ToArray();
                glsl = DeclareOutputs(glsl, labels);
            }

            ShaderSidecar sidecar = BuildSidecar(
                reflection,
                patched.Renames,
                crossed.SamplersByImage,
                stage);
            return new TranslatedStage(glsl, sidecar, applied, patched.Renames, patched.Words);
        }

        private sealed record PatchedSpirv(
            uint[] Words,
            IReadOnlyList<(string From, string To)> Renames);

        private sealed record CrossCompileResult(
            string Glsl,
            Dictionary<string, List<SamplerBinding>> SamplersByImage);

        private readonly record struct SpirvInstruction(int At, uint Op, int Count);

        public static uint[] CompileSpirv(byte[] dxbc) => Vkd3dSpirvCompiler.Compile(dxbc);

        public static string Ident(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "unnamed";

            var builder = new StringBuilder(name.Length);
            bool lastUnderscore = false;
            foreach (char original in name)
            {
                char ch = IsAsciiAlphaNumeric(original) ? original : '_';
                if (ch == '_' && lastUnderscore)
                    continue;
                lastUnderscore = ch == '_';
                builder.Append(ch);
            }

            string trimmed = builder.ToString().Trim('_');
            if (trimmed.Length == 0)
                return "unnamed";
            return trimmed[0] is >= '0' and <= '9' ? $"_{trimmed}" : trimmed;
        }

        private static bool IsAsciiAlphaNumeric(char ch) =>
            ch is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

        private static PatchedSpirv PatchSpirv(
            uint[] words,
            ShaderReflectionData reflection,
            Stage stage)
        {
            if (words == null || words.Length < 5)
                throw new InvalidOperationException("SPIR-V module shorter than its header.");

            IReadOnlyList<SpirvInstruction> instructions = ReadInstructions(words);
            var namesById = new Dictionary<uint, string>();
            var pointeeOf = new Dictionary<uint, uint>();
            var variables = new Dictionary<uint, (uint PointerType, uint Storage)>();
            var builtins = new HashSet<uint>();

            foreach (SpirvInstruction instruction in instructions)
            {
                ReadOnlySpan<uint> inst = words.AsSpan(instruction.At, instruction.Count);
                if (instruction.Op == OpName && instruction.Count >= 3)
                    namesById[inst[1]] = ReadSpirvString(inst[2..]);
                else if (instruction.Op == OpTypePointer && instruction.Count >= 4)
                    pointeeOf[inst[1]] = inst[3];
                else if (instruction.Op == OpVariable && instruction.Count >= 4)
                    variables[inst[2]] = (inst[1], inst[3]);
                else if (instruction.Op == OpDecorate && instruction.Count >= 3 && inst[2] == DecorationBuiltIn)
                    builtins.Add(inst[1]);
            }

            var newNames = new Dictionary<uint, string>();
            foreach ((uint id, string name) in namesById)
            {
                if (!variables.TryGetValue(id, out (uint PointerType, uint Storage) variable))
                    continue;

                if ((variable.Storage == StorageInput || variable.Storage == StorageOutput) && !builtins.Contains(id))
                {
                    string prefix = (stage, variable.Storage == StorageInput) switch
                    {
                        (Stage.Vertex, true) => "a_",
                        (Stage.Vertex, false) => "v_",
                        (Stage.Pixel, true) => "v_",
                        _ => null
                    };
                    if (prefix != null)
                    {
                        string semantic = InterfaceSemanticName(reflection, variable.Storage, name);
                        newNames[id] = prefix + Ident(semantic);
                    }
                    continue;
                }

                if (TryRegister(name, "cb", out uint cbufferBind))
                {
                    ShaderResourceData buffer = FindResource(
                        reflection,
                        ShaderResourceKind.Cbuffer,
                        cbufferBind);
                    if (buffer != null)
                    {
                        string block = Ident(buffer.Name);
                        newNames[id] = block + "_i";
                        if (pointeeOf.TryGetValue(variable.PointerType, out uint structType))
                            newNames[structType] = block;
                    }
                    continue;
                }

                if (TryRegister(name, "s", out uint samplerBind))
                {
                    ShaderResourceData sampler = FindResource(
                        reflection,
                        ShaderResourceKind.Sampler,
                        samplerBind);
                    if (sampler != null)
                        newNames[id] = Ident(sampler.Name);
                    continue;
                }

                if (TryRegister(name, "t", out uint viewBind))
                {
                    ShaderResourceData view = FindView(reflection, viewBind);
                    if (view != null)
                        newNames[id] = Ident(view.Name);
                }
            }

            var output = new List<uint>(words.Length + 8);
            output.AddRange(words.AsSpan(0, 5).ToArray());
            var renames = new List<(string From, string To)>();
            foreach (SpirvInstruction instruction in instructions)
            {
                ReadOnlySpan<uint> inst = words.AsSpan(instruction.At, instruction.Count);
                if (instruction.Op == OpCapability &&
                    (inst[1] == CapabilityDerivativeControl ||
                     inst[1] == CapabilityPhysicalStorageBufferAddresses ||
                     inst[1] == CapabilityDemoteToHelperInvocation))
                {
                    continue;
                }

                if (instruction.Op == OpDecorate && instruction.Count >= 3 &&
                    (inst[2] == DecorationComponent || inst[2] == DecorationNoContraction))
                {
                    continue;
                }

                if (instruction.Op == OpDemoteToHelperInvocation)
                {
                    uint fresh = output[3];
                    output[3] = fresh + 1;
                    output.Add((1u << 16) | OpKill);
                    output.Add((2u << 16) | OpLabel);
                    output.Add(fresh);
                    continue;
                }

                if (instruction.Op == OpMemoryModel && instruction.Count >= 3)
                {
                    output.Add(inst[0]);
                    output.Add(AddressingLogical);
                    output.Add(inst[2]);
                    continue;
                }

                if (instruction.Op == OpName && instruction.Count >= 3 && newNames.TryGetValue(inst[1], out string renamed))
                {
                    renames.Add((ReadSpirvString(inst[2..]), renamed));
                    uint[] text = SpirvString(renamed);
                    output.Add(((2u + (uint)text.Length) << 16) | OpName);
                    output.Add(inst[1]);
                    output.AddRange(text);
                    continue;
                }

                if (TryPlainDerivative(instruction.Op, out uint plain))
                {
                    output.Add((inst[0] & 0xFFFF0000u) | plain);
                    for (int index = 1; index < inst.Length; index++)
                        output.Add(inst[index]);
                    continue;
                }

                for (int index = 0; index < inst.Length; index++)
                    output.Add(inst[index]);
            }

            return new PatchedSpirv(output.ToArray(), renames);
        }

        private static IReadOnlyList<SpirvInstruction> ReadInstructions(uint[] words)
        {
            var result = new List<SpirvInstruction>();
            int at = 5;
            while (at < words.Length)
            {
                int count = checked((int)(words[at] >> 16));
                if (count == 0 || at > words.Length - count)
                    throw new InvalidOperationException($"SPIR-V instruction at word {at} runs past the module.");
                result.Add(new SpirvInstruction(at, words[at] & 0xFFFFu, count));
                at += count;
            }
            return result;
        }

        private static bool TryPlainDerivative(uint op, out uint plain)
        {
            plain = op switch
            {
                210 or 213 => 207,
                211 or 214 => 208,
                212 or 215 => 209,
                _ => 0
            };
            return plain != 0;
        }

        private static bool TryRegister(string name, string prefix, out uint bind)
        {
            bind = 0;
            if (string.IsNullOrEmpty(name) || !name.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            string digits = name[prefix.Length..];
            return digits.Length > 0 && digits.All(char.IsAsciiDigit) && uint.TryParse(digits, out bind);
        }

        private static string InterfaceSemanticName(
            ShaderReflectionData reflection,
            uint storage,
            string generatedName)
        {
            IReadOnlyList<ShaderSignatureData> signature = storage == StorageInput
                ? reflection.Inputs
                : reflection.Outputs;
            string registerPrefix = storage == StorageOutput ? "o" : "v";
            if (TryRegister(generatedName, registerPrefix, out uint register))
            {
                ShaderSignatureData byRegister = signature.FirstOrDefault(entry =>
                    entry.SystemValue == 0 && entry.Register == register);
                if (byRegister != null)
                    return BareLabel(byRegister);
            }

            string generated = Ident(generatedName);
            ShaderSignatureData bySemantic = signature.FirstOrDefault(entry =>
                entry.SystemValue == 0 &&
                (Ident(entry.Label) == generated ||
                 (entry.Index == 0 && Ident(entry.Semantic) == generated)));
            return bySemantic != null ? BareLabel(bySemantic) : generatedName;
        }

        private static string ReadSpirvString(ReadOnlySpan<uint> words)
        {
            byte[] bytes = new byte[checked(words.Length * sizeof(uint))];
            for (int index = 0; index < words.Length; index++)
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint), sizeof(uint)), words[index]);
            int end = Array.IndexOf(bytes, (byte)0);
            if (end < 0)
                end = bytes.Length;
            return Encoding.UTF8.GetString(bytes, 0, end);
        }

        public static uint[] SpirvString(string text)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text ?? string.Empty);
            int byteCount = checked(utf8.Length + 1);
            int padded = (byteCount + 3) & ~3;
            byte[] bytes = new byte[padded];
            utf8.CopyTo(bytes, 0);
            var words = new uint[padded / 4];
            for (int index = 0; index < words.Length; index++)
                words[index] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * 4, 4));
            return words;
        }

        private static ShaderResourceData FindResource(
            ShaderReflectionData reflection,
            ShaderResourceKind kind,
            uint bind) =>
            reflection.Resources.FirstOrDefault(resource => resource.Kind == kind && resource.Bind == bind);

        private static ShaderResourceData FindView(
            ShaderReflectionData reflection,
            uint bind) =>
            reflection.Resources.FirstOrDefault(resource =>
                resource.Bind == bind &&
                resource.Kind is ShaderResourceKind.Texture or
                    ShaderResourceKind.Structured or
                    ShaderResourceKind.Tbuffer);

        private static unsafe CrossCompileResult CrossCompile(uint[] words, Stage stage)
        {
            Check(spvc_context_create(out spvc_context context), default, "create SPIRV-Cross context");
            try
            {
                Check(spvc_context_parse_spirv(context, words, out spvc_parsed_ir parsed), context, "parse SPIR-V");
                Check(
                    spvc_context_create_compiler(context, Backend.GLSL, parsed, CaptureMode.TakeOwnership, out spvc_compiler compiler),
                    context,
                    "create GLSL compiler");

                uint dummySampler = 0;
                Check(
                    spvc_compiler_build_dummy_sampler_for_combined_images(compiler, &dummySampler),
                    context,
                    "create dummy sampler");
                uint dummySamplerId = dummySampler;
                Check(spvc_compiler_build_combined_image_samplers(compiler), context, "build combined samplers");

                spvc_combined_image_sampler[] combined = spvc_compiler_get_combined_image_samplers(compiler).ToArray();
                var descriptions = combined.Select(sampler =>
                {
                    string image = spvc_compiler_get_name(compiler, sampler.image_id) ?? string.Empty;
                    string by = sampler.sampler_id == dummySamplerId
                        ? null
                        : spvc_compiler_get_name(compiler, sampler.sampler_id);
                    return (sampler.combined_id, Image: image, By: by);
                }).ToArray();
                var perImage = descriptions
                    .GroupBy(item => item.Image, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                var samplersByImage = new Dictionary<string, List<SamplerBinding>>(StringComparer.Ordinal);
                foreach ((uint combinedId, string image, string by) in descriptions)
                {
                    string glslName = by != null && perImage[image] > 1 ? $"{image}_{by}" : image;
                    spvc_compiler_set_name(compiler, combinedId, glslName);
                    if (!samplersByImage.TryGetValue(image, out List<SamplerBinding> bindings))
                    {
                        bindings = new List<SamplerBinding>();
                        samplersByImage.Add(image, bindings);
                    }
                    bindings.Add(new SamplerBinding(by, glslName));
                }

                Check(spvc_compiler_create_compiler_options(compiler, out spvc_compiler_options options), context, "create compiler options");
                SetOption(options, CompilerOption.GLSLVersion, 300u, context);
                SetOption(options, CompilerOption.GLSLES, true, context);
                SetOption(options, CompilerOption.GLSLVulkanSemantics, false, context);
                SetOption(options, CompilerOption.GLSLESDefaultFloatPrecisionHighp, true, context);
                SetOption(options, CompilerOption.GLSLESDefaultIntPrecisionHighp, true, context);
                SetOption(options, CompilerOption.RelaxNanChecks, true, context);
                SetOption(options, CompilerOption.FixupDepthConvention, stage == Stage.Vertex, context);
                Check(spvc_compiler_install_compiler_options(compiler, options), context, "install compiler options");
                Check(spvc_compiler_compile(compiler, out string glsl), context, "compile GLSL");
                return new CrossCompileResult(glsl, samplersByImage);
            }
            finally
            {
                spvc_context_destroy(context);
            }
        }

        private static void SetOption(
            spvc_compiler_options options,
            CompilerOption option,
            bool value,
            spvc_context context) =>
            Check(spvc_compiler_options_set_bool(options, option, value), context, $"set {option}");

        private static void SetOption(
            spvc_compiler_options options,
            CompilerOption option,
            uint value,
            spvc_context context) =>
            Check(spvc_compiler_options_set_uint(options, option, value), context, $"set {option}");

        private static void Check(Result result, spvc_context context, string operation)
        {
            if (result == Result.Success)
                return;
            string detail = context.IsNotNull ? spvc_context_get_last_error_string(context) : null;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? $"SPIRV-Cross failed to {operation}: {result}."
                    : $"SPIRV-Cross failed to {operation}: {detail}");
        }

        private static ShaderSidecar BuildSidecar(
            ShaderReflectionData reflection,
            IReadOnlyList<(string From, string To)> renames,
            Dictionary<string, List<SamplerBinding>> samplersByImage,
            Stage stage)
        {
            UniformBlock[] blocks = reflection.ConstantBuffers.Select(buffer =>
                new UniformBlock(
                    buffer.Name,
                    BlockName(stage, Ident(buffer.Name)),
                    buffer.Size,
                    buffer.Members.Select(member => new BlockMember(
                        member.Name,
                        member.Offset,
                        member.Size,
                        member.Used,
                        MemberScalarOf(member.Type.Scalar),
                        member.Type.Rows,
                        member.Type.Columns,
                        member.Type.Elements,
                        member.Type.Class == ShaderTypeClass.MatrixRows)).ToArray()))
                .ToArray();

            TextureBinding[] textures = reflection.Resources
                .Where(resource => resource.Kind is ShaderResourceKind.Texture or
                    ShaderResourceKind.Structured or
                    ShaderResourceKind.Tbuffer)
                .Select(resource =>
                {
                    string image = Ident(resource.Name);
                    samplersByImage.TryGetValue(image, out List<SamplerBinding> samplers);
                    return new TextureBinding(
                        resource.Name,
                        TextureDimensionOf(resource.Dimension),
                        (IReadOnlyList<SamplerBinding>)(samplers?.ToArray() ?? Array.Empty<SamplerBinding>()));
                })
                .ToArray();

            AttributeBinding[] attributes = stage == Stage.Vertex
                ? reflection.Inputs
                    .Where(entry => entry.SystemValue == 0)
                    .Select(entry =>
                    {
                        string label = entry.Label;
                        string expected = "a_" + Ident(BareLabel(entry));
                        string glslName = renames
                            .Where(rename => rename.To.StartsWith("a_", StringComparison.Ordinal))
                            .Where(rename =>
                                string.Equals(rename.To, expected, StringComparison.Ordinal) ||
                                string.Equals(rename.From, "v" + entry.Register, StringComparison.Ordinal) ||
                                Ident(rename.From) == Ident(label) ||
                                (entry.Index == 0 && Ident(rename.From) == Ident(entry.Semantic)))
                            .Select(rename => rename.To)
                            .FirstOrDefault();
                        return glslName == null
                            ? null
                            : new AttributeBinding(entry.Semantic, entry.Index, glslName, entry.Used);
                    })
                    .Where(attribute => attribute != null)
                    .ToArray()
                : Array.Empty<AttributeBinding>();

            return new ShaderSidecar(blocks, textures, attributes);
        }

        private static MemberScalar MemberScalarOf(ShaderScalarKind scalar) =>
            scalar switch
            {
                ShaderScalarKind.Int => MemberScalar.Int,
                ShaderScalarKind.UInt => MemberScalar.UInt,
                ShaderScalarKind.Bool => MemberScalar.Bool,
                _ => MemberScalar.Float
            };

        private static TextureDimension TextureDimensionOf(ShaderResourceDimension dimension) =>
            dimension switch
            {
                ShaderResourceDimension.Texture2D => TextureDimension.Texture2D,
                ShaderResourceDimension.Texture2DArray => TextureDimension.Texture2DArray,
                ShaderResourceDimension.Texture3D => TextureDimension.Texture3D,
                ShaderResourceDimension.Cube => TextureDimension.Cube,
                ShaderResourceDimension.CubeArray => TextureDimension.CubeArray,
                ShaderResourceDimension.Buffer => TextureDimension.Buffer,
                _ => TextureDimension.Other
            };

        private static string BlockName(Stage stage, string name) => stage == Stage.Vertex ? name + "_vs" : name + "_ps";

        private static string BareLabel(ShaderSignatureData entry) =>
            entry.Index == 0 ? entry.Semantic : entry.Label;

        private static readonly (string Name, string Body)[] Polyfills =
        {
            ("bitfieldInsert", "uint bitfieldInsert(uint b, uint v, int o, int n) { uint m = (n >= 32 ? 0xffffffffu : ((1u << uint(n)) - 1u)) << uint(o); return (b & ~m) | ((v << uint(o)) & m); }\n int bitfieldInsert(int b, int v, int o, int n) { return int(bitfieldInsert(uint(b), uint(v), o, n)); }\n"),
            ("bitfieldExtract", "uint bitfieldExtract(uint v, int o, int n) { return n >= 32 ? v >> uint(o) : (v >> uint(o)) & ((1u << uint(n)) - 1u); }\n int bitfieldExtract(int v, int o, int n) { return n == 0 ? 0 : (v << (32 - n - o)) >> (32 - n); }\n"),
            ("bitCount", "int bitCount(uint v) { v = v - ((v >> 1u) & 0x55555555u); v = (v & 0x33333333u) + ((v >> 2u) & 0x33333333u); return int((((v + (v >> 4u)) & 0x0F0F0F0Fu) * 0x01010101u) >> 24u); }\n int bitCount(int v) { return bitCount(uint(v)); }\n"),
            ("findLSB", "int findLSB(uint v) { if (v == 0u) return -1; int i = 0; while ((v & 1u) == 0u) { v >>= 1u; i++; } return i; }\n int findLSB(int v) { return findLSB(uint(v)); }\n"),
            ("findMSB", "int findMSB(uint v) { if (v == 0u) return -1; int i = 31; while ((v & 0x80000000u) == 0u) { v <<= 1u; i--; } return i; }\n int findMSB(int v) { return v < 0 ? findMSB(uint(~v)) : findMSB(uint(v)); }\n")
        };

        private static readonly (string Sampler, string Body)[] BufferFetch =
        {
            ("usampler2D", "uvec4 dxbcBufferFetch(highp usampler2D t, int i) { int w = textureSize(t, 0).x; return texelFetch(t, ivec2(i % w, i / w), 0); }\n"),
            ("isampler2D", "ivec4 dxbcBufferFetch(highp isampler2D t, int i) { int w = textureSize(t, 0).x; return texelFetch(t, ivec2(i % w, i / w), 0); }\n"),
            ("sampler2D", "vec4 dxbcBufferFetch(highp sampler2D t, int i) { int w = textureSize(t, 0).x; return texelFetch(t, ivec2(i % w, i / w), 0); }\n")
        };

        private const string CubeArrayLod =
            "vec4 dxbcCubeArrayLod(highp sampler2DArray t, vec4 c, float lod) {\n" +
            "   vec3 d = c.xyz; vec3 a = abs(d); float face; vec2 uv; float ma;\n" +
            "   if (a.x >= a.y && a.x >= a.z) { ma = a.x; face = d.x > 0.0 ? 0.0 : 1.0; uv = vec2(d.x > 0.0 ? -d.z : d.z, -d.y); }\n" +
            "   else if (a.y >= a.z) { ma = a.y; face = d.y > 0.0 ? 2.0 : 3.0; uv = vec2(d.x, d.y > 0.0 ? d.z : -d.z); }\n" +
            "   else { ma = a.z; face = d.z > 0.0 ? 4.0 : 5.0; uv = vec2(d.z > 0.0 ? d.x : -d.x, -d.y); }\n" +
            "   return textureLod(t, vec3(uv / ma * 0.5 + 0.5, floor(c.w + 0.5) * 6.0 + face), lod);\n" +
            "}\n";

        private static readonly Regex TexelBufferRegex = new(
            @"uniform\s+highp\s+([iu]?samplerBuffer)\s+(\w+);",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex CubeArrayRegex = new(
            @"uniform\s+highp\s+samplerCubeArray\s+(\w+);",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex ShadowSamplerRegex = new(
            @"uniform\s+highp\s+sampler2DShadow\s+(\w+);",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex BlockRegex = new(
            @"(layout\(std140\) uniform )(\w+)\n",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex GeneratedCbufferRegex = new(
            @"layout\(std140\) uniform (?<block>\w+)(?<body>\n\{.*?\n\}\s+cb(?<bind>\d+)_\d+;)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        private static readonly Regex ExtensionRegex = new(
            @"#extension GL_EXT_texture_(buffer|cube_map_array) : require\n",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static string NormalizeConstantBuffers(string source, ShaderReflectionData reflection)
        {
            if (string.IsNullOrEmpty(source) || reflection?.ConstantBuffers == null)
                return source ?? string.Empty;

            return GeneratedCbufferRegex.Replace(source, match =>
            {
                if (!uint.TryParse(match.Groups["bind"].Value, out uint bind))
                    return match.Value;
                ShaderConstantBufferData buffer = reflection.ConstantBuffers
                    .FirstOrDefault(candidate => candidate.Bind == bind);
                if (buffer == null)
                    return match.Value;
                return "layout(std140) uniform " + Ident(buffer.Name) + match.Groups["body"].Value;
            });
        }

        public static (string Glsl, IReadOnlyList<AppliedPatch> Applied) PatchGlsl(
            string source,
            Stage stage,
            IReadOnlyList<(string Block, uint Vec4s)> extents)
        {
            string result = DeclaredExtents(source ?? string.Empty, extents ?? Array.Empty<(string, uint)>());
            (result, bool polyfilled) = Polyfill(result);
            var applied = new List<AppliedPatch>();
            if (polyfilled)
                applied.Add(AppliedPatch.BitBuiltin);
            (result, IReadOnlyList<AppliedPatch> emulated) = WebGl2Emulation(result);
            applied.AddRange(emulated);
            (result, int shadowed) = ShadowLevelZero(result);
            if (shadowed > 0)
                applied.Add(AppliedPatch.ShadowLevelZero);
            result = LinkFixup(result, stage);
            return (result, applied.Distinct().OrderBy(value => value).ToArray());
        }

        public static string DeclareOutputs(string source, IReadOnlyList<string> labels)
        {
            string[] missing = (labels ?? Array.Empty<string>())
                .Select(label => "v_" + Ident(label))
                .Where(name => !source.Contains($"out vec4 {name};", StringComparison.Ordinal))
                .Where(name => !source.Contains($" {name};", StringComparison.Ordinal))
                .ToArray();
            if (missing.Length == 0)
                return source;

            string declarations = string.Concat(missing.Select(name => $"out vec4 {name};\n"));
            int at = source.IndexOf("\nvoid main()", StringComparison.Ordinal);
            return at >= 0
                ? source[..at] + "\n" + declarations + source[(at + 1)..]
                : source + declarations;
        }

        private static string DeclaredExtents(string source, IReadOnlyList<(string Block, uint Vec4s)> extents)
        {
            string result = source;
            foreach ((string block, uint vec4s) in extents)
            {
                var regex = new Regex(
                    $@"(layout\(std140\) uniform {Regex.Escape(block)}(?:_(?:vs|ps))?\n\{{\n\s+[iu]?vec4 m\[)(\d+)(\];)",
                    RegexOptions.CultureInvariant);
                result = regex.Replace(result, match =>
                {
                    uint written = uint.TryParse(match.Groups[2].Value, out uint parsed) ? parsed : 0;
                    uint extent = written > vec4s ? vec4s : written;
                    return match.Groups[1].Value + extent + match.Groups[3].Value;
                }, 1);
            }
            return result;
        }

        private static (string Source, bool Changed) Polyfill(string source)
        {
            string[] bodies = Polyfills
                .Where(item => Calls(source, item.Name))
                .Select(item => item.Body)
                .ToArray();
            if (bodies.Length == 0)
                return (source, false);

            int line = source.IndexOf('\n');
            string head = line >= 0 ? source[..line] : source;
            string rest = line >= 0 ? source[(line + 1)..] : string.Empty;
            return (head + "\n" + string.Concat(bodies) + rest, true);
        }

        private static (string Source, IReadOnlyList<AppliedPatch> Applied) WebGl2Emulation(string source)
        {
            string result = source;
            var applied = new List<AppliedPatch>();
            var helpers = new StringBuilder();

            var buffers = TexelBufferRegex.Matches(result)
                .Select(match => (Kind: match.Groups[1].Value, Name: match.Groups[2].Value))
                .ToArray();
            foreach ((string kind, string name) in buffers)
            {
                string flat = kind.Replace("Buffer", "2D", StringComparison.Ordinal);
                result = result.Replace($"highp {kind} {name};", $"highp {flat} {name};", StringComparison.Ordinal);
                result = result.Replace($"texelFetch({name},", $"dxbcBufferFetch({name},", StringComparison.Ordinal);
                string helper = BufferFetch.First(item => item.Sampler == flat).Body;
                if (!helpers.ToString().Contains(helper, StringComparison.Ordinal))
                    helpers.Append(helper);
                applied.Add(AppliedPatch.TexelBuffer);
            }

            string[] cubes = CubeArrayRegex.Matches(result)
                .Select(match => match.Groups[1].Value)
                .ToArray();
            foreach (string name in cubes)
            {
                result = result.Replace($"highp samplerCubeArray {name};", $"highp sampler2DArray {name};", StringComparison.Ordinal);
                result = result.Replace($"textureLod({name},", $"dxbcCubeArrayLod({name},", StringComparison.Ordinal);
                if (!helpers.ToString().Contains(CubeArrayLod, StringComparison.Ordinal))
                    helpers.Append(CubeArrayLod);
                applied.Add(AppliedPatch.CubeArray);
            }

            if (applied.Count > 0)
            {
                result = ExtensionRegex.Replace(result, string.Empty);
                result = WithHelpers(result, helpers.ToString());
            }

            return (result, applied.Distinct().OrderBy(value => value).ToArray());
        }

        private static string WithHelpers(string source, string helpers)
        {
            string[] lines = source.Split('\n');
            int at = 1;
            int limit = Math.Min(80, lines.Length);
            for (int index = 0; index < limit; index++)
            {
                if (lines[index].StartsWith("#extension", StringComparison.Ordinal) ||
                    lines[index].StartsWith("precision ", StringComparison.Ordinal))
                {
                    at = index + 1;
                }
            }

            return string.Join("\n", lines.Take(at)) + "\n" +
                   helpers.TrimEnd('\n') + "\n" +
                   string.Join("\n", lines.Skip(at));
        }

        private static (string Source, int Count) ShadowLevelZero(string source)
        {
            string[] names = ShadowSamplerRegex.Matches(source)
                .Select(match => match.Groups[1].Value)
                .ToArray();
            string result = source;
            int rewritten = 0;
            foreach (string name in names)
            {
                var builder = new StringBuilder(result.Length);
                int last = 0;
                foreach ((int start, int end, IReadOnlyList<string> args) in CallsOf(result, "textureLod"))
                {
                    if (args.Count != 3 || args[0] != name || (args[2] != "0.0" && args[2] != "0"))
                        continue;
                    builder.Append(result, last, start - last);
                    builder.Append($"textureGrad({name}, {args[1]}, vec2(0.0), vec2(0.0))");
                    last = end;
                    rewritten++;
                }
                builder.Append(result, last, result.Length - last);
                result = builder.ToString();
            }
            return (result, rewritten);
        }

        private static string LinkFixup(string source, Stage stage)
        {
            string suffix = stage == Stage.Vertex ? "_vs" : "_ps";
            string result = BlockRegex.Replace(source, match => match.Groups[1].Value + match.Groups[2].Value + suffix + "\n");
            string keyword = stage == Stage.Vertex ? "out" : "in";
            var declaration = new Regex(
                $@"^{keyword} (highp |mediump )?(float|vec2|vec3) (v_\w+);$",
                RegexOptions.Multiline | RegexOptions.CultureInvariant);
            var narrow = declaration.Matches(result)
                .Select(match => (
                    Precision: match.Groups[1].Success ? match.Groups[1].Value : string.Empty,
                    Type: match.Groups[2].Value,
                    Name: match.Groups[3].Value))
                .ToArray();

            foreach ((string precision, string type, string name) in narrow)
            {
                result = result.Replace(
                    $"{keyword} {precision}{type} {name};",
                    $"{keyword} {precision}vec4 {name};",
                    StringComparison.Ordinal);
                if (stage == Stage.Vertex)
                {
                    var write = new Regex(
                        $@"^(\s*){Regex.Escape(name)} = (.+);$",
                        RegexOptions.Multiline | RegexOptions.CultureInvariant);
                    string pad = type switch
                    {
                        "float" => ", 0.0, 0.0, 0.0",
                        "vec2" => ", 0.0, 0.0",
                        _ => ", 0.0"
                    };
                    result = write.Replace(result, match =>
                        match.Groups[1].Value + name + " = vec4(" + match.Groups[2].Value + pad + ");");
                }
                else
                {
                    string swizzle = type switch
                    {
                        "float" => ".x",
                        "vec2" => ".xy",
                        _ => ".xyz"
                    };
                    result = string.Join("\n", result.Split('\n').Select(line =>
                        line.StartsWith("in ", StringComparison.Ordinal) ? line : Swizzled(line, name, swizzle)));
                }
            }
            return result;
        }

        private static string Swizzled(string line, string name, string swizzle)
        {
            var word = new Regex($@"\b{Regex.Escape(name)}\b", RegexOptions.CultureInvariant);
            var builder = new StringBuilder(line.Length + 8);
            int last = 0;
            foreach (Match match in word.Matches(line))
            {
                builder.Append(line, last, match.Index + match.Length - last);
                string rest = line[(match.Index + match.Length)..].TrimStart();
                if (!rest.StartsWith(".", StringComparison.Ordinal))
                    builder.Append(swizzle);
                last = match.Index + match.Length;
            }
            builder.Append(line, last, line.Length - last);
            return builder.ToString();
        }

        private static bool Calls(string source, string name) =>
            Regex.IsMatch(source, $@"\b{Regex.Escape(name)}\(", RegexOptions.CultureInvariant);

        private static IReadOnlyList<(int Start, int End, IReadOnlyList<string> Args)> CallsOf(
            string source,
            string function)
        {
            var result = new List<(int Start, int End, IReadOnlyList<string> Args)>();
            int from = 0;
            string needle = function + "(";
            while (from < source.Length)
            {
                int found = source.IndexOf(needle, from, StringComparison.Ordinal);
                if (found < 0)
                    break;
                bool precededByWord = found > 0 &&
                    (char.IsAsciiLetterOrDigit(source[found - 1]) || source[found - 1] == '_');
                if (precededByWord)
                {
                    from = found + 1;
                    continue;
                }

                int open = found + needle.Length;
                int depth = 1;
                var args = new List<string>();
                int argStart = open;
                int at = open;
                while (depth > 0 && at < source.Length)
                {
                    char ch = source[at];
                    if (ch == '(')
                        depth++;
                    else if (ch == ')')
                    {
                        depth--;
                        if (depth == 0)
                            args.Add(source[argStart..at].Trim());
                    }
                    else if (ch == ',' && depth == 1)
                    {
                        args.Add(source[argStart..at].Trim());
                        argStart = at + 1;
                    }
                    at++;
                }

                if (depth != 0)
                    break;
                result.Add((found, at, args));
                from = at;
            }
            return result;
        }
    }
}

