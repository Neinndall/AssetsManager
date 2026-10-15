using System;
using System.Linq;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Rendering.Core;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Tests.Support;
using AssetsManager.Utils.Rendering;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering
{
    /// <summary>
    /// A submesh without a material draws like the game's LIT_UBER: texels with alpha 0 are discarded and the rest
    /// blend by their alpha (Yunara skin10's chick eyes over a white, fully transparent background).
    /// </summary>
    public sealed class TextureOnlyAlphaRenderTests
    {
        private const int TextureSize = 64;
        private static readonly byte[] Face = { 220, 210, 235 };

        [Theory]
        [InlineData(true, true, true, ModelMaterialBlendMode.Normal, true)]
        [InlineData(false, true, true, ModelMaterialBlendMode.Normal, false)]
        [InlineData(true, false, true, ModelMaterialBlendMode.Normal, false)]
        [InlineData(true, true, false, ModelMaterialBlendMode.Normal, false)]
        [InlineData(true, true, true, ModelMaterialBlendMode.Additive, false)]
        [InlineData(true, true, true, ModelMaterialBlendMode.Modulate, false)]
        public void OrderedSkinSurfacesPrecedeFacialLayersWithoutPromotingOtherTransparency(
            bool ordered, bool depthWrite, bool depthTest, ModelMaterialBlendMode blend, bool inOrder)
        {
            var material = ModelMaterialDefinition.TextureOnly("head") with
            {
                BindingKind = ModelMaterialBindingKind.Authored,
                HasSubmeshRenderOrder = ordered,
                RenderState = ModelMaterialRenderState.TextureOnly with
                {
                    Blending = blend, DepthWrite = depthWrite, DepthTest = depthTest
                }
            };
            Assert.Equal(inOrder, material.BlendsInDrawOrder);
            Assert.Equal(!inOrder, material.DrawsInTransparentQueue);

            var pass = GameMaterialPassState.Default with
            {
                BlendEnabled = true,
                SourceColor = blend == ModelMaterialBlendMode.Modulate ? MapBlendFactor.OneMinusSourceColor : MapBlendFactor.SourceAlpha,
                DestinationColor = blend == ModelMaterialBlendMode.Additive ? MapBlendFactor.One
                    : blend == ModelMaterialBlendMode.Modulate ? MapBlendFactor.Zero : MapBlendFactor.OneMinusSourceAlpha,
                DepthEnabled = depthTest,
                WriteMask = depthWrite ? 31u : 15u
            };
            Assert.Equal(inOrder, material.BlendsPassInDrawOrder(pass));
            // A later glow pass keeps its own queue even when the base surface writes depth.
            Assert.False(material.BlendsPassInDrawOrder(pass with { DestinationColor = MapBlendFactor.One }));
            Assert.False(material.BlendsPassInDrawOrder(pass with { WriteMask = 15u }));
        }

        [Fact]
        public void MaterialLessSubmeshesBlendTheirTextureAlphaAndWriteDepth()
        {
            ModelMaterialDefinition material = ModelMaterialDefinition.TextureOnly("eyes");
            Assert.Equal(ModelMaterialBlendMode.Normal, material.RenderState.Blending);
            Assert.True(material.RenderState.DepthWrite);
            Assert.False(material.DrawsInTransparentQueue);
            Assert.False(new ModelPart { MaterialDefinition = material }.IsAlphaBlended);
            Assert.True(material.UsesTextureAlpha);
            Assert.InRange(material.AlphaCutoff, float.Epsilon, 1f / 255f);
            Assert.Equal(ModelMaterialBlendMode.Opaque, ModelMaterialDefinition.Missing.RenderState.Blending);

            GameMaterialPassState state = GameShaderProgramResolver.CreateDefaultSkinnedProgram("eyes").Passes[0].State;
            Assert.True(state.BlendEnabled);
            Assert.Equal(MapBlendFactor.SourceAlpha, state.SourceColor);
            Assert.Equal(MapBlendFactor.OneMinusSourceAlpha, state.DestinationColor);
            Assert.Equal(31u, state.WriteMask);
        }

        [Theory]
        [InlineData(64)]
        [InlineData(16)]
        public void EyeOverTheFaceShowsNeitherItsBackgroundNorAWhiteRim(int target)
        {
            byte[] blended = Draw(target, blend: true);
            byte[] opaque = Draw(target, blend: false);
            int[] pixels = Enumerable.Range(0, target * target).ToArray();

            Assert.Contains(pixels, at => blended[at * 4 + 1] < 40);
            Assert.All(pixels, at => Assert.True(blended[at * 4 + 1] <= Face[1] + 8,
                $"pixel {at} green={blended[at * 4 + 1]} is whiter than the face"));

            // Drawn opaque, the texels kept at a sliver of alpha show the white hidden under the background.
            Assert.Contains(pixels, at => opaque[at * 4 + 1] > Face[1] + 8);
        }

        /// <returns>RGBA pixels of an opaque red disc on a transparent white background drawn over the face colour.</returns>
        private static byte[] Draw(int target, bool blend)
        {
            ModelMaterialDefinition material = ModelMaterialDefinition.TextureOnly("eyes");
            using var context = new HiddenWglContext();
            using var gl = GL.GetApi(context.GetProcAddress);
            uint program = GlShaderCompiler.CreateProgram(gl, false, GlMeshShaderSource.Vertex, GlMeshShaderSource.Fragment);

            uint colour = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, colour);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)target, (uint)target, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, new ReadOnlySpan<byte>(new byte[target * target * 4]));
            uint framebuffer = gl.GenFramebuffer();
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, colour, 0);

            // A disc with a one-texel outline at a sliver of alpha, as block compression leaves around the eyes.
            byte[] texels = new byte[TextureSize * TextureSize * 4];
            for (int y = 0; y < TextureSize; y++)
            {
                for (int x = 0; x < TextureSize; x++)
                {
                    int at = (y * TextureSize + x) * 4;
                    float distance = MathF.Sqrt(MathF.Pow(x + 0.5f - 32f, 2) + MathF.Pow(y + 0.5f - 32f, 2));
                    bool eye = distance < 14f;
                    texels[at] = 255;
                    texels[at + 1] = eye ? (byte)0 : (byte)255;
                    texels[at + 2] = eye ? (byte)0 : (byte)255;
                    texels[at + 3] = eye ? (byte)255 : distance < 15f ? (byte)17 : (byte)0;
                }
            }
            uint texture = gl.GenTexture();
            gl.BindTexture(TextureTarget.Texture2D, texture);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, TextureSize, TextureSize, 0, PixelFormat.Rgba,
                PixelType.UnsignedByte, new ReadOnlySpan<byte>(texels));
            gl.GenerateMipmap(TextureTarget.Texture2D);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

            float[] positions = { -1f, -1f, 0f, 1f, -1f, 0f, -1f, 1f, 0f, 1f, 1f, 0f };
            float[] uvs = { 0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f };
            uint vao = gl.GenVertexArray();
            gl.BindVertexArray(vao);
            uint positionBuffer = Attribute(gl, 0, 3, positions);
            uint uvBuffer = Attribute(gl, 2, 2, uvs);
            gl.VertexAttrib3(1, 0f, 0f, 1f);

            gl.UseProgram(program);
            float[] identity = { 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f };
            gl.UniformMatrix4(gl.GetUniformLocation(program, "uViewProj"), 1, false, new ReadOnlySpan<float>(identity));
            gl.UniformMatrix4(gl.GetUniformLocation(program, "uWorld"), 1, false, new ReadOnlySpan<float>(identity));
            gl.Uniform1(gl.GetUniformLocation(program, "uTex"), 0);
            gl.Uniform4(gl.GetUniformLocation(program, "uColorTint"), 1f, 1f, 1f, 1f);
            gl.Uniform2(gl.GetUniformLocation(program, "uMaterialUvRepeat"), 1f, 1f);
            gl.Uniform1(gl.GetUniformLocation(program, "uMaterialUnlit"), 1);
            gl.Uniform1(gl.GetUniformLocation(program, "uAlphaCutoff"), material.AlphaCutoff);
            gl.Uniform1(gl.GetUniformLocation(program, "uMaterialUsesTextureAlpha"), material.UsesTextureAlpha ? 1 : 0);

            gl.Viewport(0, 0, (uint)target, (uint)target);
            gl.ClearColor(Face[0] / 255f, Face[1] / 255f, Face[2] / 255f, 1f);
            gl.Clear(ClearBufferMask.ColorBufferBit);
            gl.Disable(EnableCap.DepthTest);
            if (blend)
            {
                gl.Enable(EnableCap.Blend);
                gl.BlendFuncSeparate(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha, BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
            }
            else
            {
                gl.Disable(EnableCap.Blend);
            }
            gl.ActiveTexture(TextureUnit.Texture0);
            gl.BindTexture(TextureTarget.Texture2D, texture);
            gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

            byte[] pixels = new byte[target * target * 4];
            gl.ReadPixels(0, 0, (uint)target, (uint)target, PixelFormat.Rgba, PixelType.UnsignedByte, pixels.AsSpan());
            Assert.Equal(GLEnum.NoError, gl.GetError());

            gl.DeleteBuffer(positionBuffer);
            gl.DeleteBuffer(uvBuffer);
            gl.DeleteVertexArray(vao);
            gl.DeleteTexture(texture);
            gl.DeleteFramebuffer(framebuffer);
            gl.DeleteTexture(colour);
            gl.DeleteProgram(program);
            return pixels;
        }

        private static uint Attribute(GL gl, uint location, int components, float[] values)
        {
            uint buffer = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer);
            gl.BufferData(BufferTargetARB.ArrayBuffer, new ReadOnlySpan<float>(values), BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(location);
            gl.VertexAttribPointer(location, components, VertexAttribPointerType.Float, false, 0, IntPtr.Zero);
            return buffer;
        }
    }
}
