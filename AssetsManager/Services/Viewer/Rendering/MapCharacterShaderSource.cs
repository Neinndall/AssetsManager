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
layout(location = 5) in vec4 aSkinIndex;
layout(location = 6) in vec4 aSkinWeight;

uniform mat4 uViewProjection;
uniform mat4 uWorld;
uniform int uUseSkinning;

const int MAX_BONES = 512;
layout(std140) uniform BoneTransforms {
    mat4 uBoneTransforms[MAX_BONES];
};

out vec3 vNormal;
out vec2 vUv;

void main()
{
    vec3 position = aPosition;
    vec3 normal = aNormal;
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
    }

    vec4 worldPosition = uWorld * vec4(position, 1.0);
    mat3 normalMatrix = transpose(inverse(mat3(uWorld)));
    vNormal = normalize(normalMatrix * normal);
    vUv = aUv;
    gl_Position = uViewProjection * worldPosition;
}";

        internal const string Fragment = @"
in vec3 vNormal;
in vec2 vUv;

uniform sampler2D uBaseTexture;
uniform int uHasTexture;
uniform vec3 uColor;
uniform float uOpacity;
uniform float uAlphaTest;
uniform vec2 uUvRepeat;
uniform vec2 uUvOffset;
uniform int uLit;
uniform int uPremultipliedAlpha;
uniform vec3 uLightDirection;
uniform int uWireframePass;
uniform vec4 uWireframeColor;
uniform float uSelfIllumination;

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

void main()
{
    if (uWireframePass != 0)
    {
        FragColor = uWireframeColor;
        return;
    }

    vec4 texel = uHasTexture != 0
        ? texture(uBaseTexture, vUv * uUvRepeat + uUvOffset)
        : vec4(1.0);
    float alpha = texel.a * uOpacity;
    if (uAlphaTest > 0.0 && alpha < uAlphaTest)
        discard;

    vec3 color = texel.rgb * srgbToLinear(uColor);
    if (uLit != 0)
    {
        float sun = max(dot(normalize(vNormal), normalize(uLightDirection)), 0.0);
        color *= clamp(0.6 + 0.4 * sun + uSelfIllumination, 0.0, 1.0);
    }

    if (uPremultipliedAlpha != 0)
        color *= alpha;

    FragColor = vec4(linearToSrgb(color), alpha);
}";
    }
}
