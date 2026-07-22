using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Utils.Rendering;

namespace AssetsManager.Services.Viewer
{
    public sealed class GlVfxRenderer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ParticleVertex
        {
            public Vector3 Position;
            public Vector2 TexCoord;
            public Vector4 Color;
        }

        private GL _gl = null!;
        private uint _program;
        private int _uViewProj;
        private int _uTex;
        private uint _vao;
        private uint _vbo;
        private uint _ebo;
        private uint _whiteTex;
        private bool _ready;

        private VfxSystemModel _activeSystem;
        private bool _isPlaying;
        private Vector3 _worldAnchor;
        private float _worldScale = 1.0f;
        private readonly Dictionary<VfxEmitterModel, List<VfxParticleInstance>> _particlePools = new();
        private readonly Dictionary<VfxEmitterModel, float> _spawnAccumulators = new();
        private readonly Random _rand = new();

        private const string VertShader = @"
            layout(location = 0) in vec3 aPos;
            layout(location = 1) in vec2 aTexCoord;
            layout(location = 2) in vec4 aColor;
            uniform mat4 uViewProj;
            out vec2 vTexCoord;
            out vec4 vColor;
            void main() {
                vTexCoord = aTexCoord;
                vColor = aColor;
                gl_Position = uViewProj * vec4(aPos, 1.0);
            }
        ";

        private const string FragShader = @"
            in vec2 vTexCoord;
            in vec4 vColor;
            uniform sampler2D uTex;
            out vec4 FragColor;
            void main() {
                vec4 texColor = texture(uTex, vTexCoord);
                FragColor = texColor * vColor;
            }
        ";

        public void Initialize(GL gl)
        {
            _gl = gl;
            bool gles = GlShaderCompiler.UsesEmbeddedProfile(gl);
            _program = GlShaderCompiler.CreateProgram(gl, gles, VertShader, FragShader);

            _uViewProj = gl.GetUniformLocation(_program, "uViewProj");
            _uTex = gl.GetUniformLocation(_program, "uTex");

            _vao = gl.GenVertexArray();
            _vbo = gl.GenBuffer();
            _ebo = gl.GenBuffer();

            // Create 1x1 white texture fallback
            _whiteTex = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, _whiteTex);
            byte[] white = { 255, 255, 255, 255 };
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, new ReadOnlySpan<byte>(white));
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            gl.BindTexture(TextureTarget.Texture2D, 0);

            _ready = true;
        }

        public void SetVfxSystem(VfxSystemModel system)
        {
            _activeSystem = system;
            _isPlaying = false;
            _particlePools.Clear();
            _spawnAccumulators.Clear();

            if (system != null)
            {
                foreach (var emitter in system.Emitters)
                {
                    _particlePools[emitter] = new List<VfxParticleInstance>();
                    _spawnAccumulators[emitter] = 0.0f;
                }
            }
        }

        public void Play() => _isPlaying = _activeSystem != null;

        public void Pause() => _isPlaying = false;

        public void SetWorldTransform(Vector3 position, float scale)
        {
            _worldAnchor = position;
            _worldScale = Math.Max(0.01f, scale);
        }

        public void Stop()
        {
            _isPlaying = false;
            foreach (var particles in _particlePools.Values)
            {
                particles.Clear();
            }
            foreach (var emitter in new List<VfxEmitterModel>(_spawnAccumulators.Keys))
            {
                _spawnAccumulators[emitter] = 0.0f;
            }
        }

        public void Update(float deltaTime)
        {
            if (!_ready || !_isPlaying || _activeSystem == null) return;

            deltaTime *= (float)Math.Clamp(_activeSystem.Speed, 0.25, 2.0);

            foreach (var kvp in _particlePools)
            {
                var emitter = kvp.Key;
                var particles = kvp.Value;

                // 1. Update existing particles
                for (int i = particles.Count - 1; i >= 0; i--)
                {
                    var p = particles[i];
                    p.Age += deltaTime;
                    if (!p.IsAlive)
                    {
                        particles.RemoveAt(i);
                        continue;
                    }

                    p.Velocity += emitter.Acceleration * deltaTime;
                    p.Position += p.Velocity * deltaTime;
                    float lifeRatio = Math.Clamp(p.Age / p.MaxLifetime, 0.0f, 1.0f);
                    p.Color = Vector4.Lerp(emitter.StartColor, emitter.EndColor, lifeRatio);
                }

                // 2. Spawn new particles
                if (emitter.EmissionRate > 0)
                {
                    _spawnAccumulators[emitter] += deltaTime;
                    float spawnInterval = 1.0f / emitter.EmissionRate;
                    while (_spawnAccumulators[emitter] >= spawnInterval)
                    {
                        _spawnAccumulators[emitter] -= spawnInterval;
                        if (particles.Count < 500) // cap per emitter
                        {
                            particles.Add(SpawnParticle(emitter));
                        }
                    }
                }
            }
        }

        private VfxParticleInstance SpawnParticle(VfxEmitterModel emitter)
        {
            float rx = (float)(_rand.NextDouble() * 20.0 - 10.0);
            float ry = (float)(_rand.NextDouble() * 20.0 - 10.0);
            float rz = (float)(_rand.NextDouble() * 20.0 - 10.0);

            return new VfxParticleInstance
            {
                Position = new Vector3(rx, ry + 90.0f, rz),
                Velocity = emitter.InitialVelocity + new Vector3(rx * 0.1f, 12.0f + ry * 0.1f, rz * 0.1f),
                Scale = emitter.InitialScale * (0.8f + (float)_rand.NextDouble() * 0.4f),
                Color = emitter.StartColor,
                Age = 0.0f,
                MaxLifetime = emitter.Lifetime > 0 ? emitter.Lifetime : 1.5f,
                Rotation = (float)(_rand.NextDouble() * Math.PI * 2)
            };
        }

        public void Render(Matrix4x4 viewProj, Matrix4x4 viewMatrix)
        {
            if (!_ready || _particlePools.Count == 0) return;

            // Extract camera Right and Up vectors for billboard alignment
            Vector3 camRight = new Vector3(viewMatrix.M11, viewMatrix.M21, viewMatrix.M31);
            Vector3 camUp = new Vector3(viewMatrix.M12, viewMatrix.M22, viewMatrix.M32);

            _gl.UseProgram(_program);
            _gl.UniformMatrix4(_uViewProj, 1, false, in viewProj.M11);

            _gl.Enable(EnableCap.Blend);
            _gl.DepthMask(false); // don't write particles to depth buffer

            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _whiteTex);
            _gl.Uniform1(_uTex, 0);

            var vertices = new List<ParticleVertex>();
            var indices = new List<ushort>();

            foreach (var kvp in _particlePools)
            {
                var emitter = kvp.Key;
                var particles = kvp.Value;
                if (particles.Count == 0) continue;

                // Set Blend Mode
                if (emitter.BlendMode == 1) // Additive
                {
                    _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One);
                }
                else // AlphaBlend
                {
                    _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                }

                vertices.Clear();
                indices.Clear();

                foreach (var p in particles)
                {
                    ushort baseIndex = (ushort)vertices.Count;
                    float halfSize = p.Scale.X * 10.0f * _worldScale;
                    Vector3 worldPosition = _worldAnchor + p.Position * _worldScale;

                    Vector3 p0 = worldPosition + (-camRight - camUp) * halfSize;
                    Vector3 p1 = worldPosition + (camRight - camUp) * halfSize;
                    Vector3 p2 = worldPosition + (camRight + camUp) * halfSize;
                    Vector3 p3 = worldPosition + (-camRight + camUp) * halfSize;

                    vertices.Add(new ParticleVertex { Position = p0, TexCoord = new Vector2(0, 0), Color = p.Color });
                    vertices.Add(new ParticleVertex { Position = p1, TexCoord = new Vector2(1, 0), Color = p.Color });
                    vertices.Add(new ParticleVertex { Position = p2, TexCoord = new Vector2(1, 1), Color = p.Color });
                    vertices.Add(new ParticleVertex { Position = p3, TexCoord = new Vector2(0, 1), Color = p.Color });

                    indices.Add(baseIndex);
                    indices.Add((ushort)(baseIndex + 1));
                    indices.Add((ushort)(baseIndex + 2));
                    indices.Add(baseIndex);
                    indices.Add((ushort)(baseIndex + 2));
                    indices.Add((ushort)(baseIndex + 3));
                }

                UploadAndDrawQuadMesh(vertices, indices);
            }

            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
        }

        private void UploadAndDrawQuadMesh(List<ParticleVertex> vertices, List<ushort> indices)
        {
            if (vertices.Count == 0 || indices.Count == 0) return;

            _gl.BindVertexArray(_vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);

            var vertArray = vertices.ToArray();
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertArray.Length * 9 * sizeof(float)), new ReadOnlySpan<ParticleVertex>(vertArray), BufferUsageARB.StreamDraw);

            _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
            var indexArray = indices.ToArray();
            _gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(indexArray.Length * sizeof(ushort)), new ReadOnlySpan<ushort>(indexArray), BufferUsageARB.StreamDraw);

            uint stride = 9 * sizeof(float); // 3 (pos) + 2 (uv) + 4 (col)

            // Pos (0)
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);

            // TexCoord (1)
            _gl.EnableVertexAttribArray(1);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));

            // Color (2)
            _gl.EnableVertexAttribArray(2);
            _gl.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, 5 * sizeof(float));

            nint indexOffset = 0;
            _gl.DrawElements(PrimitiveType.Triangles, (uint)indices.Count, DrawElementsType.UnsignedShort, in indexOffset);

            _gl.BindVertexArray(0);
        }

        public void Dispose()
        {
            if (_ready && _gl != null)
            {
                _gl.DeleteVertexArray(_vao);
                _gl.DeleteBuffer(_vbo);
                _gl.DeleteBuffer(_ebo);
                _gl.DeleteTexture(_whiteTex);
                _gl.DeleteProgram(_program);
                _ready = false;
            }
        }
    }
}
