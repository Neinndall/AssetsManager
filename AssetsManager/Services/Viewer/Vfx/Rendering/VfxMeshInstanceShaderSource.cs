using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace AssetsManager.Services.Viewer.Vfx.Rendering;

/// <summary>Shares the mesh geometry contract between scalar diagnostic draws and interleaved GPU instances.</summary>
internal static class VfxMeshInstanceShaderSource
{
    internal const int Stride = 40;
    internal static readonly string Vertex;
    internal static readonly string Fragment;
    static VfxMeshInstanceShaderSource() { Vertex = ComposeVertex(); Fragment = ComposeFragment(); }

    private static readonly IReadOnlyDictionary<string, string> Fields = new Dictionary<string, string>
    {
        ["uWorldPos"] = "meshVec3(0)", ["uScale"] = "meshVec3(3)",
        ["uFrame"] = "meshLane(10)", ["uGameLookupDrivers"] = "meshVec3(11)",
        ["uPlacementRight"] = "meshVec3(14)", ["uPlacementUp"] = "meshVec3(17)",
        ["uPlacementForward"] = "meshVec3(20)", ["uBirthUvOffset"] = "meshVec2(23)",
        ["uUvScale"] = "meshVec2(25)", ["uUvRotation"] = "meshLane(27)",
        ["uUvOffsetMult"] = "meshVec2(28)", ["uUvScaleMult"] = "meshVec2(30)",
        ["uUvRotationMult"] = "meshLane(32)"
    };

    internal static string InstanceReads(string body)
    {
        foreach (var field in Fields)
            body = Regex.Replace(body, @"\b" + field.Key + @"\b", "(uMeshInstanced != 0 ? " + field.Value + " : " + field.Key + ")");
        return body;
    }

    private static string ComposeVertex()
    {
        string source = VfxShaderSource.MeshVertex;
        int main = source.IndexOf("void main(){", System.StringComparison.Ordinal);
        var declarations = new StringBuilder("\nuniform int uMeshInstanced;\nuniform vec4 uColor;\nuniform float uErosionDrive;\nuniform vec4 uErosionMixer;\n");
        for (int index = 0; index < 10; index++)
            declarations.AppendLine($"layout(location={index + 6}) in vec4 aMesh{index};");
        declarations.AppendLine("out vec4 vInstanceTint;\nout float vInstanceErosion;\nout vec4 vInstanceMixer;");
        declarations.AppendLine("float meshLane(int lane){ int at = lane % 4;");
        for (int index = 0; index < 9; index++)
            declarations.AppendLine($"if (lane < {(index + 1) * 4}) return aMesh{index}[at];");
        declarations.AppendLine("return aMesh9[at]; }\nvec2 meshVec2(int at){ return vec2(meshLane(at), meshLane(at+1)); }\nvec3 meshVec3(int at){ return vec3(meshLane(at), meshLane(at+1), meshLane(at+2)); }");
        string body = source[main..]
            .Replace("ivec4 boneIndices = ivec4(aBoneIndices + vec4(0.5));",
                "ivec4 boneIndices = ivec4(aBoneIndices + vec4(0.5));\n        if (uMeshInstanced != 0) boneIndices += ivec4(int(meshLane(38)));")
            .Replace("if (uAlignPitchToCamera != 0 || uAlignYawToCamera != 0)",
                "if (uMeshInstanced == 0 && (uAlignPitchToCamera != 0 || uAlignYawToCamera != 0))");
        body = InstanceReads(body).Replace("void main(){", @"void main(){
    vInstanceTint = uMeshInstanced != 0 ? vec4(meshLane(6), meshLane(7), meshLane(8), meshLane(9)) : uColor;
    vInstanceErosion = uMeshInstanced != 0 ? meshLane(33) : uErosionDrive;
    vInstanceMixer = uMeshInstanced != 0 ? vec4(meshLane(34), meshLane(35), meshLane(36), meshLane(37)) : uErosionMixer;");
        return source[..main] + declarations + body;
    }

    private static string ComposeFragment()
    {
        string source = VfxShaderSource.MeshFragment;
        // Keep the uniform declarations for scalar callers; the interpolants select the per-instance values.
        int body = source.IndexOf("float colorLookUpDriver", System.StringComparison.Ordinal);
        if (body < 0) body = source.IndexOf("void main", System.StringComparison.Ordinal);
        string reads = source[body..];
        reads = Regex.Replace(reads, @"\buColor\b", "vInstanceTint");
        reads = Regex.Replace(reads, @"\buErosionDrive\b", "vInstanceErosion");
        reads = Regex.Replace(reads, @"\buErosionMixer\b", "vInstanceMixer");
        return source[..body] + "\nin vec4 vInstanceTint;\nin float vInstanceErosion;\nin vec4 vInstanceMixer;\n" + reads;
    }
}
