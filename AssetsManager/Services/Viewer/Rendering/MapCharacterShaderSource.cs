namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Stock character material shader for MAP structures. It mirrors the Lambert/Basic split
    /// used by current LTK Manager MAIN without enabling AssetsManager's champion-only shader effects.
    /// </summary>
    internal static class MapCharacterShaderSource
    {
        internal const int MaximumBones = 512;

        internal const string Vertex = @"
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUv;
layout(location = 3) in vec4 aTangent;
layout(location = 5) in vec4 aSkinIndex;
layout(location = 6) in vec4 aSkinWeight;

uniform mat4 uViewProjection;
uniform mat4 uWorld;
uniform int uUseSkinning;

const int MAX_BONES = 512;
layout(std140) uniform BoneTransforms {
    mat4 uBoneTransforms[MAX_BONES];
};

uniform vec3 uLightDirection;
uniform vec3 uSunColor;
uniform float uSunStrength;
uniform vec3 uSkyColor;
uniform vec3 uGroundColor;
uniform float uAmbientStrength;
out vec3 vStockLightFront;
out vec3 vStockLightBack;

vec3 stockLight(vec3 normal)
{
    vec3 ambient = mix(uGroundColor, uSkyColor, normal.y * 0.5 + 0.5);
    float sun = max(dot(normal, normalize(uLightDirection)), 0.0);
    return uSunColor * sun * uSunStrength + ambient * uAmbientStrength;
}

out vec3 vNormal;
out vec4 vPbrTangent;
out vec3 vWorldPosition;
uniform vec3 uLightGridCube[6];
out vec3 vLightGrid;
out vec2 vUv;

void main()
{
    vec3 position = aPosition;
    vec3 normal = aNormal;
    vec3 tangent = aTangent.xyz;
    if (uUseSkinning != 0)
    {
        ivec4 joints = ivec4(aSkinIndex + vec4(0.5));
        mat4 skin =
            uBoneTransforms[joints.x] * aSkinWeight.x +
            uBoneTransforms[joints.y] * aSkinWeight.y +
            uBoneTransforms[joints.z] * aSkinWeight.z +
            uBoneTransforms[joints.w] * aSkinWeight.w;
        position = (skin * vec4(position, 1.0)).xyz;
        normal = mat3(skin) * normal;
        tangent = mat3(skin) * tangent;
    }

    vec4 worldPosition = uWorld * vec4(position, 1.0);
    mat3 normalMatrix = transpose(inverse(mat3(uWorld)));
    vNormal = normalize(normalMatrix * normal);
    tangent = mat3(uWorld) * tangent;
    vPbrTangent = vec4(tangent / max(length(tangent), 1e-6), aTangent.w * sign(determinant(mat3(uWorld))));
    vWorldPosition = worldPosition.xyz;
    vStockLightFront = stockLight(vNormal);
    vStockLightBack = stockLight(-vNormal);
    vec3 gridWeight = vNormal * vNormal;
    vLightGrid = gridWeight.x * uLightGridCube[vNormal.x < 0.0 ? 1 : 0]
        + gridWeight.y * uLightGridCube[vNormal.y < 0.0 ? 3 : 2]
        + gridWeight.z * uLightGridCube[vNormal.z < 0.0 ? 5 : 4];
    vUv = aUv;
    gl_Position = uViewProjection * worldPosition;
}";

        internal const string Fragment = @"
in vec3 vStockLightFront;
in vec3 vStockLightBack;
in vec2 vUv;
in vec3 vNormal;
in vec3 vWorldPosition;

uniform sampler2D uBaseTexture;
uniform int uHasTexture;
uniform vec3 uColor;
uniform float uOpacity;
uniform float uAlphaTest;
uniform vec2 uUvRepeat;
uniform vec2 uUvOffset;
uniform int uLit;
uniform int uPremultipliedAlpha;

uniform int uWireframePass;
uniform vec4 uWireframeColor;
uniform float uSelfIllumination;
uniform int uLightGridOn;
uniform float uLightGridFullBright;
in vec3 vLightGrid;

out vec4 FragColor;

vec3 srgbToLinear(vec3 value)
{
    vec3 low = value / 12.92;
    vec3 high = pow((value + 0.055) / 1.055, vec3(2.4));
    return mix(high, low, lessThanEqual(value, vec3(0.04045)));
}

vec3 linearToSrgb(vec3 value)
{
    value = max(value, vec3(0.0));
    vec3 low = value * 12.92;
    vec3 high = 1.055 * pow(value, vec3(1.0 / 2.4)) - 0.055;
    return mix(high, low, lessThanEqual(value, vec3(0.0031308)));
}

" + CharacterPbrShaderSource.Fragment + @"
void main()
{
    if (uWireframePass != 0)
    {
        FragColor = uWireframeColor;
        return;
    }

    vec2 materialUv = vUv * uUvRepeat + uUvOffset;
    vec4 texel = uHasTexture != 0
        ? texture(uBaseTexture, materialUv)
        : vec4(1.0);
    float alpha = texel.a * uOpacity;
    if (uAlphaTest > 0.0 && alpha < uAlphaTest)
        discard;

    vec3 color = texel.rgb * srgbToLinear(uColor);
    if (uLit != 0)
    {
        float emissiveMask = uHasTexture != 0 ? characterEmissiveMask(materialUv) : 0.0;
        if (uPbrOn != 0)
            color = shadeCharacterPbr(color, vNormal, vWorldPosition, vUv);
        else
            color *= uLightGridOn != 0
                ? srgbToLinear(clamp(vLightGrid * (1.0 - emissiveMask) + vec3(emissiveMask + uSelfIllumination * uLightGridFullBright), 0.0, 1.0))
                : (gl_FrontFacing ? vStockLightFront : vStockLightBack);
        if (uPbrOn != 0 || uLightGridOn == 0)
            color = mix(color, texel.rgb * srgbToLinear(uColor), emissiveMask);
    }

    if (uPremultipliedAlpha != 0)
        color *= alpha;

    FragColor = vec4(linearToSrgb(color), alpha);
}";
    }
}
