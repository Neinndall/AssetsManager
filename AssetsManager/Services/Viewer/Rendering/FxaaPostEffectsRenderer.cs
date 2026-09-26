using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Utils.Rendering;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Screen-space fast approximate anti-aliasing (FXAA 3.11 Preset 26) pass matching the in-game post filter.
    /// Smooths high-contrast edge silhouettes across rendered frames using center green luma and weighted neighbor samples.
    /// </summary>
    internal sealed class FxaaPostEffectsRenderer : IDisposable
    {
        public const float DefaultSubpix = 0.75f;
        public const float DefaultEdgeThreshold = 0.5f;
        public const float DefaultEdgeThresholdMin = 0.0833f;

        public static readonly Vector3 DefaultOptions = new(DefaultSubpix, DefaultEdgeThreshold, DefaultEdgeThresholdMin);

        public static readonly float[] SearchSteps =
        [
            1.0f, 1.5f, 2.0f, 2.0f, 2.0f, 2.0f, 2.0f, 2.0f, 2.0f, 4.0f, 8.0f
        ];

        internal const string VertexShader = @"
out vec2 vUv;

void main()
{
    vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    vUv = corner;
    gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
}";

        internal const string FragmentShader = @"
#define STEPS 11

uniform sampler2D frame;
uniform vec2 rcpFrame;
/* subpix, edge threshold, edge threshold floor */
uniform vec3 options;

const float STEP[STEPS] = float[](1.0, 1.5, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 2.0, 4.0, 8.0);

in vec2 vUv;
out vec4 FragColor;

float lumaOf(vec4 rgba) {
  return dot(rgba.rgb, vec3(0.299, 0.587, 0.114));
}

float lumaAt(vec2 at) {
  return lumaOf(textureLod(frame, at, 0.0));
}

/* A macro, since a texel offset has to be a constant expression and a parameter is not one. */
#define lumaOff(at, offset) lumaOf(textureLodOffset(frame, at, 0.0, offset))

void main() {
  vec2 posM = vUv;
  vec4 rgbyM = textureLod(frame, posM, 0.0);
  /* The game reads the centre's luma from its green channel and every neighbour's from
     the weighted sum. */
  float lumaM = rgbyM.g;
  float lumaS = lumaOff(posM, ivec2(0, 1));
  float lumaE = lumaOff(posM, ivec2(1, 0));
  float lumaN = lumaOff(posM, ivec2(0, -1));
  float lumaW = lumaOff(posM, ivec2(-1, 0));

  float rangeMax = max(max(lumaE, max(lumaM, lumaS)), max(lumaW, lumaN));
  float rangeMin = min(min(lumaE, min(lumaM, lumaS)), min(lumaW, lumaN));
  float range = rangeMax - rangeMin;
  if (range < max(options.z, rangeMax * options.y)) {
    FragColor = rgbyM;
    return;
  }

  float lumaNW = lumaOff(posM, ivec2(-1, -1));
  float lumaSE = lumaOff(posM, ivec2(1, 1));
  float lumaNE = lumaOff(posM, ivec2(1, -1));
  float lumaSW = lumaOff(posM, ivec2(-1, 1));

  float lumaNS = lumaN + lumaS;
  float lumaWE = lumaW + lumaE;
  float lumaNESE = lumaNE + lumaSE;
  float lumaNWNE = lumaNW + lumaNE;
  float lumaNWSW = lumaNW + lumaSW;
  float lumaSWSE = lumaSW + lumaSE;
  float edgeHorz = abs(-2.0 * lumaW + lumaNWSW)
    + abs(-2.0 * lumaM + lumaNS) * 2.0
    + abs(-2.0 * lumaE + lumaNESE);
  float edgeVert = abs(-2.0 * lumaS + lumaSWSE)
    + abs(-2.0 * lumaM + lumaWE) * 2.0
    + abs(-2.0 * lumaN + lumaNWNE);
  bool horzSpan = edgeHorz >= edgeVert;

  float lengthSign = horzSpan ? rcpFrame.y : rcpFrame.x;
  if (!horzSpan) {
    lumaN = lumaW;
    lumaS = lumaE;
  }
  float gradientN = lumaN - lumaM;
  float gradientS = lumaS - lumaM;
  bool pairN = abs(gradientN) >= abs(gradientS);
  float gradient = max(abs(gradientN), abs(gradientS));
  if (pairN) lengthSign = -lengthSign;

  float subpixB = ((lumaNS + lumaWE) * 2.0 + (lumaNWSW + lumaNESE)) * (1.0 / 12.0) - lumaM;
  float subpixC = clamp(abs(subpixB) / range, 0.0, 1.0);
  float subpixF = (-2.0 * subpixC + 3.0) * subpixC * subpixC;

  vec2 posB = posM;
  vec2 offNP = horzSpan ? vec2(rcpFrame.x, 0.0) : vec2(0.0, rcpFrame.y);
  if (horzSpan) posB.y += lengthSign * 0.5;
  else posB.x += lengthSign * 0.5;

  float lumaNN = (pairN ? lumaN : lumaS) + lumaM;
  float gradientScaled = gradient * 0.25;
  bool lumaMLTZero = lumaM - lumaNN * 0.5 < 0.0;

  vec2 posN = posB - offNP * STEP[0];
  vec2 posP = posB + offNP * STEP[0];
  float lumaEndN = lumaAt(posN) - lumaNN * 0.5;
  float lumaEndP = lumaAt(posP) - lumaNN * 0.5;
  bool doneN = abs(lumaEndN) >= gradientScaled;
  bool doneP = abs(lumaEndP) >= gradientScaled;
  if (!doneN) posN -= offNP * STEP[1];
  if (!doneP) posP += offNP * STEP[1];

  for (int i = 2; i < STEPS; i++) {
    if (doneN && doneP) break;

    if (!doneN) lumaEndN = lumaAt(posN) - lumaNN * 0.5;
    if (!doneP) lumaEndP = lumaAt(posP) - lumaNN * 0.5;
    doneN = abs(lumaEndN) >= gradientScaled;
    doneP = abs(lumaEndP) >= gradientScaled;
    if (!doneN) posN -= offNP * STEP[i];
    if (!doneP) posP += offNP * STEP[i];
  }

  float dstN = horzSpan ? posM.x - posN.x : posM.y - posN.y;
  float dstP = horzSpan ? posP.x - posM.x : posP.y - posM.y;
  bool directionN = dstN < dstP;
  bool goodSpan = directionN ? (lumaEndN < 0.0) != lumaMLTZero : (lumaEndP < 0.0) != lumaMLTZero;
  float pixelOffset = min(dstN, dstP) * (-1.0 / (dstN + dstP)) + 0.5;
  float offset = max(goodSpan ? pixelOffset : 0.0, subpixF * subpixF * options.x);
  if (horzSpan) posM.y += offset * lengthSign;
  else posM.x += offset * lengthSign;

  FragColor = textureLod(frame, posM, 0.0);
}";

        private GL _gl;
        private GlSceneCapture _capture;
        private uint _vao;
        private uint _program;
        private int _frameLocation;
        private int _rcpFrameLocation;
        private int _optionsLocation;
        private bool _ready;

        public bool IsReady => _ready;

        public static Vector2 CalculateRcpFrame(int width, int height) =>
            new(1.0f / Math.Max(1, width), 1.0f / Math.Max(1, height));

        internal void Initialize(GL gl)
        {
            ArgumentNullException.ThrowIfNull(gl);
            if (_ready)
                return;

            _gl = gl;
            bool embedded = GlShaderCompiler.UsesEmbeddedProfile(gl);
            _program = GlShaderCompiler.CreateProgram(gl, embedded, VertexShader, FragmentShader);
            _vao = gl.GenVertexArray();
            _capture = new GlSceneCapture(gl);

            _frameLocation = _gl.GetUniformLocation(_program, "frame");
            _rcpFrameLocation = _gl.GetUniformLocation(_program, "rcpFrame");
            _optionsLocation = _gl.GetUniformLocation(_program, "options");

            _gl.UseProgram(_program);
            if (_frameLocation >= 0)
                _gl.Uniform1(_frameLocation, 0);
            _gl.UseProgram(0);

            _ready = true;
        }

        internal void Render(int width, int height)
        {
            if (!_ready || width <= 0 || height <= 0)
                return;

            _capture.Capture((uint)width, (uint)height, captureColor: true, captureDepth: false);
            if (_capture.ColorTexture == 0)
                return;

            RenderTexture(_capture.ColorTexture, width, height);
        }

        internal void RenderTexture(uint sourceTexture, int width, int height)
        {
            if (!_ready || sourceTexture == 0 || width <= 0 || height <= 0)
                return;

            _gl.Viewport(0, 0, (uint)width, (uint)height);
            _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.Disable(EnableCap.Blend);
            _gl.Disable(EnableCap.CullFace);
            _gl.ColorMask(true, true, true, true);

            _gl.UseProgram(_program);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, sourceTexture);
            if (_frameLocation >= 0)
                _gl.Uniform1(_frameLocation, 0);
            if (_rcpFrameLocation >= 0)
                _gl.Uniform2(_rcpFrameLocation, 1f / width, 1f / height);
            if (_optionsLocation >= 0)
                _gl.Uniform3(_optionsLocation, DefaultOptions.X, DefaultOptions.Y, DefaultOptions.Z);

            _gl.BindVertexArray(_vao);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);

            _gl.BindVertexArray(0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.UseProgram(0);

            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.DepthMask(true);
        }

        public void Dispose()
        {
            if (!_ready)
                return;

            try
            {
                _capture?.Dispose();
                if (_vao != 0) _gl.DeleteVertexArray(_vao);
                if (_program != 0) _gl.DeleteProgram(_program);
            }
            catch (Silk.NET.Core.Loader.SymbolLoadingException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _capture = null;
                _vao = 0;
                _program = 0;
                _gl = null;
                _ready = false;
            }
        }
    }
}
