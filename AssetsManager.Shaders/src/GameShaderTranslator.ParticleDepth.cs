using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace AssetsManager.Shaders;

public static partial class GameShaderTranslator
{
    private static (string Source, bool Changed) OrthographicParticleDepth(string source, ShaderReflectionData reflection)
    {
        if (!reflection.ConstantBuffers.Any(buffer =>
                buffer.Members.Any(member => member.Name == "cSoftParticleParams") &&
                buffer.Members.Any(member => member.Name == "cSoftParticleControl")))
            return (source, false);
        var frame = reflection.ConstantBuffers.FirstOrDefault(buffer => buffer.Name == "PerFramePixelCB");
        var depth = frame?.Members.FirstOrDefault(member => member.Name == "cDepthConversionParams");
        // Restrict the adapter to the reflected particle depth contract. Authored buffers stay unchanged.
        if (depth == null || depth.Offset != 80 || depth.Size < 8)
            return (source, false);

        var declaration = Regex.Match(source,
            @"layout\(std140\) uniform PerFramePixelCB(?:_ps)?\s*\{\s*uvec4 m\[\d+\];\s*\}\s*(?<instance>\w+);",
            RegexOptions.CultureInvariant);
        if (!declaration.Success) return (source, false);
        string instance = declaration.Groups["instance"].Value;
        var loads = Regex.Matches(source,
            @"float (?<name>\w+) = uintBitsToFloat\(" + Regex.Escape(instance) + @"\.m\[5u\]\.(?<axis>[xy])\);",
            RegexOptions.CultureInvariant).Cast<Match>().ToArray();
        var offset = loads.FirstOrDefault(load => load.Groups["axis"].Value == "x");
        var slope = loads.FirstOrDefault(load => load.Groups["axis"].Value == "y");
        if (offset == null || slope == null) return (source, false);
        string a = Regex.Escape(offset.Groups["name"].Value), b = Regex.Escape(slope.Groups["name"].Value);
        var gap = new Regex(@"\(dxbcRcp\((?<scene>[^\r\n]+?) \* " + b + @" \+ " + a +
            @"\)\) - \(dxbcRcp\(gl_FragCoord\.z \* " + b + @" \+ " + a + @"\)\)", RegexOptions.CultureInvariant);
        bool changed = false;
        string result = gap.Replace(source, match =>
        {
            string scene = match.Groups["scene"].Value;
            if (!scene.StartsWith("texelFetch(sDepthTexture_SharedTexture,", StringComparison.Ordinal)) return match.Value;
            changed = true;
            return "particleDepthGap(" + scene + ", gl_FragCoord.z, " + offset.Groups["name"].Value + ", " +
                slope.Groups["name"].Value + ", uParticleOrthographicDepthSpan)";
        });
        if (!changed) return (source, false);
        return (WithHelpers(result,
            "uniform highp float uParticleOrthographicDepthSpan;\n" +
            "float dxbcRcp(float v);\n" +
            "float particleDepthGap(float scene, float here, float a, float b, float orthographicSpan) {\n" +
            "    if (orthographicSpan > 0.0) return (scene - here) * orthographicSpan;\n" +
            "    return dxbcRcp(scene * b + a) - dxbcRcp(here * b + a);\n" +
            "}\n"), true);
    }
}
