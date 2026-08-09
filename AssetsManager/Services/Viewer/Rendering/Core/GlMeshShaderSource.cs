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
					uniform mat4 uViewProj;
					uniform mat4 uWorld;
					uniform int uHasVertexColor;
					out vec3 vNormal;
					out vec2 vUv;
					out vec2 vLightmapUv;
					out vec4 vColor;
					void main(){
							vec4 worldPos = uWorld * vec4(aPos, 1.0);
							gl_Position = uViewProj * worldPos;
							vNormal = normalize(mat3(uWorld) * aNormal);
							vUv = aUv;
							vLightmapUv = aLightmapUv;
							vColor = uHasVertexColor != 0 ? aColor : vec4(1.0);
				}";

        internal const string Fragment = @"
					in vec3 vNormal;
					in vec2 vUv;
					in vec2 vLightmapUv;
					in vec4 vColor;
					uniform sampler2D uTex;
					uniform sampler2D uLightmap;
					uniform int uHasLightmap;
					uniform float uLightMapColorScale;
					uniform vec4 uColorTint;
					uniform float uAlphaCutoff;
					uniform int uUsesBakedDiffuse;
					uniform vec3 uLightDir;
					uniform vec3 uLightColor;
					uniform vec3 uLightDir2;
					uniform vec3 uLightColor2;
					uniform vec3 uAmbient;
					out vec4 fragColor;
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
							fragColor = vec4(finalColor, texColor.a);
				}";
    }
}
