using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Silk.NET.OpenGL;
using AssetsManager.Views.Models.Viewer;
using AssetsManager.Utils.Rendering;
using AssetsManager.Utils;
using System.Linq;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Handles 3D mesh uploads, texture generation, and drawing for character models using Silk.NET.
    /// Uses ConditionalWeakTable to bind GL buffers to existing WPF ModelPart instances without modification.
    /// </summary>
    public sealed class GlMeshRenderer : IDisposable
    {
        private const float DefaultLightmapEmissionScale = 0.1f;

        private sealed class GlPartBuffers
        {
            public uint Vao;
            public uint Vbo;
            public uint Ebo;
            public int IndexCount;
            public uint Texture;
            public bool TextureResolved;
            public string LoadedTextureKey;
            public BitmapSource LoadedBitmap;
            public uint LightmapTexture;
            public bool LightmapTextureResolved;
            public string LoadedLightmapTextureKey;
            public BitmapSource LoadedLightmapBitmap;
            public uint LightmapVbo;
            public Point3DCollection UploadedPositions;
            public int VertexCount;
            public GlMeshVertexData VertexData;
        }

        private sealed class SharedTexture
        {
            public uint Id;
            public int ReferenceCount;
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
        private int _uLightmap;
        private int _uHasLightmap;
        private int _uLightMapColorScale;
        private bool _ready;

        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
        private delegate void DrawElementsDelegate(uint mode, int count, uint type, IntPtr indices);
        private DrawElementsDelegate _drawElements = null!;

        private readonly ConditionalWeakTable<ModelPart, GlPartBuffers> _partBuffers = new();
        private readonly HashSet<GlPartBuffers> _livePartBuffers = new();
        private readonly Dictionary<BitmapSource, SharedTexture> _sharedTextures =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<BitmapSource, SharedTexture> _sharedLightmapTextures =
            new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<ModelPart> _pendingReleases = new();
        private uint _whiteTex;

        public void Initialize(GL gl)
        {
            _gl = gl;
            var proc = gl.Context.GetProcAddress("glDrawElements");
            if (proc != IntPtr.Zero)
            {
                _drawElements = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<DrawElementsDelegate>(proc);
            }
            bool gles = GlShaderCompiler.UsesEmbeddedProfile(gl);
            _program = GlShaderCompiler.CreateProgram(gl, gles, MeshVert, MeshFrag);
            _uViewProj = gl.GetUniformLocation(_program, "uViewProj");
            _uWorld = gl.GetUniformLocation(_program, "uWorld");
            _uTex = gl.GetUniformLocation(_program, "uTex");
            _uLightDir = gl.GetUniformLocation(_program, "uLightDir");
            _uLightColor = gl.GetUniformLocation(_program, "uLightColor");
            _uLightDir2 = gl.GetUniformLocation(_program, "uLightDir2");
            _uLightColor2 = gl.GetUniformLocation(_program, "uLightColor2");
            _uAmbient = gl.GetUniformLocation(_program, "uAmbient");
            _uLightmap = gl.GetUniformLocation(_program, "uLightmap");
            _uHasLightmap = gl.GetUniformLocation(_program, "uHasLightmap");
            _uLightMapColorScale = gl.GetUniformLocation(_program, "uLightMapColorScale");

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

        public void Render(
            SceneModel model,
            Matrix4x4 viewProj,
            Vector3 lightDir,
            Vector3 lightColor,
            Vector3 lightDir2,
            Vector3 lightColor2,
            Vector3 ambientColor)
        {
            if (!_ready || model == null || !model.IsVisible) return;

            MapLightingProfile mapLighting = model.MapLightingProfile;
            float lightMapColorScale = DefaultLightmapEmissionScale;
            if (mapLighting != null)
            {
                lightDir = mapLighting.SunDirection;
                lightColor = mapLighting.SunColor;
                lightDir2 = Vector3.UnitY;
                lightColor2 = Vector3.Zero;
                ambientColor = mapLighting.AmbientColor;
                lightMapColorScale *= mapLighting.LightMapColorScale;
            }

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
            _gl.Uniform3(_uLightDir, NormalizeOrDefault(lightDir));
            _gl.Uniform3(_uLightColor, lightColor);
            _gl.Uniform3(_uLightDir2, NormalizeOrDefault(lightDir2));
            _gl.Uniform3(_uLightColor2, lightColor2);
            _gl.Uniform3(_uAmbient, ambientColor);
            _gl.Uniform1(_uTex, 0);
            _gl.Uniform1(_uLightmap, 1);
            _gl.Uniform1(_uLightMapColorScale, lightMapColorScale);

            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);

            RenderParts(model, renderDecals: false);
            RenderParts(model, renderDecals: true);

            _gl.Disable(EnableCap.PolygonOffsetFill);
            _gl.BindVertexArray(0);
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        private static Vector3 NormalizeOrDefault(Vector3 value) =>
            value.LengthSquared() > 1e-6f ? Vector3.Normalize(value) : Vector3.UnitY;

        private void RenderParts(SceneModel model, bool renderDecals)
        {
            if (renderDecals)
            {
                _gl.Enable(EnableCap.PolygonOffsetFill);
                _gl.PolygonOffset(-1f, -1f);
            }
            else
            {
                _gl.Disable(EnableCap.PolygonOffsetFill);
            }

            foreach (var part in model.Parts)
            {
                if (!part.IsVisible || part.IsDecal != renderDecals) continue;

                var buffers = EnsureBuffers(part);
                if (buffers.Vao == 0) continue;

                _gl.BindVertexArray(buffers.Vao);
                _gl.ActiveTexture(TextureUnit.Texture0);

                uint tex = buffers.Texture != 0 ? buffers.Texture : _whiteTex;
                _gl.BindTexture(TextureTarget.Texture2D, tex);

                bool hasLightmap = buffers.LightmapTexture != 0 && buffers.LightmapVbo != 0;
                _gl.Uniform1(_uHasLightmap, hasLightmap ? 1 : 0);
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(TextureTarget.Texture2D, hasLightmap ? buffers.LightmapTexture : 0);
                _gl.ActiveTexture(TextureUnit.Texture0);

                if (_drawElements != null)
                {
                    _drawElements((uint)PrimitiveType.Triangles, buffers.IndexCount, (uint)DrawElementsType.UnsignedInt, IntPtr.Zero);
                }
            }
        }

        private GlPartBuffers EnsureBuffers(ModelPart part)
        {
            if (!_partBuffers.TryGetValue(part, out var buffers))
            {
                buffers = new GlPartBuffers();
                _partBuffers.Add(part, buffers);
                _livePartBuffers.Add(buffers);
            }

            EnsureTexture(part, buffers);
            EnsureLightmapTexture(part, buffers);

            // Build VAO/VBO/EBO if not already created
            if (buffers.Vao == 0 && part.Geometry?.Geometry is MeshGeometry3D mesh)
            {
                var positions = mesh.Positions;
                var indices = mesh.TriangleIndices;

                if (positions != null && indices != null)
                {
                    int vertexCount = positions.Count;
                    var vertexData = new GlMeshVertexData(vertexCount);
                    vertexData.Update(mesh, updateTextureCoordinates: true);
                    bool retainsVertexData = part.SourceVertexIndices != null;
                    buffers.VertexCount = vertexCount;
                    buffers.VertexData = retainsVertexData ? vertexData : null;
                    buffers.UploadedPositions = positions;

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
                    _gl.BufferData(
                        BufferTargetARB.ArrayBuffer,
                        new ReadOnlySpan<float>(vertexData.Data),
                        retainsVertexData ? BufferUsageARB.DynamicDraw : BufferUsageARB.StaticDraw);

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
                }
            }
            else if (buffers.Vao != 0 &&
                     buffers.VertexData != null &&
                     part.Geometry?.Geometry is MeshGeometry3D meshAnim &&
                     !ReferenceEquals(buffers.UploadedPositions, meshAnim.Positions))
            {
                var positions = meshAnim.Positions;

                if (positions != null)
                {
                    if (positions.Count != buffers.VertexData.VertexCount)
                    {
                        ReleasePart(part);
                        return EnsureBuffers(part);
                    }

                    buffers.VertexData.Update(meshAnim, updateTextureCoordinates: false);

                    _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffers.Vbo);
                    _gl.BufferSubData(
                        BufferTargetARB.ArrayBuffer,
                        0,
                        new ReadOnlySpan<float>(buffers.VertexData.Data));
                    _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
                    buffers.UploadedPositions = positions;
                }
            }

            EnsureLightmapVertexBuffer(part, buffers);

            return buffers;
        }

        private void EnsureLightmapVertexBuffer(ModelPart part, GlPartBuffers buffers)
        {
            float[] coordinates = part.Lightmap?.UvCoordinates;
            if (buffers.Vao == 0 || buffers.LightmapVbo != 0 ||
                coordinates == null ||
                coordinates.Length != buffers.VertexCount * 2)
            {
                return;
            }

            buffers.LightmapVbo = _gl.GenBuffer();
            _gl.BindVertexArray(buffers.Vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffers.LightmapVbo);
            _gl.BufferData(
                BufferTargetARB.ArrayBuffer,
                new ReadOnlySpan<float>(coordinates),
                BufferUsageARB.StaticDraw);
            _gl.EnableVertexAttribArray(3);
            _gl.VertexAttribPointer(
                3,
                2,
                VertexAttribPointerType.Float,
                false,
                2 * sizeof(float),
                IntPtr.Zero);
            _gl.BindVertexArray(0);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
        }

        private void EnsureTexture(ModelPart part, GlPartBuffers buffers)
        {
            string selectedTexture = part.SelectedTextureName;
            if (buffers.TextureResolved && buffers.LoadedTextureKey == selectedTexture)
            {
                return;
            }

            BitmapSource bitmap = TextureUtils.ResolveTexture(part.AllTextures, selectedTexture);
            ReleaseBaseTexture(buffers);
            buffers.TextureResolved = true;
            buffers.LoadedTextureKey = selectedTexture;
            buffers.LoadedBitmap = bitmap;
            if (bitmap == null) return;

            if (!_sharedTextures.TryGetValue(bitmap, out SharedTexture sharedTexture))
            {
                sharedTexture = new SharedTexture { Id = UploadTexture(bitmap) };
                _sharedTextures.Add(bitmap, sharedTexture);
            }

            sharedTexture.ReferenceCount++;
            buffers.Texture = sharedTexture.Id;
        }

        private void EnsureLightmapTexture(ModelPart part, GlPartBuffers buffers)
        {
            string selectedTexture = part.Lightmap?.TextureKey;
            if (buffers.LightmapTextureResolved && buffers.LoadedLightmapTextureKey == selectedTexture)
                return;

            ReleaseLightmapTexture(buffers);
            buffers.LightmapTextureResolved = true;
            buffers.LoadedLightmapTextureKey = selectedTexture;

            BitmapSource bitmap = TextureUtils.ResolveTexture(part.AllTextures, selectedTexture);
            buffers.LoadedLightmapBitmap = bitmap;
            if (bitmap == null) return;

            if (!_sharedLightmapTextures.TryGetValue(bitmap, out SharedTexture sharedTexture))
            {
                sharedTexture = new SharedTexture
                {
                    Id = UploadTexture(
                        bitmap,
                        premultiplyAlpha: false,
                        wrapMode: TextureWrapMode.ClampToEdge)
                };
                _sharedLightmapTextures.Add(bitmap, sharedTexture);
            }

            sharedTexture.ReferenceCount++;
            buffers.LightmapTexture = sharedTexture.Id;
        }

        private void ReleaseTexture(GlPartBuffers buffers)
        {
            ReleaseBaseTexture(buffers);
            ReleaseLightmapTexture(buffers);
        }

        private void ReleaseBaseTexture(GlPartBuffers buffers)
        {
            ReleaseSharedTexture(_sharedTextures, buffers.LoadedBitmap);
            buffers.Texture = 0;
            buffers.LoadedBitmap = null;
        }

        private void ReleaseLightmapTexture(GlPartBuffers buffers)
        {
            ReleaseSharedTexture(_sharedLightmapTextures, buffers.LoadedLightmapBitmap);
            buffers.LightmapTexture = 0;
            buffers.LoadedLightmapBitmap = null;
        }

        private void ReleaseSharedTexture(
            Dictionary<BitmapSource, SharedTexture> textures,
            BitmapSource bitmap)
        {
            if (bitmap == null || !textures.TryGetValue(bitmap, out SharedTexture sharedTexture))
                return;

            sharedTexture.ReferenceCount--;
            if (sharedTexture.ReferenceCount == 0)
            {
                _gl.DeleteTexture(sharedTexture.Id);
                textures.Remove(bitmap);
            }
        }

        public void QueueRelease(SceneModel model)
        {
            if (model?.Parts == null) return;
            foreach (ModelPart part in model.Parts)
            {
                _pendingReleases.Add(part);
            }
        }

        public void ProcessPendingReleases()
        {
            if (!_ready || _pendingReleases.Count == 0) return;
            foreach (ModelPart part in _pendingReleases)
            {
                ReleasePart(part);
            }
            _pendingReleases.Clear();
        }

        private void ReleasePart(ModelPart part)
        {
            if (!_partBuffers.TryGetValue(part, out GlPartBuffers buffers)) return;

            ReleaseTexture(buffers);
            if (buffers.Vao != 0) _gl.DeleteVertexArray(buffers.Vao);
            if (buffers.Vbo != 0) _gl.DeleteBuffer(buffers.Vbo);
            if (buffers.Ebo != 0) _gl.DeleteBuffer(buffers.Ebo);
            if (buffers.LightmapVbo != 0) _gl.DeleteBuffer(buffers.LightmapVbo);

            _livePartBuffers.Remove(buffers);
            _partBuffers.Remove(part);
        }

        private uint UploadTexture(
            BitmapSource bitmap,
            bool premultiplyAlpha = true,
            TextureWrapMode wrapMode = TextureWrapMode.Repeat)
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
            if (premultiplyAlpha)
                PremultiplyBgra(pixels);

            uint tex = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, tex);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)width, (uint)height, 0, PixelFormat.Bgra, PixelType.UnsignedByte, new ReadOnlySpan<byte>(pixels));
            _gl.GenerateMipmap(TextureTarget.Texture2D);

            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)wrapMode);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)wrapMode);
            _gl.BindTexture(TextureTarget.Texture2D, 0);

            return tex;
        }

        internal static void PremultiplyBgra(Span<byte> pixels)
        {
            for (int i = 0; i + 3 < pixels.Length; i += 4)
            {
                int alpha = pixels[i + 3];
                pixels[i] = (byte)((pixels[i] * alpha + 127) / 255);
                pixels[i + 1] = (byte)((pixels[i + 1] * alpha + 127) / 255);
                pixels[i + 2] = (byte)((pixels[i + 2] * alpha + 127) / 255);
            }
        }

        public void Dispose()
        {
            if (!_ready) return;

            foreach (SharedTexture texture in _sharedTextures.Values)
            {
                _gl.DeleteTexture(texture.Id);
            }
            _sharedTextures.Clear();

            foreach (SharedTexture texture in _sharedLightmapTextures.Values)
            {
                _gl.DeleteTexture(texture.Id);
            }
            _sharedLightmapTextures.Clear();

            foreach (GlPartBuffers buffers in _livePartBuffers)
            {
                if (buffers.Vao != 0) _gl.DeleteVertexArray(buffers.Vao);
                if (buffers.Vbo != 0) _gl.DeleteBuffer(buffers.Vbo);
                if (buffers.Ebo != 0) _gl.DeleteBuffer(buffers.Ebo);
                if (buffers.LightmapVbo != 0) _gl.DeleteBuffer(buffers.LightmapVbo);
            }
            _livePartBuffers.Clear();
            _pendingReleases.Clear();

            if (_whiteTex != 0) _gl.DeleteTexture(_whiteTex);
            if (_program != 0) _gl.DeleteProgram(_program);

            _ready = false;
        }

        private const string MeshVert = @"
					layout(location=0) in vec3 aPos;
					layout(location=1) in vec3 aNormal;
					layout(location=2) in vec2 aUv;
					layout(location=3) in vec2 aLightmapUv;
					uniform mat4 uViewProj;
					uniform mat4 uWorld;
					out vec3 vNormal;
					out vec2 vUv;
					out vec2 vLightmapUv;
					void main(){
							vec4 worldPos = uWorld * vec4(aPos, 1.0);
							gl_Position = uViewProj * worldPos;
							vNormal = normalize(mat3(uWorld) * aNormal);
							vUv = aUv;
							vLightmapUv = aLightmapUv;
				}";

        private const string MeshFrag = @"
					in vec3 vNormal;
					in vec2 vUv;
					in vec2 vLightmapUv;
					uniform sampler2D uTex;
					uniform sampler2D uLightmap;
					uniform int uHasLightmap;
					uniform float uLightMapColorScale;
					uniform vec3 uLightDir;
					uniform vec3 uLightColor;
					uniform vec3 uLightDir2;
					uniform vec3 uLightColor2;
					uniform vec3 uAmbient;
					out vec4 fragColor;
					void main(){
							vec4 texColor = texture(uTex, vUv);
							if (texColor.a < 0.1) discard;
							texColor.rgb /= max(texColor.a, 0.0039215686);
							
							// Light 1 (Key Light)
							float diff1 = max(dot(vNormal, uLightDir), 0.0);
							vec3 diffuse1 = diff1 * uLightColor;
							
							// Light 2 (Fill Light)
							float diff2 = max(dot(vNormal, uLightDir2), 0.0);
							vec3 diffuse2 = diff2 * uLightColor2;
							
							vec3 finalLight = clamp(uAmbient + diffuse1 + diffuse2, 0.0, 1.0);
							vec3 finalColor = texColor.rgb * finalLight;
							if (uHasLightmap != 0)
							{
								vec2 lightmapUv = vLightmapUv;
								finalColor += texture(uLightmap, lightmapUv).rgb * uLightMapColorScale;
							}
							fragColor = vec4(finalColor, texColor.a);
				}";
    }
}
