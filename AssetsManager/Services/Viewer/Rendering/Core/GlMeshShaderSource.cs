namespace AssetsManager.Services.Viewer.Rendering.Core
{
    internal static class GlMeshShaderSource
    {
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
					uniform vec3 uWaveDirection;
					uniform float uWaveSpeed;
					uniform float uWaveFrequency;
					uniform float uWaveIntensity;
					const int MAX_BONES = 256;
					layout(std140) uniform BoneTransforms {
						mat4 uBoneTransforms[MAX_BONES];
					};
					out vec3 vNormal;
					out vec3 vWorldPosition;
					out vec2 vUv;
					out vec2 vLightmapUv;
					out vec4 vColor;
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
							if ((uEffectKind & 32) != 0 && abs(uWaveIntensity) > 0.0001)
							{
								vec2 waveDirection = length(uWaveDirection.xy) > 0.0001
									? normalize(uWaveDirection.xy)
									: vec2(0.0, 1.0);
								vec3 normal = length(animatedNormal) > 0.0001
									? normalize(animatedNormal)
									: vec3(0.0, 1.0, 0.0);
								float phase = dot(aUv, waveDirection) * 6.2831853 * uWaveFrequency + uEffectTime * uWaveSpeed;
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
					uniform sampler2D uEffectTex;
					uniform sampler2D uEffectMask;
					uniform sampler2D uEmissionTex;
					uniform sampler2D uEmissionMask;
					uniform sampler2D uIridescenceTex;
					uniform sampler2D uIridescenceMask;
					uniform int uHasLightmap;
					uniform int uEffectKind;
					uniform int uHasEffectTex;
					uniform int uHasEmissionTex;
					uniform int uHasEmissionMask;
					uniform int uHasIridescenceTex;
					uniform vec4 uIridescenceControl;
					uniform vec2 uIridescencePulseSpeedMin;
					uniform vec2 uIridescenceAlphaMinMax;
					uniform float uIridescenceDiffuseFadeMask;
					uniform float uEffectTime;
					uniform vec2 uEffectScrollSpeed;
					uniform vec2 uEffectTiling;
					uniform vec4 uEffectColor;
					uniform float uEffectStrength;
					uniform float uFlowIntensity;
					uniform vec3 uCameraPosition;
					uniform vec4 uFresnelColor;
					uniform float uFresnelPower;
					uniform float uFresnelStrength;
					uniform float uDissolveThreshold;
					uniform float uDissolveSoftness;
					uniform vec4 uBloomColor;
					uniform float uBloomIntensity;
					uniform float uPulseRate;
					uniform float uPulseMax;
					uniform float uPulseOffset;
					uniform float uGradientSharpness;
					uniform vec2 uEmissionScrollSpeed;
					uniform vec2 uEmissionTiling;
					uniform vec4 uEmissionColor;
					uniform float uEmissionStrength;
					uniform int uEmissionChannel;
					uniform vec2 uFresnelNoiseTiling;
					uniform vec2 uFresnelNoiseSpeed;
					uniform float uLightMapColorScale;
					uniform vec4 uColorTint;
					uniform float uAlphaCutoff;
					uniform int uUsesBakedDiffuse;
					uniform vec3 uLightDir;
					uniform vec3 uLightColor;
					uniform vec3 uLightDir2;
					uniform vec3 uLightColor2;
					uniform vec3 uAmbient;
					const float BLOOM_EMISSION_SCALE = 0.2;
					const float GRADIENT_BLOOM_SCALE = 0.05;
					const float GRADIENT_PULSE_SCALE = 0.1;
					const float EMISSION_TEXTURE_SCALE = 0.5;
					out vec4 fragColor;
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
					vec3 readEmissionColor(vec4 sampleValue){
						if (uEmissionChannel == 0) return vec3(sampleValue.r);
						if (uEmissionChannel == 1) return vec3(sampleValue.g);
						if (uEmissionChannel == 2) return vec3(sampleValue.b);
						if (uEmissionChannel == 3) return vec3(sampleValue.a);
						return sampleValue.rgb;
					}
					void main(){
							vec4 texColor = texture(uTex, vUv);
							if (texColor.a * vColor.a * uColorTint.a < uAlphaCutoff) discard;
							texColor.rgb /= max(texColor.a, 0.0039215686);
							texColor *= vColor * uColorTint;
							float diff1 = max(dot(vNormal, uLightDir), 0.0);
							float diff2 = max(dot(vNormal, uLightDir2), 0.0);
							vec3 finalLight = clamp(uAmbient + diff1 * uLightColor + diff2 * uLightColor2, 0.0, 1.0);
							vec3 finalColor;
							if (uUsesBakedDiffuse != 0 && uHasLightmap != 0)
							{
									finalColor = texture(uLightmap, vLightmapUv).rgb * vColor.rgb;
							}
							else
							{
									finalColor = texColor.rgb * finalLight;
									if (uHasLightmap != 0)
										finalColor += texture(uLightmap, vLightmapUv).rgb * uLightMapColorScale;
							}
							float effectMask = texture(uEffectMask, vUv).r;
							vec2 effectUv = vUv * max(uEffectTiling, vec2(0.0001));
							if ((uEffectKind & 256) != 0 && uHasEffectTex != 0)
							{
								float gradientMask = clamp(effectMask * uEffectStrength, 0.0, 1.0);
								if (uDissolveThreshold > 0.0)
								{
									gradientMask *= smoothstep(uDissolveThreshold - uDissolveSoftness,
										uDissolveThreshold + uDissolveSoftness, gradientMask);
								}
								vec2 gradientUv = effectUv + uEffectScrollSpeed * uEffectTime;
								vec3 gradientColor = texture(uEffectTex, gradientUv).rgb;
								float gradientStrength = pow(
									clamp(gradientColor.r, 0.0, 1.0),
									max(uGradientSharpness, 0.001));
								float pulse = 1.0 + sin((uEffectTime * uPulseRate + uPulseOffset) * 6.2831853) * uPulseMax;
								float bloom = clamp(uBloomIntensity * GRADIENT_BLOOM_SCALE, 0.0, 1.0);
								vec3 gradientTint = gradientColor * uEffectColor.rgb * gradientStrength;
								float effectAmount = clamp(gradientMask * GRADIENT_PULSE_SCALE *
									(max(pulse, 0.0) + bloom), 0.0, 1.0);
								vec3 colorDodge = min(finalColor /
									max(vec3(1.0) - gradientTint, vec3(0.001)), vec3(2.0));
								finalColor = mix(finalColor, colorDodge, effectAmount);
							}
							if (uHasEffectTex != 0)
							{
								if ((uEffectKind & 1) != 0)
								{
									float additive = texture(
										uEffectTex,
										effectUv + uEffectScrollSpeed * uEffectTime).r;
									finalColor += vec3(additive) * uEffectColor.rgb * uEffectStrength * effectMask;
							}
								else if ((uEffectKind & 2) != 0)
								{
									vec2 flow = texture(
										uEffectTex,
										effectUv + uEffectScrollSpeed * uEffectTime).rg * 2.0 - 1.0;
									vec2 flowUv = vUv + flow * uFlowIntensity;
									vec3 flowColor = texture(uTex, flowUv).rgb * finalLight;
									finalColor = mix(
										finalColor,
										flowColor,
										effectMask * clamp(uEffectStrength, 0.0, 1.0));
								}
								if ((uEffectKind & 8) != 0)
								{
									float dissolve = texture(
										uEffectTex,
										effectUv + uEffectScrollSpeed * uEffectTime).r;
									dissolve = mix(1.0, dissolve, effectMask);
									float softness = max(uDissolveSoftness, 0.001);
									texColor.a *= smoothstep(
										uDissolveThreshold - softness,
										uDissolveThreshold + softness,
										dissolve);
								}
							}
							if (texColor.a < uAlphaCutoff) discard;
							if (uHasEmissionTex != 0 && (uEffectKind & 128) != 0)
							{
								vec2 emissionUv = vUv * max(uEmissionTiling, vec2(0.0001)) +
									uEmissionScrollSpeed * uEffectTime;
								vec4 emissionSample = texture(uEmissionTex, emissionUv);
								vec3 emissionColor = readEmissionColor(emissionSample);
								float emissionMask = uHasEmissionMask != 0
									? texture(uEmissionMask, vUv).r
									: 1.0;
								float emissionStrength = clamp(
									uEmissionStrength * EMISSION_TEXTURE_SCALE,
									0.0,
									2.0);
								finalColor += emissionColor * uEmissionColor.rgb *
									emissionStrength * emissionMask;
							}
							if ((uEffectKind & 4) != 0)
							{
								vec3 viewDirection = normalize(uCameraPosition - vWorldPosition);
								float facing = max(dot(normalize(vNormal), viewDirection), 0.0);
								float fresnel = pow(
									1.0 - facing,
									max(uFresnelPower, 0.01));
								float fresnelNoise = 1.0;
								if ((uEffectKind & 64) != 0)
								{
									vec2 noiseUv = vUv * max(uFresnelNoiseTiling, vec2(0.001)) +
										uFresnelNoiseSpeed * uEffectTime;
									fresnelNoise = mix(0.6, 1.2, effectNoise(noiseUv));
								}
								finalColor += uFresnelColor.rgb * fresnel * uFresnelStrength * fresnelNoise * effectMask;
							}
							if ((uEffectKind & 16) != 0)
							{
								float bloomEmission = clamp(uBloomIntensity * BLOOM_EMISSION_SCALE, 0.0, 1.0);
								finalColor += uBloomColor.rgb * bloomEmission * effectMask;
							}
							if ((uEffectKind & 512) != 0 && uHasIridescenceTex != 0)
							{
								vec3 iriNormal = normalize(vNormal);
								vec3 iriViewDir = normalize(uCameraPosition - vWorldPosition);
								float facing = abs(dot(iriNormal, iriViewDir));
								float edge = 1.0 - facing;
								float angular = pow(
									clamp(edge, 0.0, 1.0),
									max(uIridescenceControl.z, 0.001));
								float mask = texture(uIridescenceMask, vUv).r;
								mask = clamp(mask, 0.0, 1.0);
								float pulseSpeed = max(uIridescencePulseSpeedMin.x, 0.0);
								float pulseMinimum = clamp(uIridescencePulseSpeedMin.y, 0.0, 1.0);
								float pulse = pulseSpeed > 0.0001
									? mix(
										pulseMinimum,
										1.0,
										0.5 + 0.5 * sin(uEffectTime * pulseSpeed * 6.2831853))
									: 1.0;
								float lutHalfTexel = 0.5 /
									float(textureSize(uIridescenceTex, 0).x);
								float lutU = clamp(
									angular * uIridescenceControl.y + uIridescenceControl.w,
									lutHalfTexel,
									1.0 - lutHalfTexel);
								vec3 iridescenceSample = texture(
									uIridescenceTex,
									vec2(lutU, 0.5)).rgb;
								float iridescenceAmount = clamp(
									angular * max(uIridescenceControl.x, 0.0) * pulse * mask,
									0.0,
									1.0);
								finalColor = finalColor * (1.0 - 0.15 * iridescenceAmount) +
									iridescenceSample * (0.85 * iridescenceAmount);

								float fadeMask = clamp(
									mask * max(uIridescenceDiffuseFadeMask, 0.0),
									0.0,
									1.0);
								float fresnelAlpha = mix(
									clamp(uIridescenceAlphaMinMax.x, 0.0, 1.0),
									clamp(uIridescenceAlphaMinMax.y, 0.0, 1.0),
									angular);
								texColor.a *= mix(1.0, fresnelAlpha, fadeMask);
							}
							if (texColor.a <= 0.0001) discard;
							fragColor = vec4(finalColor, texColor.a);
				}";
    }
}
