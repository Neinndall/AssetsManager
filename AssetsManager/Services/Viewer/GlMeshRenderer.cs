using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Silk.NET.OpenGL;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Services.Viewer.Vfx;

namespace AssetsManager.Services.Viewer
{
    /// <summary>
    /// Handles 3D mesh uploads, texture generation, and drawing for character models using Silk.NET.
    /// Uses ConditionalWeakTable to bind GL buffers to existing WPF ModelPart instances without modification.
    /// </summary>
    public sealed class GlMeshRenderer : IDisposable
    {
        private class GlPartBuffers : IDisposable
        {
            public uint Vao;
            public uint Vbo;
            public uint Ebo;
            public int IndexCount;
            public uint Texture;
            public string LoadedTextureKey;

            public void Dispose()
            {
                // Managed by renderer, but safe fallback
            }
        }

        private GL _gl = null!;
        private uint _program;
        private int _uViewProj;
        private int _uWorld;
        private int _uTex;
        private int _uLightDir;
        private int _uLightColor;
        private int _uLightDir2;
        private int _uLightColor2;
        private int _uAmbient;
        private bool _ready;

        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
        private delegate void DrawElementsDelegate(uint mode, int count, uint type, IntPtr indices);
        private DrawElementsDelegate _drawElements = null!;

        private readonly ConditionalWeakTable<ModelPart, GlPartBuffers> _partBuffers = new();
        private readonly List<uint> _allocatedTextures = new();
        private readonly List<(uint Vao, uint Vbo, uint Ebo)> _allocatedBuffers = new();
        private uint _whiteTex;

        public void Initialize(GL gl)
        {
            _gl = gl;
            var proc = gl.Context.GetProcAddress("glDrawElements");
            if (proc != IntPtr.Zero)
            {
                _drawElements = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<DrawElementsDelegate>(proc);
            }
            bool gles = ShaderUtil.DetectGles(gl);
            _program = ShaderUtil.CreateProgram(gl, gles, MeshVert, MeshFrag);
            _uViewProj = gl.GetUniformLocation(_program, "uViewProj");
            _uWorld = gl.GetUniformLocation(_program, "uWorld");
            _uTex = gl.GetUniformLocation(_program, "uTex");
            _uLightDir = gl.GetUniformLocation(_program, "uLightDir");
            _uLightColor = gl.GetUniformLocation(_program, "uLightColor");
            _uLightDir2 = gl.GetUniformLocation(_program, "uLightDir2");
            _uLightColor2 = gl.GetUniformLocation(_program, "uLightColor2");
            _uAmbient = gl.GetUniformLocation(_program, "uAmbient");

            // Generate fallback 1x1 white texture
            _whiteTex = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, _whiteTex);
            byte[] white = { 255, 255, 255, 255 };
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 1, 1, 0, PixelFormat.Rgba, PixelType.UnsignedByte, new ReadOnlySpan<byte>(white));
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            gl.BindTexture(TextureTarget.Texture2D, 0);

            _ready = true;
        }

        public void Render(SceneModel model, Matrix4x4 viewProj, Vector3 lightDir, Vector3 lightColor, Vector3 lightDir2, Vector3 lightColor2, Vector3 ambientColor)
        {
            if (!_ready || model == null || !model.IsVisible) return;

            _gl.UseProgram(_program);
            _gl.UniformMatrix4(_uViewProj, 1, false, in viewProj.M11);

            // Compute model's world matrix from Position/Rotation/Scale
            float pitch = (float)(model.RotationX * (Math.PI / 180.0));
            float yaw = (float)(model.RotationY * (Math.PI / 180.0));
            float roll = (float)(model.RotationZ * (Math.PI / 180.0));
            Matrix4x4 world = Matrix4x4.CreateScale((float)model.Scale) *
                              Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll) *
                              Matrix4x4.CreateTranslation((float)model.PositionX, (float)model.PositionY, (float)model.PositionZ);

            _gl.UniformMatrix4(_uWorld, 1, false, in world.M11);
            _gl.Uniform3(_uLightDir, Vector3.Normalize(lightDir));
            _gl.Uniform3(_uLightColor, lightColor);
            _gl.Uniform3(_uLightDir2, Vector3.Normalize(lightDir2));
            _gl.Uniform3(_uLightColor2, lightColor2);
            _gl.Uniform3(_uAmbient, ambientColor);
            _gl.Uniform1(_uTex, 0);

            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(true);

            foreach (var part in model.Parts)
            {
                if (!part.IsVisible) continue;

                var buffers = EnsureBuffers(part, model);
                if (buffers.Vao == 0) continue;

                _gl.BindVertexArray(buffers.Vao);
                _gl.ActiveTexture(TextureUnit.Texture0);

                uint tex = buffers.Texture != 0 ? buffers.Texture : _whiteTex;
                _gl.BindTexture(TextureTarget.Texture2D, tex);

                if (part.IsTransparent)
                {
                    _gl.Enable(EnableCap.Blend);
                    _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
                    _gl.DepthMask(false);
                }
                else
                {
                    _gl.Disable(EnableCap.Blend);
                    _gl.DepthMask(true);
                }

                if (_drawElements != null)
                {
                    _drawElements((uint)PrimitiveType.Triangles, buffers.IndexCount, (uint)DrawElementsType.UnsignedInt, IntPtr.Zero);
                }
            }

            _gl.BindVertexArray(0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
        }

        private GlPartBuffers EnsureBuffers(ModelPart part, SceneModel model)
        {
            if (!_partBuffers.TryGetValue(part, out var buffers))
            {
                buffers = new GlPartBuffers();
                _partBuffers.Add(part, buffers);
            }

            // If the selected texture changed in WPF UI, rebuild the OpenGL texture
            if (buffers.LoadedTextureKey != part.SelectedTextureName)
            {
                if (buffers.Texture != 0)
                {
                    _gl.DeleteTexture(buffers.Texture);
                    _allocatedTextures.Remove(buffers.Texture);
                    buffers.Texture = 0;
                }

                if (!string.IsNullOrEmpty(part.SelectedTextureName) && part.AllTextures.TryGetValue(part.SelectedTextureName, out var bitmap))
                {
                    buffers.Texture = UploadTexture(bitmap);
                    buffers.LoadedTextureKey = part.SelectedTextureName;
                }
            }

            // Build VAO/VBO/EBO if not already created
            if (buffers.Vao == 0 && part.Geometry?.Geometry is MeshGeometry3D mesh)
            {
                var positions = mesh.Positions;
                var texCoords = mesh.TextureCoordinates;
                var indices = mesh.TriangleIndices;

                if (positions != null && indices != null)
                {
                    int vertexCount = positions.Count;
                    // Interleaved: pos3 + normal3 + uv2 = 8 floats per vertex
                    float[] vertices = new float[vertexCount * 8];

                    // Generate smooth flat normals dynamically
                    Vector3[] normals = new Vector3[vertexCount];
                    for (int i = 0; i < indices.Count; i += 3)
                    {
                        if (i + 2 >= indices.Count) break;
                        int idx0 = indices[i];
                        int idx1 = indices[i + 1];
                        int idx2 = indices[i + 2];

                        if (idx0 < vertexCount && idx1 < vertexCount && idx2 < vertexCount)
                        {
                            var p0 = positions[idx0];
                            var p1 = positions[idx1];
                            var p2 = positions[idx2];

                            Vector3 v0 = new Vector3((float)(p1.X - p0.X), (float)(p1.Y - p0.Y), (float)(p1.Z - p0.Z));
                            Vector3 v1 = new Vector3((float)(p2.X - p0.X), (float)(p2.Y - p0.Y), (float)(p2.Z - p0.Z));
                            Vector3 normal = Vector3.Normalize(Vector3.Cross(v0, v1));

                            normals[idx0] += normal;
                            normals[idx1] += normal;
                            normals[idx2] += normal;
                        }
                    }

                    for (int i = 0; i < vertexCount; i++)
                    {
                        var p = positions[i];
                        vertices[i * 8 + 0] = (float)p.X;
                        vertices[i * 8 + 1] = (float)p.Y;
                        vertices[i * 8 + 2] = (float)p.Z;

                        var norm = normals[i].LengthSquared() > 0f ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
                        vertices[i * 8 + 3] = norm.X;
                        vertices[i * 8 + 4] = norm.Y;
                        vertices[i * 8 + 5] = norm.Z;

                        if (texCoords != null && i < texCoords.Count)
                        {
                            var uv = texCoords[i];
                            vertices[i * 8 + 6] = (float)uv.X;
                            vertices[i * 8 + 7] = (float)uv.Y;
                        }
                    }

                    uint[] indicesArray = new uint[indices.Count];
                    for (int i = 0; i < indices.Count; i++)
                    {
                        indicesArray[i] = (uint)indices[i];
                    }

                    buffers.Vao = _gl.GenVertexArray();
                    buffers.Vbo = _gl.GenBuffer();
                    buffers.Ebo = _gl.GenBuffer();

                    _gl.BindVertexArray(buffers.Vao);

                    _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffers.Vbo);
                    _gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(vertices), BufferUsageARB.DynamicDraw);

                    _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, buffers.Ebo);
                    _gl.BufferData(BufferTargetARB.ElementArrayBuffer, new ReadOnlySpan<uint>(indicesArray), BufferUsageARB.StaticDraw);

                    uint stride = 8 * sizeof(float);
                    _gl.EnableVertexAttribArray(0);
                    _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, IntPtr.Zero);

                    _gl.EnableVertexAttribArray(1);
                    _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, new IntPtr(3 * sizeof(float)));

                    _gl.EnableVertexAttribArray(2);
                    _gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, new IntPtr(6 * sizeof(float)));

                    _gl.BindVertexArray(0);

                    buffers.IndexCount = indices.Count;
                    _allocatedBuffers.Add((buffers.Vao, buffers.Vbo, buffers.Ebo));
                }
            }
            else if (buffers.Vao != 0 && part.Geometry?.Geometry is MeshGeometry3D meshAnim && model.CurrentAnimation != null && !model.IsAnimationPaused)
            {
                var positions = meshAnim.Positions;
                var indices = meshAnim.TriangleIndices;
                var texCoords = meshAnim.TextureCoordinates;

                if (positions != null && indices != null)
                {
                    int vertexCount = positions.Count;
                    float[] vertices = new float[vertexCount * 8];
                    Vector3[] normals = new Vector3[vertexCount];

                    for (int i = 0; i < indices.Count; i += 3)
                    {
                        int idx0 = indices[i];
                        int idx1 = indices[i + 1];
                        int idx2 = indices[i + 2];

                        if (idx0 < vertexCount && idx1 < vertexCount && idx2 < vertexCount)
                        {
                            var p0 = positions[idx0];
                            var p1 = positions[idx1];
                            var p2 = positions[idx2];

                            Vector3 v0 = new Vector3((float)(p1.X - p0.X), (float)(p1.Y - p0.Y), (float)(p1.Z - p0.Z));
                            Vector3 v1 = new Vector3((float)(p2.X - p0.X), (float)(p2.Y - p0.Y), (float)(p2.Z - p0.Z));
                            Vector3 normal = Vector3.Normalize(Vector3.Cross(v0, v1));

                            normals[idx0] += normal;
                            normals[idx1] += normal;
                            normals[idx2] += normal;
                        }
                    }

                    for (int i = 0; i < vertexCount; i++)
                    {
                        var p = positions[i];
                        vertices[i * 8 + 0] = (float)p.X;
                        vertices[i * 8 + 1] = (float)p.Y;
                        vertices[i * 8 + 2] = (float)p.Z;

                        var norm = normals[i].LengthSquared() > 0f ? Vector3.Normalize(normals[i]) : Vector3.UnitY;
                        vertices[i * 8 + 3] = norm.X;
                        vertices[i * 8 + 4] = norm.Y;
                        vertices[i * 8 + 5] = norm.Z;

                        if (texCoords != null && i < texCoords.Count)
                        {
                            var uv = texCoords[i];
                            vertices[i * 8 + 6] = (float)uv.X;
                            vertices[i * 8 + 7] = (float)uv.Y;
                        }
                    }

                    _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffers.Vbo);
                    _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, new ReadOnlySpan<float>(vertices));
                    _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
                }
            }

            return buffers;
        }

        private uint UploadTexture(BitmapSource bitmap)
        {
            if (bitmap.Format != System.Windows.Media.PixelFormats.Bgra32)
            {
                var converted = new System.Windows.Media.Imaging.FormatConvertedBitmap();
                converted.BeginInit();
                converted.Source = bitmap;
                converted.DestinationFormat = System.Windows.Media.PixelFormats.Bgra32;
                converted.EndInit();
                bitmap = converted;
            }
            int width = bitmap.PixelWidth;
            int height = bitmap.PixelHeight;
            int stride = width * 4;
            byte[] pixels = new byte[height * stride];
            bitmap.CopyPixels(pixels, stride, 0);

            uint tex = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, tex);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, new ReadOnlySpan<byte>(pixels));
            _gl.GenerateMipmap(TextureTarget.Texture2D);

            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            _gl.BindTexture(TextureTarget.Texture2D, 0);

            _allocatedTextures.Add(tex);
            return tex;
        }

        public void Dispose()
        {
            if (!_ready) return;

            foreach (var tex in _allocatedTextures)
            {
                _gl.DeleteTexture(tex);
            }
            _allocatedTextures.Clear();

            foreach (var (vao, vbo, ebo) in _allocatedBuffers)
            {
                _gl.DeleteVertexArray(vao);
                _gl.DeleteBuffer(vbo);
                _gl.DeleteBuffer(ebo);
            }
            _allocatedBuffers.Clear();

            if (_whiteTex != 0) _gl.DeleteTexture(_whiteTex);
            if (_program != 0) _gl.DeleteProgram(_program);

            _ready = false;
        }

        private const string MeshVert = @"
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUv;
uniform mat4 uViewProj;
uniform mat4 uWorld;
out vec3 vNormal;
out vec2 vUv;
void main(){
    vec4 worldPos = uWorld * vec4(aPos, 1.0);
    gl_Position = uViewProj * worldPos;
    vNormal = normalize(mat3(uWorld) * aNormal);
    vUv = aUv;
}";

        private const string MeshFrag = @"
in vec3 vNormal;
in vec2 vUv;
uniform sampler2D uTex;
uniform vec3 uLightDir;
uniform vec3 uLightColor;
uniform vec3 uLightDir2;
uniform vec3 uLightColor2;
uniform vec3 uAmbient;
out vec4 fragColor;
void main(){
    vec4 texColor = texture(uTex, vUv);
    if (texColor.a < 0.1) discard;
    
    // Light 1 (Key Light)
    float diff1 = max(dot(vNormal, uLightDir), 0.0);
    vec3 diffuse1 = diff1 * uLightColor;
    
    // Light 2 (Fill Light)
    float diff2 = max(dot(vNormal, uLightDir2), 0.0);
    vec3 diffuse2 = diff2 * uLightColor2;
    
    vec3 finalLight = clamp(uAmbient + diffuse1 + diffuse2, 0.0, 1.0);
    fragColor = vec4(texColor.rgb * finalLight, texColor.a);
}";
    }
}
