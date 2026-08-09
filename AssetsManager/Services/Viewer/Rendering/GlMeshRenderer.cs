using System;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Rendering
{
    /// <summary>
    /// Draws scene mesh parts using the shared OpenGL resource cache.
    /// </summary>
    public sealed class GlMeshRenderer : IDisposable
    {
        private const float DefaultLightmapEmissionScale = 0.1f;

        private GL _gl = null!;
        private GlMeshResourceCache _resources = null!;
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
        private int _uColorTint;
        private int _uAlphaCutoff;
        private int _uUsesBakedDiffuse;
        private int _uHasVertexColor;
        private bool _ready;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void DrawElementsDelegate(uint mode, int count, uint type, IntPtr indices);

        private DrawElementsDelegate _drawElements = null!;

        public void Initialize(GL gl)
        {
            _gl = gl;
            IntPtr proc = gl.Context.GetProcAddress("glDrawElements");
            if (proc != IntPtr.Zero)
            {
                _drawElements = Marshal.GetDelegateForFunctionPointer<DrawElementsDelegate>(proc);
            }

            bool gles = GlShaderCompiler.UsesEmbeddedProfile(gl);
            _program = GlShaderCompiler.CreateProgram(
                gl,
                gles,
                GlMeshShaderSource.Vertex,
                GlMeshShaderSource.Fragment);
            CacheUniformLocations(gl);
            _resources = new GlMeshResourceCache(gl);
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

            float lightmapScale = DefaultLightmapEmissionScale;
            if (model.MapLightingProfile is MapLightingProfile mapLighting)
            {
                lightDir = mapLighting.SunDirection;
                lightColor = mapLighting.SunColor;
                lightDir2 = Vector3.UnitY;
                lightColor2 = Vector3.Zero;
                ambientColor = mapLighting.AmbientColor;
                lightmapScale *= mapLighting.LightMapColorScale;
            }

            _gl.UseProgram(_program);
            _gl.UniformMatrix4(_uViewProj, 1, false, in viewProj.M11);
            Matrix4x4 world = CreateWorldMatrix(model);
            _gl.UniformMatrix4(_uWorld, 1, false, in world.M11);
            _gl.Uniform3(_uLightDir, NormalizeOrDefault(lightDir));
            _gl.Uniform3(_uLightColor, lightColor);
            _gl.Uniform3(_uLightDir2, NormalizeOrDefault(lightDir2));
            _gl.Uniform3(_uLightColor2, lightColor2);
            _gl.Uniform3(_uAmbient, ambientColor);
            _gl.Uniform1(_uTex, 0);
            _gl.Uniform1(_uLightmap, 1);
            _gl.Uniform1(_uLightMapColorScale, lightmapScale);

            _gl.Enable(EnableCap.DepthTest);
            _gl.DepthMask(true);
            _gl.Disable(EnableCap.Blend);
            RenderParts(model, renderDecals: false, alphaBlended: false);
            RenderParts(model, renderDecals: true, alphaBlended: false);

            _gl.Enable(EnableCap.Blend);
            _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            _gl.DepthMask(false);
            RenderParts(model, renderDecals: false, alphaBlended: true);
            RenderParts(model, renderDecals: true, alphaBlended: true);

            _gl.Disable(EnableCap.PolygonOffsetFill);
            _gl.Disable(EnableCap.Blend);
            _gl.DepthMask(true);
            _gl.BindVertexArray(0);
            UnbindSceneTextures();
        }

        private void CacheUniformLocations(GL gl)
        {
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
            _uColorTint = gl.GetUniformLocation(_program, "uColorTint");
            _uAlphaCutoff = gl.GetUniformLocation(_program, "uAlphaCutoff");
            _uUsesBakedDiffuse = gl.GetUniformLocation(_program, "uUsesBakedDiffuse");
            _uHasVertexColor = gl.GetUniformLocation(_program, "uHasVertexColor");
        }

        private void RenderParts(SceneModel model, bool renderDecals, bool alphaBlended)
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

            foreach (ModelPart part in model.Parts)
            {
                if (!part.IsVisible ||
                    part.IsDecal != renderDecals ||
                    part.IsAlphaBlended != alphaBlended)
                {
                    continue;
                }

                GlMeshResourceCache.PartResources resources = _resources.Ensure(part);
                if (resources.Vao == 0) continue;

                _gl.BindVertexArray(resources.Vao);
                _gl.ActiveTexture(TextureUnit.Texture0);
                _gl.BindTexture(
                    TextureTarget.Texture2D,
                    resources.Texture != 0 ? resources.Texture : _resources.WhiteTexture);
                _gl.Uniform4(_uColorTint, part.ColorTint.X, part.ColorTint.Y, part.ColorTint.Z, part.ColorTint.W);
                _gl.Uniform1(_uAlphaCutoff, part.AlphaCutoff);
                _gl.Uniform1(_uUsesBakedDiffuse, part.UsesBakedDiffuse ? 1 : 0);
                _gl.Uniform1(_uHasVertexColor, resources.ColorVbo != 0 ? 1 : 0);

                bool hasLightmap = resources.LightmapTexture != 0 && resources.LightmapVbo != 0;
                _gl.Uniform1(_uHasLightmap, hasLightmap ? 1 : 0);
                _gl.ActiveTexture(TextureUnit.Texture1);
                _gl.BindTexture(
                    TextureTarget.Texture2D,
                    hasLightmap ? resources.LightmapTexture : 0);
                _gl.ActiveTexture(TextureUnit.Texture0);

                _drawElements?.Invoke(
                    (uint)PrimitiveType.Triangles,
                    resources.IndexCount,
                    (uint)DrawElementsType.UnsignedInt,
                    IntPtr.Zero);
            }
        }

        private void UnbindSceneTextures()
        {
            _gl.ActiveTexture(TextureUnit.Texture1);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
        }

        private static Matrix4x4 CreateWorldMatrix(SceneModel model)
        {
            float pitch = (float)(model.RotationX * (Math.PI / 180.0));
            float yaw = (float)(model.RotationY * (Math.PI / 180.0));
            float roll = (float)(model.RotationZ * (Math.PI / 180.0));
            return Matrix4x4.CreateScale((float)model.Scale) *
                   Matrix4x4.CreateFromYawPitchRoll(yaw, pitch, roll) *
                   Matrix4x4.CreateTranslation(
                       (float)model.PositionX,
                       (float)model.PositionY,
                       (float)model.PositionZ);
        }

        private static Vector3 NormalizeOrDefault(Vector3 value) =>
            value.LengthSquared() > 1e-6f ? Vector3.Normalize(value) : Vector3.UnitY;

        public void QueueRelease(SceneModel model) => _resources?.QueueRelease(model);

        public void ProcessPendingReleases()
        {
            if (_ready)
                _resources.ProcessPendingReleases();
        }

        internal static void PremultiplyBgra(Span<byte> pixels) =>
            GlMeshResourceCache.PremultiplyBgra(pixels);

        public void Dispose()
        {
            if (!_ready) return;

            _resources.Dispose();
            if (_program != 0)
                _gl.DeleteProgram(_program);
            _ready = false;
        }
    }
}
