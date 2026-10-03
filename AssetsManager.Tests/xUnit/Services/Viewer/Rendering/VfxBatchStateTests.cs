using System;
using System.Numerics;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    // Keep desktop GL initialization separate from WPF dispatcher initialization in other tests.
    [CollectionDefinition("Viewport native graphics", DisableParallelization = true)]
    public sealed class ViewportNativeGraphicsCollection { }

    [Collection("Viewport native graphics")]
    public sealed class VfxBatchStateTests
    {
        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public void MixedMeshAndQuadPassesRestoreCallerState(bool batch, bool culling)
        {
            using var context = new HiddenWglContext();
            using GL gl = GL.GetApi(context.GetProcAddress);
            using var renderer = new VfxOpenGlRenderer();
            renderer.Initialize(gl);
            var definition = new VfxEmitterDefinition("test", VfxCurveF.Const(1), VfxCurveF.Const(1),
                null, 0, 0, false, false, 1, VfxCurve3.Const(Vector3.One), null,
                VfxCurve4.Const(Vector4.One), null, null, null, null, VfxCurve3.Const(Vector3.Zero),
                null, Vector2.One, 1, false, false,
                PaletteDefinition: new VfxPaletteDefinition(0, VfxCurve3.Const(Vector3.Zero)));
            float[] instance = new float[VfxPlaybackRuntime.InstanceStride];
            instance[3] = instance[4] = instance[18] = 0.5f;
            instance[5] = instance[6] = instance[7] = instance[8] = 1;
            instance[21] = instance[22] = instance[31] = instance[32] = 1;
            instance[36] = instance[40] = instance[44] = 1;
            var mesh = new VfxPlaybackRuntime.EmitterState
            {
                Def = definition with { IsMeshPrimitive = true, PrimitiveKind = VfxPrimitiveKind.Mesh },
                Instances = instance, InstanceCount = 1
            };
            renderer.UploadEmitterMesh(mesh,
                new float[] { -1, -1, 0, 1, -1, 0, 0, 1, 0 },
                new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 },
                new float[] { 0, 0, 1, 0, 0.5f, 1 }, null);
            var quad = new VfxPlaybackRuntime.EmitterState { Def = definition, Instances = instance, InstanceCount = 1 };
            var queue = new[] { new VfxRenderQueueEntry(mesh, 0, 0), new VfxRenderQueueEntry(quad, 0, 1) };
            uint caller = GlShaderCompiler.CreateProgram(gl, false,
                "void main() { gl_Position = vec4(0.0); }",
                "out vec4 color; void main() { color = vec4(1.0); }");
            try
            {
                gl.UseProgram(caller);
                if (culling) gl.Enable(EnableCap.CullFace); else gl.Disable(EnableCap.CullFace);
                gl.CullFace(TriangleFace.Front);
                gl.FrontFace(FrontFaceDirection.CW);
                using (batch ? renderer.BeginRenderBatch() : null)
                {
                    renderer.Render(queue, Matrix4x4.Identity, Matrix4x4.Identity);
                    renderer.Render(queue, Matrix4x4.Identity, Matrix4x4.Identity, wireframePass: true);
                }
                Assert.Equal(culling, gl.IsEnabled(EnableCap.CullFace));
                gl.GetInteger(GLEnum.CullFaceMode, out int face);
                Assert.Equal((int)TriangleFace.Front, face);
                gl.GetInteger(GLEnum.FrontFace, out int winding);
                Assert.Equal((int)FrontFaceDirection.CW, winding);
                gl.GetInteger(GLEnum.CurrentProgram, out int active);
                Assert.Equal(caller, (uint)active);
                Assert.Equal(GLEnum.NoError, gl.GetError());
            }
            finally { gl.UseProgram(0); gl.DeleteProgram(caller); }
        }
    }
}
