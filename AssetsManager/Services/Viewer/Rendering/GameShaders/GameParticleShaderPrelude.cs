using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AssetsManager.Shaders;
using AssetsManager.Services.Viewer.Vfx.Rendering;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    internal static class GameParticleShaderPrelude
    {
        internal static GameShaderTranslator.TranslatedProgram Compose(GameShaderTranslator.TranslatedProgram program, bool mesh)
        {
            string feed = mesh ? VfxShaderSource.MeshVertex : VfxShaderSource.ParticleVertex;
            feed = Regex.Replace(feed, @"^out (\w+) (\w+);", "$1 $2;", RegexOptions.Multiline);
            feed = feed.Replace("void main(){", "void particleGeometry(){");
            feed = mesh
                ? feed.Replace("gl_Position = uViewProj * vec4(p, 1.0);", "particleWorld = p; particleNormal = worldSurface;")
                : feed.Replace("world += normalize(eyeRay) * uDepthPushPull;", "world += vec3(0.0);")
                    .Replace("gl_Position = uViewProj * vec4(world, 1.0);", "particleWorld = world; particleNormal = normalize(cross(right, up));");
            bool transformsMeshUv = mesh && program.Vertex.Sidecar.Blocks
                .SelectMany(block => block.Members)
                .Any(member => member.Name is "vParticleUVTransform" or "vParticleUVTransformMult");
            var inputs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["a_POSITION"] = "vec4(particleWorld, 1.0)",
                ["a_NORMAL"] = "vec4(particleNormal, 0.0)",
                ["a_COLOR"] = mesh ? "vMeshColor.bgra" : "particleTint.bgra",
                ["a_TEXCOORD"] = transformsMeshUv
                    ? "vec4(aUv, 0.0, particleErosion)" : "vec4(particleBaseUv, 0.0, particleErosion)",
                ["a_TEXCOORD1"] = "vec4(uHasTexMult != 0 ? particleMultUv : particleLookup, 0.0, 0.0)",
                ["a_BLENDWEIGHT"] = "vec4(1.0, 0.0, 0.0, 0.0)",
                ["a_BLENDINDICES"] = "vec4(0.0)"
            };
            var assignments = new StringBuilder();
            string vertex = Regex.Replace(program.Vertex.Glsl,
                @"^(?:layout\(location\s*=\s*\d+\)\s*)?in\s+(\w+)\s+(a_\w+);", match =>
                {
                    string type = match.Groups[1].Value;
                    string name = match.Groups[2].Value;
                    string value = inputs.GetValueOrDefault(name) ?? (name.Contains("COLOR", StringComparison.Ordinal) ? "vec4(1.0)" : "vec4(0.0, 0.0, 0.0, 1.0)");
                    assignments.AppendLine($"    {name} = {type}({value});");
                    return $"{type} {name};";
                }, RegexOptions.Multiline);
            vertex = Regex.Replace(vertex, @"\bvoid main\(\)", "void gameParticleMain()", RegexOptions.CultureInvariant);
            string shared = "vec3 particleWorld; vec3 particleNormal; vec4 particleTint; vec2 particleBaseUv; vec2 particleMultUv; vec2 particleLookup; float particleErosion;\n";
            if (mesh) shared += "vec4 particleUvRowU; vec4 particleUvRowV; vec4 particleMultRowU; vec4 particleMultRowV;\n";
            string helpers = Helpers(mesh);
            if (mesh) helpers = helpers.Replace("vColorDynamics", "uGameLookupDrivers");
            string geometry = feed + "\n" + helpers;
            // Geometry and its uniform declarations precede accessor prototypes and the translated stage.
            vertex = InsertAfterPrecision(vertex, shared + geometry);
            var values = new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["mWorld"] = Identity(),
                ["BONES"] = Identity().Take(3).ToArray(),
                ["kColorFactor"] = new[] { "particleTint" },
                // Screen UVs and separate alpha UVs need the affine basis, not a resolved vertex UV.
                ["vParticleUVTransform"] = mesh
                    ? new[] { "particleUvRowU", "particleUvRowV" }
                    : new[] { "vec4(0.0, 0.0, particleBaseUv.x, 0.0)", "vec4(0.0, 0.0, particleBaseUv.y, 0.0)" },
                ["vParticleUVTransformMult"] = mesh
                    ? new[] { "particleMultRowU", "particleMultRowV" }
                    : new[] { "vec4(0.0, 0.0, particleMultUv.x, 0.0)", "vec4(0.0, 0.0, particleMultUv.y, 0.0)" },
                ["COLOR_LOOKUP_UV"] = new[] { "vec4(particleLookup, 0.0, 0.0)" },
                ["cAlphaErosionParams"] = new[] { "vec4(particleErosion, uErosionSliceWidth, 1.0 / max(uErosionFeatherIn, 0.0001), 1.0 / max(uErosionFeatherOut, 0.0001))" }
            };
            vertex = RewriteMembers(vertex, program.Vertex.Sidecar, values);
            string pixel = program.Pixel.Glsl;
            var pixelValues = new Dictionary<string, string[]>(StringComparer.Ordinal);
            var output = new StringBuilder();
            var handoff = new StringBuilder();
            foreach (var member in program.Pixel.Sidecar.Blocks.SelectMany(block => block.Members))
            {
                if (!values.TryGetValue(member.Name, out var registers) || pixelValues.ContainsKey(member.Name)) continue;
                var names = new string[registers.Length];
                for (int register = 0; register < registers.Length; register++)
                {
                    names[register] = $"particle_{member.Name}_{register}";
                    output.AppendLine($"flat out vec4 {names[register]};");
                    handoff.AppendLine($"    {names[register]} = {registers[register]};");
                    pixel = InsertAfterPrecision(pixel, $"flat in vec4 {names[register]};\n");
                }
                pixelValues.Add(member.Name, names);
            }
            vertex = InsertAfterPrecision(vertex, output.ToString());
            vertex += "\nvoid main(){ particleGeometry(); particleFeed();\n" + assignments + handoff + "    gameParticleMain();\n}\n";
            pixel = RewriteMembers(pixel, program.Pixel.Sidecar, pixelValues);
            pixel = WithScreenCopy(pixel);
            return program with
            {
                Vertex = program.Vertex with { Glsl = vertex },
                Pixel = program.Pixel with { Glsl = pixel }
            };
        }

        /// <summary>
        /// Screen textures captured from the GL framebuffer (scene colour and depth). Shaders address them
        /// with D3D screen UVs (origin top-left), so UV-based reads flip V; texelFetch at gl_FragCoord is
        /// already in GL space and stays untouched.
        /// </summary>
        private static readonly (string Sampler, string Helper)[] ScreenTextures =
        {
            ("SAMPLER_BACK_BUFFER_COPY_SharedTexture", "particleScreenCopy"),
            ("sDepthTexture_SharedTexture", "particleScreenDepth")
        };

        internal static string WithScreenCopy(string source)
        {
            foreach ((string sampler, string helper) in ScreenTextures)
            {
                string declaration = "uniform highp sampler2D " + sampler + ";";
                if (!source.Contains(declaration, StringComparison.Ordinal))
                    continue;
                string helpers = "\nvec4 " + helper + "(vec2 at){ return texture(" + sampler + ", vec2(at.x, 1.0-at.y)); }" +
                    "\nvec4 " + helper + "Lod(vec2 at, float lod){ return textureLod(" + sampler + ", vec2(at.x, 1.0-at.y), lod); }\n";
                source = source.Replace("textureLod(" + sampler + ", ", helper + "Lod(")
                    .Replace("texture(" + sampler + ", ", helper + "(")
                    .Replace(declaration, declaration + helpers);
            }
            return source;
        }

        private static string[] Identity() => new[] { "vec4(1.0,0.0,0.0,0.0)", "vec4(0.0,1.0,0.0,0.0)", "vec4(0.0,0.0,1.0,0.0)", "vec4(0.0,0.0,0.0,1.0)" };

        private const string MeshUvHelpers = @"
void particleUvRows(vec2 scale, float angle, vec2 offset, vec2 center,
                    ivec2 flip, vec2 cell, vec2 divisions, out vec4 rowU, out vec4 rowV){
    float cosine = cos(angle); float sine = sin(angle);
    vec2 along = vec2(scale.x * cosine, -scale.y * sine);
    vec2 across = vec2(scale.x * sine, scale.y * cosine);
    vec2 mirrored = vec2(1.0) - vec2(flip) * 2.0;
    vec2 origin = center + offset - vec2(dot(along, center), dot(across, center));
    origin = origin * mirrored + vec2(flip) + cell;
    vec2 div = max(round(divisions), vec2(1.0));
    rowU = vec4(along * mirrored.x, origin.x, 0.0) / div.x;
    rowV = vec4(across * mirrored.y, origin.y, 0.0) / div.y;
}
";

        private static string Helpers(bool mesh) => (mesh ? "uniform vec4 uColor;\nuniform float uErosionDrive;\n" + MeshUvHelpers : string.Empty) + @"
uniform int uGamePremultiplied;
uniform int uHasTexMult;
uniform int uColorLookUpTypeX;
uniform int uColorLookUpTypeY;
uniform vec2 uColorLookUpScales;
uniform vec2 uColorLookUpOffsets;
uniform float uErosionSliceWidth;
uniform float uErosionFeatherIn;
uniform float uErosionFeatherOut;
float particleLookupDriver(int type){
    if (type == 1) return vColorDynamics.x;
    if (type == 2) return vColorDynamics.y;
    if (type == 3) return vColorDynamics.z;
    return 1.0;
}
void particleFeed(){
    particleBaseUv = (vCell + vLocalUv) / max(round(uTexDiv), vec2(1.0));
    particleMultUv = (vCellMult + vLocalUvMult) / max(round(uTexDivMult), vec2(1.0));
    particleLookup = vec2(particleLookupDriver(uColorLookUpTypeX), particleLookupDriver(uColorLookUpTypeY)) * uColorLookUpScales;
    if (uColorLookUpTypeX != 0) particleLookup.x += uColorLookUpOffsets.x;
    if (uColorLookUpTypeY != 0) particleLookup.y += uColorLookUpOffsets.y;
" + (mesh ? @"
    particleUvRows(uUvScale, uUvRotation, uBirthUvOffset + uEmitterUvOffset, uUvTransformCenter,
                   ivec2(uFlipU, uFlipV), vCell, uTexDiv, particleUvRowU, particleUvRowV);
    particleUvRows(uUvScaleMult, uUvRotationMult, uUvOffsetMult + uEmitterUvOffsetMult, uUvTransformCenterMult,
                   ivec2(uFlipUMult, uFlipVMult), vCellMult, uTexDivMult, particleMultRowU, particleMultRowV);
    particleTint = uColor;
    particleErosion = uErosionDrive;
" : @"
    // Ribbon geometry repacks the driver lanes and carries the resolved lookup separately.
    if (uPrimitiveKind == 5 || uPrimitiveKind == 6 || uPrimitiveKind == 8 || uPrimitiveKind == 10)
        particleLookup = vRibbonLookup;
    particleTint = vColor;
    particleErosion = vErosionDrive;
") + @"
    if (uGamePremultiplied != 0) { particleTint.rgb *= particleTint.a; particleTint.a = 1.0; }
}
";

        private static string InsertAfterPrecision(string source, string code)
        {
            int at = source.LastIndexOf("precision ", StringComparison.Ordinal);
            if (at < 0) at = source.IndexOf('\n');
            else at = source.IndexOf(';', at);
            return source.Insert(at + 1, "\n" + code + "\n");
        }

        private static string RewriteMembers(string source, GameShaderTranslator.ShaderSidecar sidecar, IReadOnlyDictionary<string, string[]> values)
        {
            var definitions = new StringBuilder();
            foreach (var block in sidecar.Blocks)
            {
                var selected = block.Members.Where(member => values.ContainsKey(member.Name)).ToArray();
                if (selected.Length == 0) continue;
                // The translator normalizes each cbuffer to a register array with this instance name.
                var declaration = Regex.Match(source, @"uniform\s+" + Regex.Escape(block.GlslName) + @"\s*\{[\s\S]*?\}\s*(\w+)\s*;");
                if (!declaration.Success) continue;
                string instance = declaration.Groups[1].Value;
                string accessor = "particleRead_" + block.GlslName;
                // A block the shader also bit-casts is declared as uvec4 (or ivec4); the accessor keeps that
                // type and stores the float values as their bits.
                string type = Regex.Match(declaration.Value, @"\b(uvec4|ivec4|vec4)\s+m\s*\[").Groups[1].Value;
                if (type.Length == 0) type = "vec4";
                string bits = type == "uvec4" ? "floatBitsToUint" : type == "ivec4" ? "floatBitsToInt" : string.Empty;
                source = Regex.Replace(source, Regex.Escape(instance) + @"\.m\[([^\]\r\n]+)\]", accessor + "(int($1))");
                source = source.Insert(declaration.Index + declaration.Length, $"\n{type} {accessor}(int index);\n");
                definitions.AppendLine($"{type} {accessor}(int index){{ {type} value = {instance}.m[index];");
                foreach (var member in selected)
                {
                    string[] expressions = values[member.Name];
                    for (int lane = 0; lane < member.Size / 4; lane++)
                    {
                        int register = lane / 4;
                        if (register >= expressions.Length) break;
                        int offset = (int)member.Offset / 4 + lane;
                        string target = "xyzw"[offset % 4].ToString();
                        string component = "xyzw"[lane % 4].ToString();
                        definitions.AppendLine($"if(index == {offset / 4}) value.{target} = {bits}(({expressions[register]}).{component});");
                    }
                }
                definitions.AppendLine("return value; }");
            }
            return source + "\n" + definitions;
        }
    }
}
