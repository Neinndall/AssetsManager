using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Screen-space MAP pass aligned with LTK MAIN: scene depth drives SSAO, fog and depth of field,
    /// while the finished color frame is sampled only after scene particles/VFX have been drawn.
    /// </summary>
    internal sealed class MapPostEffectsRenderer : IDisposable
    {
        private const int MostSamples = 8;
        private const int NoiseSide = 4;
        private const float GoldenAngle = 2.39996323f;
        private const float CocFrameHeight = 1080f;

        internal const string FullscreenVertex = @"
out vec2 vUv;

void main()
{
    vec2 corner = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    vUv = corner;
    gl_Position = vec4(corner * 2.0 - 1.0, 0.0, 1.0);
}";

        internal const string OcclusionFragment = @"
uniform sampler2D uSceneDepth;
uniform mat4 uProjection;
uniform mat4 uProjectionInverse;
uniform vec4 uKernel[8];
uniform vec3 uNoise[16];
uniform int uSamples;
uniform float uSampleRadius;
uniform float uBias;
uniform float uPower;

in vec2 vUv;
out vec4 FragColor;

vec4 sceneAt(vec2 at)
{
    vec2 texels = vec2(textureSize(uSceneDepth, 0));
    vec2 texel = clamp(floor(at * texels), vec2(0.0), texels - 1.0);
    float depth = texelFetch(uSceneDepth, ivec2(texel), 0).x;
    vec2 centre = (texel + 0.5) / texels;
    vec4 view = uProjectionInverse * vec4(vec3(centre, depth) * 2.0 - 1.0, 1.0);
    return vec4(view.xyz / view.w, depth);
}

void main()
{
    vec4 scene = sceneAt(vUv);
    vec3 center = scene.xyz;
    if (scene.w >= 1.0)
    {
        FragColor = vec4(1.0, -center.z * 0.1, 0.0, 1.0);
        return;
    }

    vec3 normal = normalize(cross(dFdx(center), dFdy(center)));
    if (dot(normal, center) > 0.0)
        normal = -normal;
    vec2 cell = mod(floor(gl_FragCoord.xy), 4.0);
    vec3 turn = uNoise[int(cell.x + cell.y * 4.0)];

    float occluded = 0.0;
    for (int i = 0; i < 8; i++)
    {
        if (i >= uSamples)
            break;
        vec3 reach = normalize(reflect(uKernel[i].xyz, turn)) * uSampleRadius * uKernel[i].w;
        vec3 probe = center + reach * sign(dot(reach, normal));
        vec4 clip = uProjection * vec4(probe, 1.0);
        vec2 at = clip.xy / clip.w * 0.5 + 0.5;
        float sceneZ = sceneAt(at).z;
        float gap = sceneZ - center.z;
        float range = gap > 0.0 ? clamp(uSampleRadius / gap, 0.0, 1.0) : 0.0;
        if (sceneZ > probe.z + uBias)
            occluded += range * range * (3.0 - 2.0 * range);
    }

    float open = 1.0 - occluded / float(uSamples);
    FragColor = vec4(pow(abs(open), uPower), -center.z * 0.1, 0.0, 1.0);
}";

        internal const string BlurFragment = @"
uniform sampler2D uOcclusion;
uniform vec2 uStride;
uniform int uEdgeAware;

in vec2 vUv;
out vec4 FragColor;

const float WEIGHTS[5] = float[5](0.125, 0.25, 0.25, 0.25, 0.125);

void main()
{
    vec2 center = texture(uOcclusion, vUv).xy;
    float sum = 0.0;
    for (int i = 0; i < 5; i++)
    {
        vec2 tap = texture(uOcclusion, vUv + float(i - 2) * uStride).xy;
        float value = uEdgeAware != 0
            ? mix(tap.x, center.x, clamp(center.y - tap.y, 0.0, 1.0))
            : tap.x;
        sum += value * WEIGHTS[i];
    }
    FragColor = vec4(sum, center.y, 0.0, 1.0);
}";

        internal const string PostFragment = @"
#define TAPS 64
#define GOLDEN_ANGLE 2.39996323
#define COC_FRAME_HEIGHT 1080.0

uniform sampler2D uFrame;
uniform sampler2D uSceneDepth;
uniform sampler2D uOcclusion;
uniform mat4 uProjectionInverse;
uniform mat4 uCameraWorld;
uniform vec2 uViewport;

uniform int uDepthFogOn;
uniform vec3 uDepthFogColor;
uniform vec3 uDepthFogRamp;
uniform int uHeightFogOn;
uniform vec3 uHeightFogColor;
uniform vec3 uHeightFogRamp;
uniform int uFocusOn;
uniform vec3 uFocus;
uniform int uOcclusionOn;
uniform float uOcclusionIntensity;

in vec2 vUv;
out vec4 FragColor;

float storedDepth(vec2 at)
{
    return texture(uSceneDepth, at).x;
}

vec4 viewAt(vec2 at, float depth)
{
    vec4 view = uProjectionInverse * vec4(vec3(at, depth) * 2.0 - 1.0, 1.0);
    return vec4(view.xyz / view.w, 1.0);
}

float awayAt(vec2 at, float depth)
{
    return max(-viewAt(at, depth).z, 0.0);
}

vec3 shadedAt(vec2 at)
{
    vec3 color = texture(uFrame, at).rgb;
    if (uOcclusionOn != 0)
        color *= mix(1.0, texture(uOcclusion, at).x, uOcclusionIntensity);
    return color;
}

float ramp(float at, vec3 fog)
{
    float span = fog.y - fog.x;
    if (abs(span) < 1e-3)
        return 0.0;
    return min(clamp((at - fog.x) / span, 0.0, 1.0), fog.z);
}

float blurAt(float away)
{
    float halfBand = uFocus.y * 0.5;
    float transition = max(halfBand, 1.0);
    float farAmount = clamp((away - uFocus.x - halfBand) / transition, 0.0, 1.0);
    float nearAmount = clamp((uFocus.x - halfBand - away) / transition, 0.0, 1.0);
    return max(farAmount, nearAmount);
}

vec3 blurred(vec2 at, float depth, float away, vec3 color)
{
    float amount = blurAt(away);
    if (amount <= 0.0)
        return color;
    vec2 reach = amount * uFocus.z * (uViewport.y / COC_FRAME_HEIGHT) / uViewport;
    vec3 sum = color;
    float weight = 1.0;
    for (int i = 0; i < TAPS; i++)
    {
        float radius = sqrt((float(i) + 0.5) / float(TAPS));
        float angle = float(i) * GOLDEN_ANGLE;
        vec2 tap = at + vec2(cos(angle), sin(angle)) * radius * reach;
        float tapDepth = storedDepth(tap);
        float counts = depth < tapDepth ? 1.0 : blurAt(awayAt(tap, tapDepth));
        sum += shadedAt(tap) * counts;
        weight += counts;
    }
    return sum / weight;
}

void main()
{
    float depth = storedDepth(vUv);
    vec3 color = shadedAt(vUv);

    if (uFocusOn != 0)
        color = blurred(vUv, depth, awayAt(vUv, depth), color);
    if (depth < 1.0 && (uHeightFogOn != 0 || uDepthFogOn != 0))
    {
        vec3 world = (uCameraWorld * viewAt(vUv, depth)).xyz;
        if (uHeightFogOn != 0)
            color = mix(color, uHeightFogColor, ramp(world.y, uHeightFogRamp));
        if (uDepthFogOn != 0)
        {
            float away = length(world - uCameraWorld[3].xyz);
            color = mix(color, uDepthFogColor, ramp(away, uDepthFogRamp));
        }
    }

    FragColor = vec4(color, 1.0);
}";

        private readonly Vector4[][] _kernels =
        {
            BuildKernel(4),
            BuildKernel(8)
        };
        private readonly Vector3[] _noise = BuildNoise();

        private GL _gl;
        private GlSceneCapture _capture;
        private uint _vao;
        private uint _occlusionProgram;
        private uint _blurProgram;
        private uint _postProgram;
        private uint _occludedTexture;
        private uint _blurredTexture;
        private uint _occludedFramebuffer;
        private uint _blurredFramebuffer;
        private int _aoWidth;
        private int _aoHeight;
        private bool _ready;

        internal static bool DrawsAnything(MapPostEffectsData effects, MapSsaoData occlusion) =>
            effects?.DrawsAnything == true || occlusion?.DrawsAnything == true;

        internal void Initialize(GL gl)
        {
            ArgumentNullException.ThrowIfNull(gl);
            if (_ready)
                return;

            _gl = gl;
            bool embedded = GlShaderCompiler.UsesEmbeddedProfile(gl);
            _occlusionProgram = GlShaderCompiler.CreateProgram(gl, embedded, FullscreenVertex, OcclusionFragment);
            _blurProgram = GlShaderCompiler.CreateProgram(gl, embedded, FullscreenVertex, BlurFragment);
            _postProgram = GlShaderCompiler.CreateProgram(gl, embedded, FullscreenVertex, PostFragment);
            _vao = gl.GenVertexArray();
            _capture = new GlSceneCapture(gl);
            BindFixedSamplers();
            _ready = true;
        }

        internal void CaptureSceneDepth(
            MapPostEffectsData effects,
            MapSsaoData occlusion,
            uint width,
            uint height)
        {
            if (!_ready || !DrawsAnything(effects, occlusion) || width == 0 || height == 0)
                return;
            _capture.Capture(width, height, captureColor: false, captureDepth: true);
        }

        internal void Render(
            MapPostEffectsData effects,
            MapSsaoData occlusion,
            Matrix4x4 view,
            Matrix4x4 projection,
            uint width,
            uint height)
        {
            if (!_ready || !DrawsAnything(effects, occlusion) || width == 0 || height == 0 ||
                _capture?.DepthTexture == 0)
            {
                return;
            }

            _capture.Capture(width, height, captureColor: true, captureDepth: false);
            _gl.GetInteger(GLEnum.FramebufferBinding, out int previousFramebuffer);
            Matrix4x4.Invert(projection, out Matrix4x4 projectionInverse);
            Matrix4x4.Invert(view, out Matrix4x4 cameraWorld);

            bool drawsOcclusion = occlusion?.DrawsAnything == true;
            if (drawsOcclusion)
                DrawOcclusion(occlusion, projection, projectionInverse, width, height);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)previousFramebuffer);
            _gl.Viewport(0, 0, width, height);
            _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.Disable(EnableCap.CullFace);
            _gl.Disable(EnableCap.Blend);
            _gl.ColorMask(true, true, true, true);
            _gl.UseProgram(_postProgram);
            BindTexture(0, _capture.ColorTexture);
            BindTexture(1, _capture.DepthTexture);
            BindTexture(2, drawsOcclusion ? _occludedTexture : _capture.ColorTexture);
            WritePostEffects(effects, occlusion, projectionInverse, cameraWorld, width, height, drawsOcclusion);
            DrawFullscreen();

            RestoreState();
        }

        private void DrawOcclusion(
            MapSsaoData occlusion,
            Matrix4x4 projection,
            Matrix4x4 projectionInverse,
            uint frameWidth,
            uint frameHeight)
        {
            float scale = Math.Clamp(occlusion.BufferScale, 0f, 1f);
            int width = Math.Max(1, (int)MathF.Round(frameWidth * scale));
            int height = Math.Max(1, (int)MathF.Round(frameHeight * scale));
            EnsureOcclusionTargets(width, height);

            _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _gl.Disable(EnableCap.CullFace);
            _gl.Disable(EnableCap.Blend);
            _gl.ColorMask(true, true, true, true);

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _occludedFramebuffer);
            _gl.Viewport(0, 0, (uint)width, (uint)height);
            _gl.UseProgram(_occlusionProgram);
            BindTexture(0, _capture.DepthTexture);
            WriteOcclusion(occlusion, projection, projectionInverse);
            DrawFullscreen();

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _blurredFramebuffer);
            _gl.UseProgram(_blurProgram);
            BindTexture(0, _occludedTexture);
            _gl.Uniform2(_gl.GetUniformLocation(_blurProgram, "uStride"), 1f / width, 0f);
            _gl.Uniform1(_gl.GetUniformLocation(_blurProgram, "uEdgeAware"), occlusion.EdgeAwareBlur ? 1 : 0);
            DrawFullscreen();

            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _occludedFramebuffer);
            BindTexture(0, _blurredTexture);
            _gl.Uniform2(_gl.GetUniformLocation(_blurProgram, "uStride"), 0f, 1f / height);
            DrawFullscreen();
        }

        private void WriteOcclusion(MapSsaoData occlusion, Matrix4x4 projection, Matrix4x4 projectionInverse)
        {
            UniformMatrix(_occlusionProgram, "uProjection", projection);
            UniformMatrix(_occlusionProgram, "uProjectionInverse", projectionInverse);
            int samples = occlusion.SampleCount;
            Vector4[] kernel = samples == 4 ? _kernels[0] : _kernels[1];
            for (int index = 0; index < MostSamples; index++)
            {
                Vector4 value = kernel[index];
                int location = _gl.GetUniformLocation(_occlusionProgram, $"uKernel[{index}]");
                if (location >= 0)
                    _gl.Uniform4(location, value.X, value.Y, value.Z, value.W);
            }
            for (int index = 0; index < _noise.Length; index++)
            {
                Vector3 value = _noise[index];
                int location = _gl.GetUniformLocation(_occlusionProgram, $"uNoise[{index}]");
                if (location >= 0)
                    _gl.Uniform3(location, value.X, value.Y, value.Z);
            }
            _gl.Uniform1(_gl.GetUniformLocation(_occlusionProgram, "uSamples"), samples);
            _gl.Uniform1(_gl.GetUniformLocation(_occlusionProgram, "uSampleRadius"), occlusion.SampleRadius);
            _gl.Uniform1(_gl.GetUniformLocation(_occlusionProgram, "uBias"), occlusion.Bias);
            _gl.Uniform1(_gl.GetUniformLocation(_occlusionProgram, "uPower"), occlusion.Power);
        }

        private void WritePostEffects(
            MapPostEffectsData effects,
            MapSsaoData occlusion,
            Matrix4x4 projectionInverse,
            Matrix4x4 cameraWorld,
            uint width,
            uint height,
            bool drawsOcclusion)
        {
            UniformMatrix(_postProgram, "uProjectionInverse", projectionInverse);
            UniformMatrix(_postProgram, "uCameraWorld", cameraWorld);
            _gl.Uniform2(_gl.GetUniformLocation(_postProgram, "uViewport"), (float)width, (float)height);

            WriteFog("Depth", effects?.DepthFog);
            WriteFog("Height", effects?.HeightFog);
            MapDepthOfFieldData focus = effects?.DepthOfField;
            bool focusOn = focus?.Enabled == true && focus.Coc > 0f;
            _gl.Uniform1(_gl.GetUniformLocation(_postProgram, "uFocusOn"), focusOn ? 1 : 0);
            _gl.Uniform3(
                _gl.GetUniformLocation(_postProgram, "uFocus"),
                focus?.FocalDistance ?? 2000f,
                focus?.InFocusWidth ?? 800f,
                focus?.Coc ?? 10f);
            _gl.Uniform1(_gl.GetUniformLocation(_postProgram, "uOcclusionOn"), drawsOcclusion ? 1 : 0);
            _gl.Uniform1(_gl.GetUniformLocation(_postProgram, "uOcclusionIntensity"), occlusion?.Intensity ?? 1f);
        }

        private void WriteFog(string prefix, MapFogData fog)
        {
            bool on = fog?.Enabled == true && fog.MaxIntensity > 0f;
            _gl.Uniform1(_gl.GetUniformLocation(_postProgram, $"u{prefix}FogOn"), on ? 1 : 0);
            Vector4 color = fog?.Color ?? new Vector4(0f, 0f, 0f, 1f);
            _gl.Uniform3(_gl.GetUniformLocation(_postProgram, $"u{prefix}FogColor"), color.X, color.Y, color.Z);
            _gl.Uniform3(
                _gl.GetUniformLocation(_postProgram, $"u{prefix}FogRamp"),
                fog?.Start ?? 0f,
                fog?.End ?? 0f,
                fog?.MaxIntensity ?? 0f);
        }

        private void EnsureOcclusionTargets(int width, int height)
        {
            if (_occludedTexture != 0 && _aoWidth == width && _aoHeight == height)
                return;

            DeleteOcclusionTargets();
            _occludedTexture = CreateTargetTexture(width, height);
            _blurredTexture = CreateTargetTexture(width, height);
            _occludedFramebuffer = CreateFramebuffer(_occludedTexture);
            _blurredFramebuffer = CreateFramebuffer(_blurredTexture);
            _aoWidth = width;
            _aoHeight = height;
        }

        private uint CreateTargetTexture(int width, int height)
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba16f,
                (uint)width,
                (uint)height,
                0,
                PixelFormat.Rgba,
                PixelType.HalfFloat,
                ReadOnlySpan<byte>.Empty);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }

        private uint CreateFramebuffer(uint texture)
        {
            uint framebuffer = _gl.GenFramebuffer();
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            _gl.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                texture,
                0);
            GLEnum status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != GLEnum.FramebufferComplete)
                throw new InvalidOperationException($"MAP post-effect framebuffer is incomplete: {status}.");
            return framebuffer;
        }

        private void BindFixedSamplers()
        {
            _gl.UseProgram(_occlusionProgram);
            _gl.Uniform1(_gl.GetUniformLocation(_occlusionProgram, "uSceneDepth"), 0);
            _gl.UseProgram(_blurProgram);
            _gl.Uniform1(_gl.GetUniformLocation(_blurProgram, "uOcclusion"), 0);
            _gl.UseProgram(_postProgram);
            _gl.Uniform1(_gl.GetUniformLocation(_postProgram, "uFrame"), 0);
            _gl.Uniform1(_gl.GetUniformLocation(_postProgram, "uSceneDepth"), 1);
            _gl.Uniform1(_gl.GetUniformLocation(_postProgram, "uOcclusion"), 2);
            _gl.UseProgram(0);
        }

        private void BindTexture(uint unit, uint texture)
        {
            _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
            _gl.BindTexture(TextureTarget.Texture2D, texture);
        }

        private void UniformMatrix(uint program, string name, Matrix4x4 matrix)
        {
            int location = _gl.GetUniformLocation(program, name);
            if (location >= 0)
                _gl.UniformMatrix4(location, 1, false, in matrix.M11);
        }

        private void DrawFullscreen()
        {
            _gl.BindVertexArray(_vao);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
        }

        private void RestoreState()
        {
            _gl.UseProgram(0);
            _gl.BindVertexArray(0);
            for (uint unit = 0; unit < 3; unit++)
            {
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + unit));
                _gl.BindTexture(TextureTarget.Texture2D, 0);
                _gl.BindSampler(unit, 0);
            }
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthFunc(DepthFunction.Lequal);
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
            _gl.Disable(EnableCap.CullFace);
            _gl.ColorMask(true, true, true, true);
        }

        private static Vector4[] BuildKernel(int samples)
        {
            Vector3[] directions = Sphere(samples);
            var kernel = new Vector4[MostSamples];
            for (int index = 0; index < MostSamples; index++)
            {
                if (index >= directions.Length)
                {
                    kernel[index] = Vector4.Zero;
                    continue;
                }
                float share = (index + 1f) / samples;
                Vector3 direction = directions[index];
                kernel[index] = new Vector4(direction, 0.1f + 0.9f * share * share);
            }
            return kernel;
        }

        private static Vector3[] BuildNoise()
        {
            Vector3[] sphere = Sphere(NoiseSide * NoiseSide);
            var noise = new Vector3[sphere.Length];
            for (int index = 0; index < sphere.Length; index++)
                noise[index] = sphere[(index * 7) % sphere.Length];
            return noise;
        }

        private static Vector3[] Sphere(int count)
        {
            var values = new Vector3[count];
            float golden = MathF.PI * (3f - MathF.Sqrt(5f));
            for (int index = 0; index < count; index++)
            {
                float y = 1f - (2f * (index + 0.5f)) / count;
                float ring = MathF.Sqrt(MathF.Max(0f, 1f - y * y));
                float angle = index * golden;
                values[index] = new Vector3(MathF.Cos(angle) * ring, y, MathF.Sin(angle) * ring);
            }
            return values;
        }

        private void DeleteOcclusionTargets()
        {
            if (_occludedFramebuffer != 0) _gl.DeleteFramebuffer(_occludedFramebuffer);
            if (_blurredFramebuffer != 0) _gl.DeleteFramebuffer(_blurredFramebuffer);
            if (_occludedTexture != 0) _gl.DeleteTexture(_occludedTexture);
            if (_blurredTexture != 0) _gl.DeleteTexture(_blurredTexture);
            _occludedFramebuffer = 0;
            _blurredFramebuffer = 0;
            _occludedTexture = 0;
            _blurredTexture = 0;
            _aoWidth = 0;
            _aoHeight = 0;
        }

        public void Dispose()
        {
            if (!_ready)
                return;
            try
            {
                _capture?.Dispose();
                DeleteOcclusionTargets();
                if (_vao != 0) _gl.DeleteVertexArray(_vao);
                if (_occlusionProgram != 0) _gl.DeleteProgram(_occlusionProgram);
                if (_blurProgram != 0) _gl.DeleteProgram(_blurProgram);
                if (_postProgram != 0) _gl.DeleteProgram(_postProgram);
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
                _occlusionProgram = 0;
                _blurProgram = 0;
                _postProgram = 0;
                _gl = null;
                _ready = false;
            }
        }
    }
}
