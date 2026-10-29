namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>Texture-only LIT_UBER preview, using the reference's Three r185 GGX and multiscattering model.</summary>
    internal static class CharacterPbrShaderSource
    {
        internal const string Fragment = @"
in vec4 vPbrTangent;
uniform int uCharacterEmissiveOn;
uniform sampler2D uCharacterEmissive;
float characterEmissiveMask(vec2 uv) {
    return uCharacterEmissiveOn != 0 ? texture(uCharacterEmissive, uv).g : 0.0;
}
uniform int uPbrOn;
uniform int uPbrNormalOn;
uniform sampler2D uPbrRma;
uniform sampler2D uPbrNormal;
uniform sampler2D uPbrDfg;
uniform vec3 uPbrCamera;
uniform mat4 uPbrView;
uniform int uPbrOrthographic;
uniform vec3 uPbrLightDirection;
uniform vec3 uPbrLightColor;
uniform vec3 uPbrLightDirection2;
uniform vec3 uPbrLightColor2;
uniform vec3 uPbrSky;
uniform vec3 uPbrGround;
uniform float uPbrAmbientStrength;
const float PBR_PI = 3.141592653589793;

vec2 pbrDfg(float roughness, float cosine) {
    return texture(uPbrDfg, vec2(roughness, clamp(cosine, 0.0, 1.0))).rg;
}
vec3 pbrSingle(vec3 f0, vec2 fab) { return f0 * fab.x + fab.y; }
vec3 pbrMulti(vec3 f0, vec2 fab) {
    float missing = 1.0 - fab.x - fab.y;
    vec3 average = f0 + (1.0 - f0) * 0.047619;
    return pbrSingle(f0, fab) * average / (1.0 - missing * average) * missing;
}
vec3 pbrDirect(vec3 n, vec3 v, vec3 l, vec3 light, vec3 diffuse, vec3 f0, float roughness) {
    float nl = max(dot(n, l), 0.0);
    float nv = max(dot(n, v), 0.0);
    vec3 halfVector = l + v;
    vec3 h = halfVector / max(length(halfVector), 1e-6);
    float nh = max(dot(n, h), 0.0);
    float vh = max(dot(v, h), 0.0);
    float alpha = roughness * roughness;
    float a2 = alpha * alpha;
    float gv = nl * sqrt(a2 + (1.0 - a2) * nv * nv);
    float gl = nv * sqrt(a2 + (1.0 - a2) * nl * nl);
    float visibility = 0.5 / max(gv + gl, 1e-6);
    float denominator = nh * nh * (a2 - 1.0) + 1.0;
    float distribution = a2 / (PBR_PI * denominator * denominator);
    float fresnel = exp2((-5.55473 * vh - 6.98316) * vh);
    vec3 specular = mix(f0, vec3(1.0), fresnel) * visibility * distribution;
    vec2 dfgV = pbrDfg(roughness, nv);
    vec2 dfgL = pbrDfg(roughness, nl);
    float lost = (1.0 - dfgV.x - dfgV.y) * (1.0 - dfgL.x - dfgL.y);
    vec3 average = f0 + (1.0 - f0) * 0.047619;
    specular += pbrSingle(f0, dfgV) * pbrSingle(f0, dfgL) * average
        / (1.0 - lost * average + vec3(1e-6)) * lost;
    return nl * light * (diffuse / PBR_PI + specular);
}
vec3 shadeCharacterPbr(vec3 albedo, vec3 sourceNormal, vec3 position, vec2 uv) {
    float face = gl_FrontFacing ? 1.0 : -1.0;
    vec3 unperturbed = normalize(sourceNormal) * face;
    vec3 n = unperturbed;
    if (uPbrNormalOn != 0) {
        vec3 t = normalize(vPbrTangent.xyz) * face;
        vec3 b = normalize(cross(normalize(sourceNormal), normalize(vPbrTangent.xyz))) * vPbrTangent.w * face;
        vec2 xy = texture(uPbrNormal, uv).xy * 2.0 - 1.0;
        vec3 mapN = vec3(xy, sqrt(clamp(1.0 - dot(xy, xy), 0.0, 1.0)));
        n = normalize(mat3(t, b, unperturbed) * mapN);
    }
    vec3 rma = texture(uPbrRma, uv).rgb;
    vec3 geometryNormal = normalize(mat3(uPbrView) * unperturbed);
    vec3 dxy = max(abs(dFdx(geometryNormal)), abs(dFdy(geometryNormal)));
    float roughness = min(max(rma.r, 0.0525) + max(max(dxy.x, dxy.y), dxy.z), 1.0);
    float metalness = rma.g;
    vec3 viewVector = uPbrOrthographic != 0
        ? transpose(mat3(uPbrView)) * vec3(0.0, 0.0, 1.0) : uPbrCamera - position;
    vec3 v = viewVector / max(length(viewVector), 1e-6);
    vec3 diffuse = albedo * (1.0 - metalness);
    vec3 f0 = mix(vec3(0.04), albedo, metalness);
    vec3 direct = pbrDirect(n, v, uPbrLightDirection, uPbrLightColor, diffuse, f0, roughness)
        + pbrDirect(n, v, uPbrLightDirection2, uPbrLightColor2, diffuse, f0, roughness);
    vec3 ambient = mix(uPbrGround, uPbrSky, n.y * 0.5 + 0.5) * uPbrAmbientStrength / PBR_PI;
    if (uLightGridOn != 0)
        ambient = srgbToLinear(clamp(vLightGrid + vec3(uSelfIllumination * uLightGridFullBright), 0.0, 1.0));
    vec2 fab = pbrDfg(roughness, dot(n, v));
    vec3 singleDielectric = pbrSingle(vec3(0.04), fab);
    vec3 multiDielectric = pbrMulti(vec3(0.04), fab);
    vec3 single = mix(singleDielectric, pbrSingle(albedo, fab), metalness);
    vec3 multi = mix(multiDielectric, pbrMulti(albedo, fab), metalness);
    vec3 indirect = ambient * (single + multi + diffuse * (1.0 - singleDielectric - multiDielectric));
    return direct + indirect * rma.b;
}
";
    }
}
