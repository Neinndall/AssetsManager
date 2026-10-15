using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Vfx.Rendering;

/// <summary>Owns a scene's particle shadow depth map and the preview ground's receiver.</summary>
internal sealed class VfxParticleShadowScene : IDisposable
{
    internal const int Size = 2048;
    internal const float Extent = 3000f;
    private readonly GL _gl;
    private uint _framebuffer, _depth;
    internal uint DepthTexture => _depth;
    internal bool Enabled { get; private set; }
    internal Matrix4x4 ViewProjection { get; private set; }
    internal Vector3 Color { get; private set; } = new(.6f);

    internal VfxParticleShadowScene(GL gl) => _gl = gl;

    internal static Matrix4x4 Aim(Matrix4x4 eyeView, Vector3 toSun, float ground = 0f)
    {
        Matrix4x4.Invert(eyeView, out Matrix4x4 world);
        Vector3 direction = -new Vector3(world.M31, world.M32, world.M33);
        Vector3 focus = world.Translation;
        float travel = MathF.Abs(direction.Y) > 1e-6f ? (ground - focus.Y) / direction.Y : -1f;
        focus = travel >= 0 ? focus + direction * travel : new Vector3(focus.X, ground, focus.Z);
        toSun = toSun.LengthSquared() > 0f ? Vector3.Normalize(toSun) : Vector3.UnitY;
        Vector3 up = MathF.Abs(toSun.Z) > .99f ? Vector3.UnitY : Vector3.UnitZ;
        Matrix4x4 rotation = Matrix4x4.CreateLookAt(toSun, Vector3.Zero, up);
        Vector3 local = Vector3.TransformNormal(focus, rotation);
        float texel = Extent / Size;
        local.X = MathF.Floor(local.X / texel + .5f) * texel;
        local.Y = MathF.Floor(local.Y / texel + .5f) * texel;
        Matrix4x4.Invert(rotation, out Matrix4x4 inverse);
        focus = Vector3.TransformNormal(local, inverse);
        Matrix4x4 projection = Matrix4x4.Identity;
        projection.M11 = projection.M22 = 2f / Extent;
        projection.M33 = -2f / 9999f;
        projection.M43 = -10001f / 9999f;
        return Matrix4x4.CreateLookAt(focus + toSun * 5000f, focus, up) * projection;
    }

    internal unsafe void Render(IReadOnlyList<IPreparedParticlePass> passes, Matrix4x4 eyeView, MapSunData sun = null)
    {
        Enabled = false;
        foreach (var pass in passes) Enabled |= pass.HasPreparedShadowCasters;
        if (!Enabled) return;
        Vector3 light = sun?.Direction ?? Vector3.Normalize(new Vector3(-.25f, .75f, -.05f));
        light.X = -light.X;
        ViewProjection = Aim(eyeView, light);
        float ambient = sun is null ? .6f : sun.SkyScale + sun.Intensity > 0 ? sun.SkyScale / (sun.SkyScale + sun.Intensity) : .5f;
        Vector4 sky = sun?.SkyColor ?? Vector4.One;
        Color = new Vector3(sky.X, sky.Y, sky.Z) * Math.Clamp(ambient, 0, 1);
        _gl.GetInteger(GLEnum.DrawFramebufferBinding, out int draw);
        _gl.GetInteger(GLEnum.ReadFramebufferBinding, out int read);
        int[] viewport = new int[4]; _gl.GetInteger(GLEnum.Viewport, viewport);
        _gl.GetInteger(GLEnum.ActiveTexture, out int active);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.GetInteger(GLEnum.TextureBinding2D, out int texture);
        _gl.GetInteger(GLEnum.DepthWritemask, out int depthWrite);
        _gl.GetFloat(GLEnum.DepthClearValue, out float clearDepth);
        bool scissor = _gl.IsEnabled(EnableCap.ScissorTest);
        try
        {
            if (_depth == 0)
            {
                _depth = _gl.GenTexture(); _gl.BindTexture(TextureTarget.Texture2D, _depth);
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, Size, Size, 0, PixelFormat.DepthComponent, PixelType.UnsignedInt, null);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                _framebuffer = _gl.GenFramebuffer();
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
                _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment, TextureTarget.Texture2D, _depth, 0);
                DrawBufferMode none = DrawBufferMode.None;
                _gl.DrawBuffers(1, &none); _gl.ReadBuffer(ReadBufferMode.None);
                if (_gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
                    throw new InvalidOperationException("Particle shadow framebuffer is incomplete.");
            }
            _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _framebuffer);
            _gl.Viewport(0, 0, Size, Size);
            _gl.Disable(EnableCap.ScissorTest);
            _gl.DepthMask(true); _gl.ClearDepth(1d); _gl.Clear(ClearBufferMask.DepthBufferBit);
            foreach (var pass in passes) pass.RenderPreparedShadowPass(ViewProjection, eyeView);
        }
        finally
        {
            _gl.DepthMask(depthWrite != 0); _gl.ClearDepth(clearDepth);
            if (scissor) _gl.Enable(EnableCap.ScissorTest);
            _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, (uint)draw);
            _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)read);
            _gl.Viewport(viewport[0], viewport[1], (uint)viewport[2], (uint)viewport[3]);
            _gl.ActiveTexture(TextureUnit.Texture0); _gl.BindTexture(TextureTarget.Texture2D, (uint)texture);
            _gl.ActiveTexture((TextureUnit)active);
        }
    }

    internal void Bind(uint program)
    {
        _gl.Uniform1(_gl.GetUniformLocation(program, "uSunShadowOn"), Enabled ? 1 : 0);
        if (!Enabled) return;
        var matrix = ViewProjection;
        _gl.UniformMatrix4(_gl.GetUniformLocation(program, "uSunShadowMatrix"), 1, false, in matrix.M11);
        _gl.Uniform3(_gl.GetUniformLocation(program, "uSunShadowColor"), Color.X, Color.Y, Color.Z);
        _gl.Uniform1(_gl.GetUniformLocation(program, "uSunShadowMap"), 2);
        _gl.ActiveTexture(TextureUnit.Texture2);
        _gl.BindTexture(TextureTarget.Texture2D, _depth); _gl.BindSampler(2, 0);
    }

    internal const string Receiver = @"
uniform sampler2D uSunShadowMap;
uniform mat4 uSunShadowMatrix;
uniform vec3 uSunShadowColor;
uniform int uSunShadowOn;
vec3 sunShadowed(vec3 albedo, vec3 world) {
    if (uSunShadowOn == 0) return albedo;
    vec4 at = uSunShadowMatrix * vec4(world, 1.0);
    vec3 mapped = at.xyz / at.w * 0.5 + 0.5;
    if (any(lessThan(mapped, vec3(0.0))) || any(greaterThan(mapped, vec3(1.0)))) return albedo;
    float visible = mapped.z - 0.0005 <= texture(uSunShadowMap, mapped.xy).r ? 1.0 : 0.0;
    return albedo * (uSunShadowColor + visible * (1.0 - uSunShadowColor));
}";

    public void Dispose()
    {
        if (_framebuffer != 0) _gl.DeleteFramebuffer(_framebuffer);
        if (_depth != 0) _gl.DeleteTexture(_depth);
        _framebuffer = _depth = 0; Enabled = false;
    }
}
