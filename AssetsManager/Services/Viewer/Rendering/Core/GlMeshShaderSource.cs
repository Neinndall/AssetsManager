using System;
using System.Text;

namespace AssetsManager.Services.Viewer.Rendering.Core
{
    internal static class GlMeshShaderSource
    {
        internal const int PortableAuxiliaryTextureCount = 14;
        internal const int MaximumAuxiliaryTextureCount = 20;

        internal const string Vertex = @"
                    layout(location=0) in vec3 aPos;
                    layout(location=1) in vec3 aNormal;
                    layout(location=2) in vec2 aUv;
                    layout(location=3) in vec2 aLightmapUv;
                    layout(location=4) in vec4 aColor;
                    layout(location=5) in vec4 aBoneIndices;
                    layout(location=6) in vec4 aBoneWeights;
                    uniform mat4 uViewProj;
                    uniform mat4 uWorld;
                    uniform int uUseSkinning;
                    uniform int uHasVertexColor;
                    uniform int uEffectKind;
                    uniform float uEffectTime;
                    uniform sampler2D uAuxTex0;
                    uniform sampler2D uAuxTex1;
                    uniform sampler2D uAuxTex2;
                    uniform sampler2D uAuxTex3;
                    uniform sampler2D uAuxTex4;
                    uniform sampler2D uAuxTex5;
                    uniform sampler2D uAuxTex6;
                    uniform sampler2D uAuxTex7;
                    uniform sampler2D uAuxTex8;
                    uniform sampler2D uAuxTex9;
                    uniform sampler2D uAuxTex10;
                    uniform sampler2D uAuxTex11;
                    uniform sampler2D uAuxTex12;
                    uniform sampler2D uAuxTex13;
                    uniform vec3 uWaveDirection;
                    uniform float uWaveSpeed;
                    uniform float uWaveFrequency;
                    uniform float uWaveIntensity;
                    uniform int uDeformNoiseIndex;
                    uniform int uDeformMaskIndex;
                    uniform vec3 uDeformDirection;
                    uniform vec2 uDeformScrollSpeed;
                    uniform vec2 uDeformTiling;
                    uniform float uDeformSpeed;
                    uniform float uDeformFrequency;
                    uniform float uDeformIntensity;
                    uniform float uDeformProtection;
                    uniform int uDeformNoiseChannel;
                    uniform int uDeformMaskChannel;
                    const int MAX_BONES = 512;
                    layout(std140) uniform BoneTransforms {
                        mat4 uBoneTransforms[MAX_BONES];
                    };
                    out vec3 vNormal;
                    out vec3 vWorldPosition;
                    out vec2 vUv;
                    out vec2 vLightmapUv;
                    out vec4 vColor;
                    vec4 sampleAux(int index, vec2 uv){
                        if (index == -2) return vec4(0.0);
                        if (index == 0) return texture(uAuxTex0, uv);
                        if (index == 1) return texture(uAuxTex1, uv);
                        if (index == 2) return texture(uAuxTex2, uv);
                        if (index == 3) return texture(uAuxTex3, uv);
                        if (index == 4) return texture(uAuxTex4, uv);
                        if (index == 5) return texture(uAuxTex5, uv);
                        if (index == 6) return texture(uAuxTex6, uv);
                        if (index == 7) return texture(uAuxTex7, uv);
                        if (index == 8) return texture(uAuxTex8, uv);
                        if (index == 9) return texture(uAuxTex9, uv);
                        if (index == 10) return texture(uAuxTex10, uv);
                        if (index == 11) return texture(uAuxTex11, uv);
                        if (index == 12) return texture(uAuxTex12, uv);
                        if (index == 13) return texture(uAuxTex13, uv);
                        return vec4(1.0);
                    }
                    float channelValue(vec4 value, int channel){
                        if (channel == 0) return value.r;
                        if (channel == 1) return value.g;
                        if (channel == 2) return value.b;
                        if (channel == 3) return value.a;
                        return value.r;
                    }
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
                            if ((uEffectKind & 2048) != 0 && uDeformNoiseIndex >= 0 && abs(uDeformIntensity) > 0.0001)
                            {
                                vec2 deformUv = aUv * max(uDeformTiling, vec2(0.0001)) +
                                    uDeformScrollSpeed * uEffectTime;
                                float authoredNoise = channelValue(
                                    sampleAux(uDeformNoiseIndex, deformUv),
                                    uDeformNoiseChannel) * 2.0 - 1.0;
                                float authoredMask = uDeformMaskIndex >= 0
                                    ? channelValue(sampleAux(uDeformMaskIndex, aUv), uDeformMaskChannel)
                                    : 1.0;
                                vec3 normal = length(animatedNormal) > 0.0001
                                    ? normalize(animatedNormal)
                                    : vec3(0.0, 1.0, 0.0);
                                vec3 deformDirection = length(uDeformDirection) > 0.0001
                                    ? normalize(uDeformDirection)
                                    : normal;
                                vec2 phaseDirection = length(deformDirection.xy) > 0.0001
                                    ? normalize(deformDirection.xy)
                                    : vec2(0.0, 1.0);
                                float phase = dot(aUv, phaseDirection) * 6.2831853 * uDeformFrequency +
                                    uEffectTime * uDeformSpeed + authoredNoise * 3.14159265;
                                float protection = 1.0 / (1.0 + max(uDeformProtection, 0.0));
                                animatedPosition += deformDirection * sin(phase) *
                                    uDeformIntensity * authoredMask * protection;
                            }
                            if ((uEffectKind & 32) != 0 && abs(uWaveIntensity) > 0.0001)
                            {
                                vec2 waveDirection = length(uWaveDirection.xy) > 0.0001
                                    ? normalize(uWaveDirection.xy)
                                    : vec2(0.0, 1.0);
                                vec3 normal = length(animatedNormal) > 0.0001
                                    ? normalize(animatedNormal)
                                    : vec3(0.0, 1.0, 0.0);
                                float phase = dot(aUv, waveDirection) * 6.2831853 * uWaveFrequency +
                                    uEffectTime * uWaveSpeed;
                                animatedPosition += normal * sin(phase) * uWaveIntensity;
                            }
                            vec4 worldPos = uWorld * vec4(animatedPosition, 1.0);
                            gl_Position = uViewProj * worldPos;
                            vNormal = normalize(mat3(uWorld) * animatedNormal);
                            vWorldPosition = worldPos.xyz;
                            vUv = aUv;
                            vLightmapUv = aLightmapUv;
                            vColor = uHasVertexColor != 0 ? aColor : vec4(1.0);
                    }";

        internal const string Fragment = @"
                    in vec3 vNormal;
                    in vec3 vWorldPosition;
                    in vec2 vUv;
                    in vec2 vLightmapUv;
                    in vec4 vColor;
                    uniform sampler2D uTex;
                    uniform sampler2D uLightmap;
                    uniform sampler2D uAuxTex0;
                    uniform sampler2D uAuxTex1;
                    uniform sampler2D uAuxTex2;
                    uniform sampler2D uAuxTex3;
                    uniform sampler2D uAuxTex4;
                    uniform sampler2D uAuxTex5;
                    uniform sampler2D uAuxTex6;
                    uniform sampler2D uAuxTex7;
                    uniform sampler2D uAuxTex8;
                    uniform sampler2D uAuxTex9;
                    uniform sampler2D uAuxTex10;
                    uniform sampler2D uAuxTex11;
                    uniform sampler2D uAuxTex12;
                    uniform sampler2D uAuxTex13;
                    uniform int uHasLightmap;
                    uniform int uEffectKind;
                    uniform float uEffectTime;
                    uniform vec3 uCameraPosition;
                    uniform int uAdditiveTexIndex;
                    uniform int uAdditiveMaskIndex;
                    uniform vec2 uAdditiveScrollSpeed;
                    uniform vec2 uAdditiveTiling;
                    uniform vec4 uAdditiveColor;
                    uniform float uAdditiveStrength;
                    uniform int uAdditiveTextureChannel;
                    uniform int uAdditiveMaskChannel;
                    uniform int uFlowTexIndex;
                    uniform int uFlowMaskIndex;
                    uniform vec2 uFlowScrollSpeed;
                    uniform vec2 uFlowTiling;
                    uniform float uFlowStrength;
                    uniform float uFlowIntensity;
                    uniform int uFlowMaskChannel;
                    uniform int uGradientTexIndex;
                    uniform int uGradientMaskIndex;
                    uniform vec2 uGradientScrollSpeed;
                    uniform vec2 uGradientTiling;
                    uniform vec4 uGradientColor;
                    uniform float uGradientStrength;
                    uniform float uPulseRate;
                    uniform float uPulseMax;
                    uniform float uPulseOffset;
                    uniform float uGradientSharpness;
                    uniform float uGradientBloomIntensity;
                    uniform float uGradientMaskThreshold;
                    uniform float uGradientMaskSoftness;
                    uniform int uGradientTextureChannel;
                    uniform int uGradientMaskChannel;
                    uniform int uDissolvePatternIndex;
                    uniform int uDissolveStateIndex;
                    uniform int uDissolveMaskIndex;
                    uniform vec2 uDissolveScrollSpeed;
                    uniform vec2 uDissolveTiling;
                    uniform float uDissolveThreshold;
                    uniform float uDissolveSoftness;
                    uniform int uDissolvePatternChannel;
                    uniform int uDissolveMaskChannel;
                    uniform int uFresnelMaskIndex;
                    uniform int uFresnelNoiseIndex;
                    uniform vec4 uFresnelColor;
                    uniform float uFresnelPower;
                    uniform float uFresnelStrength;
                    uniform vec2 uFresnelNoiseTiling;
                    uniform vec2 uFresnelNoiseSpeed;
                    uniform int uFresnelMaskChannel;
                    uniform int uFresnelNoiseChannel;
                    uniform int uBloomMaskIndex;
                    uniform vec4 uBloomColor;
                    uniform float uBloomIntensity;
                    uniform int uBloomMaskChannel;
                    uniform int uEmissionTexIndex;
                    uniform int uEmissionMaskIndex;
                    uniform vec2 uEmissionScrollSpeed;
                    uniform vec2 uEmissionTiling;
                    uniform vec4 uEmissionColor;
                    uniform float uEmissionStrength;
                    uniform int uEmissionChannel;
                    uniform int uEmissionMaskChannel;
                    uniform int uDistortionTexIndex;
                    uniform int uDistortionMaskIndex;
                    uniform vec2 uDistortionScrollSpeed;
                    uniform vec2 uDistortionTiling;
                    uniform float uDistortionStrength;
                    uniform int uDistortionChannelX;
                    uniform int uDistortionChannelY;
                    uniform int uDistortionMaskChannel;
                    uniform int uIridescenceTexIndex;
                    uniform int uIridescenceMaskIndex;
                    uniform vec4 uIridescenceControl;
                    uniform vec2 uIridescencePulseSpeedMin;
                    uniform vec2 uIridescenceAlphaMinMax;
                    uniform float uIridescenceDiffuseFadeMask;
                    uniform int uIridescenceMaskChannel;
                    uniform float uLightMapColorScale;
                    uniform vec4 uColorTint;
                    uniform float uAlphaCutoff;
                    uniform vec2 uMaterialUvRepeat;
                    uniform vec2 uMaterialUvScroll;
                    uniform int uMaterialUnlit;
                    uniform int uMaterialPremultipliedAlpha;
                    uniform int uMaterialSrgb;
                    uniform int uMaterialUsesTextureAlpha;
                    uniform int uUsesBakedDiffuse;
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
                    vec4 sampleAux(int index, vec2 uv){
                        if (index == -2) return vec4(0.0);
                        if (index == 0) return texture(uAuxTex0, uv);
                        if (index == 1) return texture(uAuxTex1, uv);
                        if (index == 2) return texture(uAuxTex2, uv);
                        if (index == 3) return texture(uAuxTex3, uv);
                        if (index == 4) return texture(uAuxTex4, uv);
                        if (index == 5) return texture(uAuxTex5, uv);
                        if (index == 6) return texture(uAuxTex6, uv);
                        if (index == 7) return texture(uAuxTex7, uv);
                        if (index == 8) return texture(uAuxTex8, uv);
                        if (index == 9) return texture(uAuxTex9, uv);
                        if (index == 10) return texture(uAuxTex10, uv);
                        if (index == 11) return texture(uAuxTex11, uv);
                        if (index == 12) return texture(uAuxTex12, uv);
                        if (index == 13) return texture(uAuxTex13, uv);
                        return vec4(1.0);
                    }
                    float channelValue(vec4 value, int channel){
                        if (channel == 0) return value.r;
                        if (channel == 1) return value.g;
                        if (channel == 2) return value.b;
                        if (channel == 3) return value.a;
                        return value.r;
                    }
                    vec3 channelColor(vec4 value, int channel){
                        if (channel >= 0) return vec3(channelValue(value, channel));
                        return value.rgb;
                    }
                    float effectHash(vec2 value){
                        return fract(sin(dot(value, vec2(127.1, 311.7))) * 43758.5453);
                    }
                    float effectNoise(vec2 uv){
                        vec2 cell = floor(uv);
                        vec2 local = fract(uv);
                        local = local * local * (3.0 - 2.0 * local);
                        float a = effectHash(cell);
                        float b = effectHash(cell + vec2(1.0, 0.0));
                        float c = effectHash(cell + vec2(0.0, 1.0));
                        float d = effectHash(cell + vec2(1.0, 1.0));
                        return mix(mix(a, b, local.x), mix(c, d, local.x), local.y);
                    }
                    void main(){
                            vec2 materialUv = vUv * uMaterialUvRepeat + uMaterialUvScroll * uEffectTime;
                            if ((uEffectKind & 1024) != 0 && uDistortionTexIndex >= 0 && abs(uDistortionStrength) > 0.0001)
                            {
                                vec2 distortionUv = vUv * max(uDistortionTiling, vec2(0.0001)) +
                                    uDistortionScrollSpeed * uEffectTime;
                                vec4 distortionSample = sampleAux(uDistortionTexIndex, distortionUv);
                                float dx = channelValue(distortionSample, uDistortionChannelX) * 2.0 - 1.0;
                                float dy = uDistortionChannelY >= 0
                                    ? channelValue(distortionSample, uDistortionChannelY) * 2.0 - 1.0
                                    : dx;
                                float distortionMask = uDistortionMaskIndex >= 0
                                    ? channelValue(sampleAux(uDistortionMaskIndex, vUv), uDistortionMaskChannel)
                                    : 1.0;
                                materialUv += vec2(dx, dy) * uDistortionStrength * distortionMask;
                            }

                            vec4 texColor = readBaseTexture(materialUv);
                            float coverageAlpha = uMaterialUsesTextureAlpha != 0 ? texColor.a : 1.0;
                            coverageAlpha *= vColor.a * uColorTint.a;
                            if (uAlphaCutoff > 0.0 && coverageAlpha < uAlphaCutoff) discard;
                            vec3 tintRgb = uMaterialSrgb != 0
                                ? srgbToLinear(uColorTint.rgb)
                                : uColorTint.rgb;
                            texColor.rgb *= vColor.rgb * tintRgb;
                            texColor.a = coverageAlpha;
                            float diff1 = max(dot(vNormal, uLightDir), 0.0);
                            float diff2 = max(dot(vNormal, uLightDir2), 0.0);
                            vec3 finalLight = clamp(uAmbient + diff1 * uLightColor + diff2 * uLightColor2, 0.0, 1.0);
                            vec3 finalColor;
                            if (uMaterialUnlit != 0)
                            {
                                finalColor = texColor.rgb;
                            }
                            else if (uUsesBakedDiffuse != 0 && uHasLightmap != 0)
                            {
                                finalColor = texture(uLightmap, vLightmapUv).rgb * vColor.rgb;
                            }
                            else
                            {
                                finalColor = texColor.rgb * finalLight;
                                if (uHasLightmap != 0)
                                    finalColor += texture(uLightmap, vLightmapUv).rgb * uLightMapColorScale;
                            }

                            if ((uEffectKind & 256) != 0 && uGradientTexIndex >= 0)
                            {
                                float mask = uGradientMaskIndex >= 0
                                    ? channelValue(sampleAux(uGradientMaskIndex, vUv), uGradientMaskChannel)
                                    : 1.0;
                                if (uGradientMaskThreshold > 0.0)
                                {
                                    float softness = max(uGradientMaskSoftness, 0.001);
                                    mask *= smoothstep(
                                        uGradientMaskThreshold - softness,
                                        uGradientMaskThreshold + softness,
                                        mask);
                                }
                                vec2 uv = vUv * max(uGradientTiling, vec2(0.0001)) +
                                    uGradientScrollSpeed * uEffectTime;
                                vec4 gradientSample = sampleAux(uGradientTexIndex, uv);
                                float gradientStrength = pow(
                                    clamp(channelValue(gradientSample, uGradientTextureChannel), 0.0, 1.0),
                                    max(uGradientSharpness, 0.001));
                                float pulse = 1.0 + sin((uEffectTime * uPulseRate + uPulseOffset) * 6.2831853) * uPulseMax;
                                float amount = clamp(
                                    mask * uGradientStrength * gradientStrength *
                                    max(pulse + max(uGradientBloomIntensity, 0.0), 0.0),
                                    0.0,
                                    1.0);
                                vec3 gradientTint = gradientSample.rgb * uGradientColor.rgb;
                                vec3 colorDodge = min(
                                    finalColor / max(vec3(1.0) - gradientTint, vec3(0.001)),
                                    vec3(4.0));
                                finalColor = mix(finalColor, colorDodge, amount);
                            }

                            if ((uEffectKind & 1) != 0 && uAdditiveTexIndex >= 0)
                            {
                                vec2 uv = vUv * max(uAdditiveTiling, vec2(0.0001)) +
                                    uAdditiveScrollSpeed * uEffectTime;
                                vec4 additiveSample = sampleAux(uAdditiveTexIndex, uv);
                                vec3 additiveColor = channelColor(additiveSample, uAdditiveTextureChannel);
                                float mask = uAdditiveMaskIndex >= 0
                                    ? channelValue(sampleAux(uAdditiveMaskIndex, vUv), uAdditiveMaskChannel)
                                    : 1.0;
                                finalColor += additiveColor * uAdditiveColor.rgb * uAdditiveStrength * mask;
                            }

                            if ((uEffectKind & 2) != 0 && uFlowTexIndex >= 0)
                            {
                                vec2 uv = vUv * max(uFlowTiling, vec2(0.0001)) +
                                    uFlowScrollSpeed * uEffectTime;
                                vec2 flow = sampleAux(uFlowTexIndex, uv).rg * 2.0 - 1.0;
                                float mask = uFlowMaskIndex >= 0
                                    ? channelValue(sampleAux(uFlowMaskIndex, vUv), uFlowMaskChannel)
                                    : 1.0;
                                vec2 flowUv = materialUv + flow * uFlowIntensity;
                                vec3 flowColor = readBaseTexture(flowUv).rgb *
                                    (uMaterialUnlit != 0 ? vec3(1.0) : finalLight);
                                finalColor = mix(
                                    finalColor,
                                    flowColor,
                                    clamp(mask * uFlowStrength, 0.0, 1.0));
                            }

                            if ((uEffectKind & 8) != 0 && uDissolvePatternIndex >= 0)
                            {
                                vec2 uv = vUv * max(uDissolveTiling, vec2(0.0001)) +
                                    uDissolveScrollSpeed * uEffectTime;
                                float pattern = channelValue(
                                    sampleAux(uDissolvePatternIndex, uv),
                                    uDissolvePatternChannel);
                                float mask = uDissolveMaskIndex >= 0
                                    ? channelValue(sampleAux(uDissolveMaskIndex, vUv), uDissolveMaskChannel)
                                    : 1.0;
                                pattern = mix(1.0, pattern, mask);
                                float softness = max(uDissolveSoftness, 0.001);
                                float coverage = smoothstep(
                                    uDissolveThreshold - softness,
                                    uDissolveThreshold + softness,
                                    pattern);
                                if (uDissolveStateIndex >= 0)
                                {
                                    vec4 stateSample = sampleAux(uDissolveStateIndex, materialUv);
                                    vec3 stateColor = uMaterialSrgb != 0
                                        ? srgbToLinear(stateSample.rgb)
                                        : stateSample.rgb;
                                    stateColor *= uMaterialUnlit != 0 ? vec3(1.0) : finalLight;
                                    finalColor = mix(stateColor, finalColor, coverage);
                                }
                                else
                                {
                                    texColor.a *= coverage;
                                }
                            }

                            if ((uEffectKind & 128) != 0 && uEmissionTexIndex >= 0)
                            {
                                vec2 uv = vUv * max(uEmissionTiling, vec2(0.0001)) +
                                    uEmissionScrollSpeed * uEffectTime;
                                vec4 emissionSample = sampleAux(uEmissionTexIndex, uv);
                                vec3 emissionColor = channelColor(emissionSample, uEmissionChannel);
                                float mask = uEmissionMaskIndex >= 0
                                    ? channelValue(sampleAux(uEmissionMaskIndex, vUv), uEmissionMaskChannel)
                                    : 1.0;
                                finalColor += emissionColor * uEmissionColor.rgb *
                                    clamp(uEmissionStrength, 0.0, 4.0) * mask;
                            }

                            if ((uEffectKind & 4) != 0)
                            {
                                vec3 viewDirection = normalize(uCameraPosition - vWorldPosition);
                                float facing = max(dot(normalize(vNormal), viewDirection), 0.0);
                                float fresnel = pow(1.0 - facing, max(uFresnelPower, 0.01));
                                float mask = uFresnelMaskIndex >= 0
                                    ? channelValue(sampleAux(uFresnelMaskIndex, vUv), uFresnelMaskChannel)
                                    : 1.0;
                                float noise = 1.0;
                                if ((uEffectKind & 64) != 0)
                                {
                                    vec2 uv = vUv * max(uFresnelNoiseTiling, vec2(0.001)) +
                                        uFresnelNoiseSpeed * uEffectTime;
                                    noise = uFresnelNoiseIndex >= 0
                                        ? channelValue(sampleAux(uFresnelNoiseIndex, uv), uFresnelNoiseChannel)
                                        : effectNoise(uv);
                                    noise = mix(0.6, 1.2, clamp(noise, 0.0, 1.0));
                                }
                                finalColor += uFresnelColor.rgb * fresnel * uFresnelStrength * noise * mask;
                            }

                            if ((uEffectKind & 16) != 0)
                            {
                                float mask = uBloomMaskIndex >= 0
                                    ? channelValue(sampleAux(uBloomMaskIndex, vUv), uBloomMaskChannel)
                                    : 1.0;
                                finalColor += uBloomColor.rgb * clamp(uBloomIntensity, 0.0, 4.0) * mask;
                            }

                            if ((uEffectKind & 512) != 0 && uIridescenceTexIndex >= 0)
                            {
                                vec3 iriNormal = normalize(vNormal);
                                vec3 iriViewDir = normalize(uCameraPosition - vWorldPosition);
                                float facing = abs(dot(iriNormal, iriViewDir));
                                float edge = 1.0 - facing;
                                float angular = uIridescenceControl.z > 0.0
                                    ? pow(clamp(edge, 0.0, 1.0), uIridescenceControl.z)
                                    : 0.0;
                                float mask = channelValue(
                                    sampleAux(uIridescenceMaskIndex, vUv),
                                    uIridescenceMaskChannel);
                                mask = clamp(mask, 0.0, 1.0);
                                float pulseSpeed = max(uIridescencePulseSpeedMin.x, 0.0);
                                float pulseMinimum = clamp(uIridescencePulseSpeedMin.y, 0.0, 1.0);
                                float pulse = pulseSpeed > 0.0001
                                    ? mix(
                                        pulseMinimum,
                                        1.0,
                                        0.5 + 0.5 * sin(uEffectTime * pulseSpeed * 6.2831853))
                                    : 1.0;
                                float lutU = clamp(
                                    angular * uIridescenceControl.y + uIridescenceControl.w,
                                    0.001,
                                    0.999);
                                vec3 iridescenceSample = sampleAux(
                                    uIridescenceTexIndex,
                                    vec2(lutU, 0.5)).rgb;
                                float iridescenceAmount = clamp(
                                    angular * max(uIridescenceControl.x, 0.0) * pulse * mask,
                                    0.0,
                                    1.0);
                                finalColor = finalColor * (1.0 - 0.15 * iridescenceAmount) +
                                    iridescenceSample * (0.25 * iridescenceAmount);
                                float fadeMask = clamp(
                                    mask * max(uIridescenceDiffuseFadeMask, 0.0),
                                    0.0,
                                    1.0);
                                float fresnelAlpha = mix(
                                    clamp(uIridescenceAlphaMinMax.x, 0.0, 1.0),
                                    clamp(uIridescenceAlphaMinMax.y, 0.0, 1.0),
                                    angular);
                                if (uMaterialUsesTextureAlpha != 0)
                                    texColor.a *= mix(1.0, fresnelAlpha, fadeMask);
                            }

                            if (uAlphaCutoff > 0.0 && texColor.a < uAlphaCutoff) discard;
                            if (uMaterialPremultipliedAlpha != 0)
                                finalColor *= texColor.a;
                            if (uMaterialSrgb != 0)
                                finalColor = linearToSrgb(finalColor);
                            fragColor = vec4(finalColor, texColor.a);
                    }";

        internal static string CreateVertex(int auxiliaryTextureCount) =>
            ExpandAuxiliaryTextureSlots(Vertex, auxiliaryTextureCount);

        internal static string CreateFragment(int auxiliaryTextureCount) =>
            ExpandAuxiliaryTextureSlots(Fragment, auxiliaryTextureCount);

        private static string ExpandAuxiliaryTextureSlots(string source, int auxiliaryTextureCount)
        {
            int count = Math.Clamp(
                auxiliaryTextureCount,
                1,
                MaximumAuxiliaryTextureCount);
            if (count == PortableAuxiliaryTextureCount)
                return source;

            return source
                .Replace(
                    BuildAuxiliarySamplerDeclarations(PortableAuxiliaryTextureCount),
                    BuildAuxiliarySamplerDeclarations(count),
                    StringComparison.Ordinal)
                .Replace(
                    BuildAuxiliarySampleCases(PortableAuxiliaryTextureCount),
                    BuildAuxiliarySampleCases(count),
                    StringComparison.Ordinal);
        }

        private static string BuildAuxiliarySamplerDeclarations(int count)
        {
            var builder = new StringBuilder(count * 56);
            for (int i = 0; i < count; i++)
            {
                builder.Append("                    uniform sampler2D uAuxTex")
                    .Append(i)
                    .Append(";\n");
            }
            return builder.ToString();
        }

        private static string BuildAuxiliarySampleCases(int count)
        {
            var builder = new StringBuilder(count * 84);
            for (int i = 0; i < count; i++)
            {
                builder.Append("                        if (index == ")
                    .Append(i)
                    .Append(") return texture(uAuxTex")
                    .Append(i)
                    .Append(", uv);\n");
            }
            return builder.ToString();
        }
    }
}
