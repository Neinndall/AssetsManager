using System;

namespace AssetsManager.Services.Viewer.Rendering.Core
{
    internal static class GlMeshShaderSource
    {
        internal const int PortableAuxiliaryTextureCount = 0;
        internal const int MaximumAuxiliaryTextureCount = 0;

        internal const string Vertex = @"
                    layout(location=0) in vec3 aPos;
                    layout(location=1) in vec3 aNormal;
                    layout(location=2) in vec2 aUv;
                    layout(location=5) in vec4 aBoneIndices;
                    layout(location=6) in vec4 aBoneWeights;
                    uniform mat4 uViewProj;
                    uniform mat4 uWorld;
                    uniform int uUseSkinning;
                    const int MAX_BONES = 512;
                    layout(std140) uniform BoneTransforms {
                        mat4 uBoneTransforms[MAX_BONES];
                    };
                    out vec3 vNormal;
                    out vec3 vWorldPosition;
                    out vec2 vUv;
                    void main(){
                            vec3 animatedPosition = aPos;
                            vec3 animatedNormal = aNormal;
                            if (uUseSkinning != 0)
                            {
                                ivec4 boneIndices = ivec4(aBoneIndices + vec4(0.5));
                                mat4 skinMatrix =
                                    uBoneTransforms[boneIndices.x] * aBoneWeights.x +
                                    uBoneTransforms[boneIndices.y] * aBoneWeights.y +
                                    uBoneTransforms[boneIndices.z] * aBoneWeights.z +
                                    uBoneTransforms[boneIndices.w] * aBoneWeights.w;
                                animatedPosition = (skinMatrix * vec4(aPos, 1.0)).xyz;
                                animatedNormal = mat3(skinMatrix) * aNormal;
                            }
                            vec4 worldPos = uWorld * vec4(animatedPosition, 1.0);
                            gl_Position = uViewProj * worldPos;
                            vNormal = normalize(mat3(uWorld) * animatedNormal);
                            vWorldPosition = worldPos.xyz;
                            vUv = aUv;
                    }";

        internal const string Fragment = @"
                    in vec3 vNormal;
                    in vec3 vWorldPosition;
                    in vec2 vUv;
                    uniform sampler2D uTex;
                    uniform float uEffectTime;
                    uniform vec4 uColorTint;
                    uniform float uAlphaCutoff;
                    uniform vec2 uMaterialUvRepeat;
                    uniform vec2 uMaterialUvScroll;
                    uniform int uMaterialUnlit;
                    uniform int uMaterialPremultipliedAlpha;
                    uniform int uMaterialSrgb;
                    uniform int uMaterialUsesTextureAlpha;
                    uniform int uWireframePass;
                    uniform vec4 uWireframeColor;
                    uniform vec3 uLightDir;
                    uniform vec3 uLightColor;
                    uniform vec3 uLightDir2;
                    uniform vec3 uLightColor2;
                    uniform vec3 uAmbient;
                    out vec4 fragColor;
                    vec3 srgbToLinear(vec3 value){
                        vec3 low = value / 12.92;
                        vec3 high = pow((value + 0.055) / 1.055, vec3(2.4));
                        return mix(high, low, lessThanEqual(value, vec3(0.04045)));
                    }
                    vec3 linearToSrgb(vec3 value){
                        value = max(value, vec3(0.0));
                        vec3 low = value * 12.92;
                        vec3 high = 1.055 * pow(value, vec3(1.0 / 2.4)) - 0.055;
                        return mix(high, low, lessThanEqual(value, vec3(0.0031308)));
                    }
                    vec4 readBaseTexture(vec2 uv){
                        return texture(uTex, uv);
                    }
                    void main(){
                            if (uWireframePass != 0)
                            {
                                fragColor = uWireframeColor;
                                return;
                            }
                            vec2 materialUv = vUv * uMaterialUvRepeat + uMaterialUvScroll * uEffectTime;
                            vec4 texColor = readBaseTexture(materialUv);
                            float coverageAlpha = (uMaterialUsesTextureAlpha != 0 ? texColor.a : 1.0) * uColorTint.a;
                            if (coverageAlpha <= 0.0) discard;
                            if (uAlphaCutoff > 0.0 && coverageAlpha < uAlphaCutoff) discard;
                            vec3 tintRgb = uMaterialSrgb != 0
                                ? srgbToLinear(uColorTint.rgb)
                                : uColorTint.rgb;
                            texColor.rgb *= tintRgb;
                            texColor.a = coverageAlpha;
                            float diff1 = max(dot(vNormal, uLightDir), 0.0);
                            float diff2 = max(dot(vNormal, uLightDir2), 0.0);
                            vec3 finalLight = clamp(uAmbient + diff1 * uLightColor + diff2 * uLightColor2, 0.0, 1.0);
                            vec3 finalColor = uMaterialUnlit != 0
                                ? texColor.rgb
                                : texColor.rgb * finalLight;

                            if (uAlphaCutoff > 0.0 && texColor.a < uAlphaCutoff) discard;
                            if (uMaterialPremultipliedAlpha != 0)
                                finalColor *= texColor.a;
                            if (uMaterialSrgb != 0)
                                finalColor = linearToSrgb(finalColor);
                            fragColor = vec4(finalColor, texColor.a);
                    }";

        internal static string CreateVertex(int auxiliaryTextureCount = 0) => Vertex;

        internal static string CreateFragment(int auxiliaryTextureCount = 0) => Fragment;
    }
}
