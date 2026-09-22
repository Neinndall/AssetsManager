namespace AssetsManager.Services.Viewer.Rendering.Map
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

void main()
{
    vec3 position = vec3(-aPosition.x, aPosition.y, aPosition.z);
    vNormal = normalize(vec3(-aNormal.x, aNormal.y, aNormal.z));
    vUv = aUv0;
    gl_Position = uViewProjection * vec4(position, 1.0);
}";

        internal const string Fragment = @"
in vec3 vNormal;
in vec2 vUv;

uniform sampler2D uBaseTexture;
uniform vec3 uColor;
uniform float uOpacity;
uniform float uAlphaTest;
uniform vec2 uUvRepeat;
uniform int uLit;
uniform int uPremultipliedAlpha;
uniform vec3 uLightDirection;

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
    vec4 texel = texture(uBaseTexture, vUv * uUvRepeat);
    float alpha = texel.a * uOpacity;
    if (uAlphaTest > 0.0 && alpha < uAlphaTest)
        discard;

    vec3 color = texel.rgb * uColor;
    if (uLit != 0)
    {
        float sun = max(dot(normalize(vNormal), normalize(uLightDirection)), 0.0);
        color *= 0.6 + 0.4 * sun;
    }

    if (uPremultipliedAlpha != 0)
        color *= alpha;

    FragColor = vec4(linearToSrgb(color), alpha);
}";
    }
}
