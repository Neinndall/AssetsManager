namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Minimal stock-material shader used by the MAPGEO backdrop.
    /// It mirrors LTK Manager's MeshLambertMaterial/MeshBasicMaterial split without
    /// pulling the map through the champion SceneModel material pipeline.
    /// </summary>
    internal static class MapGeometryShaderSource
    {
        internal const string Vertex = @"
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec3 aNormal;
layout(location = 2) in vec2 aUv0;
layout(location = 3) in vec2 aUv1;

uniform mat4 uViewProjection;

out vec3 vNormal;
out vec2 vUv;
out vec2 vUv1;

void main()
{
    vec3 position = vec3(-aPosition.x, aPosition.y, aPosition.z);
    vNormal = normalize(vec3(-aNormal.x, aNormal.y, aNormal.z));
    vUv = aUv0;
    vUv1 = aUv1;
    gl_Position = uViewProjection * vec4(position, 1.0);
}";

        internal const string Fragment = @"
in vec3 vNormal;
in vec2 vUv;
in vec2 vUv1;

uniform sampler2D uBaseTexture;
uniform sampler2D uBakedLight;
uniform sampler2D uStationaryLight;
uniform vec3 uColor;
uniform float uOpacity;
uniform float uAlphaTest;
uniform vec2 uUvRepeat;
uniform int uLit;
uniform int uPremultipliedAlpha;
uniform vec3 uLightDirection;
uniform vec3 uSunColor;
uniform float uSunStrength;
uniform vec3 uSkyColor;
uniform vec3 uGroundColor;
uniform vec3 uHorizonColor;
uniform float uAmbientStrength;
uniform float uLightMapColorScale;
uniform int uHasBakedLight;
uniform int uHasStationaryLight;
uniform vec2 uBakedLightScale;
uniform vec2 uBakedLightBias;
uniform vec2 uStationaryLightScale;
uniform vec2 uStationaryLightBias;
uniform int uWireframePass;
uniform vec4 uWireframeColor;

out vec4 FragColor;

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

    vec4 texel = texture(uBaseTexture, vUv * uUvRepeat);
    float alpha = texel.a * uOpacity;
    if (uAlphaTest > 0.0 && alpha < uAlphaTest)
        discard;

    vec3 color = texel.rgb * uColor;
    if (uLit != 0)
    {
        vec3 normal = normalize(vNormal);
        float sun = max(dot(normal, normalize(uLightDirection)), 0.0);
        float up = clamp(normal.y, 0.0, 1.0);
        float down = clamp(-normal.y, 0.0, 1.0);
        float side = 1.0 - max(up, down);
        vec3 ambientColor = uSkyColor * up + uGroundColor * down + uHorizonColor * side;
        vec3 lighting = (uSunColor * sun * uSunStrength) + (ambientColor * uAmbientStrength);

        if (uHasBakedLight != 0)
        {
            vec2 lightUv = vUv1 * uBakedLightScale + uBakedLightBias;
            lighting += texture(uBakedLight, lightUv).rgb * uLightMapColorScale;
        }
        if (uHasStationaryLight != 0)
        {
            vec2 lightUv = vUv1 * uStationaryLightScale + uStationaryLightBias;
            lighting += texture(uStationaryLight, lightUv).rgb;
        }

        color *= lighting;
    }

    if (uPremultipliedAlpha != 0)
        color *= alpha;

    FragColor = vec4(linearToSrgb(color), alpha);
}";
    }
}
