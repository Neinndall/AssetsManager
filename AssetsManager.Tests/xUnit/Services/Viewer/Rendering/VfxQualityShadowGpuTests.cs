using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Services.Viewer.Rendering;
using AssetsManager.Services.Viewer.Vfx.Rendering;
using AssetsManager.Services.Viewer.Vfx.Runtime;
using AssetsManager.Tests.Support;
using AssetsManager.Tests.xUnit.Services.Viewer.Vfx;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Rendering;

[Collection("Viewport native graphics")]
public sealed class VfxQualityShadowGpuTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void SliceUsesTheThresholdBandAndIgnoresVertexAlpha(bool mesh, byte uvMode)
    {
        using var context=new HiddenWglContext(); using var gl=GL.GetApi(context.GetProcAddress);
        using var renderer=new VfxOpenGlRenderer(); renderer.Initialize(gl);
        uint output=Texture(gl,32,32,new byte[32*32*4]), frame=Frame(gl,output);
        uint sprite=Texture(gl,1,1,[128,64,255,128]), ramp=Texture(gl,1,1,[255,128,0,128]);
        try
        {
            var def=VfxQualityFlexTests.Definition() with { IsMeshPrimitive=mesh,
                PrimitiveKind=mesh?VfxPrimitiveKind.Mesh:VfxPrimitiveKind.CameraQuad,
                BlendMode=3, SliceRange=.2f, UvMode=uvMode, RenderState=VfxEmitterRenderState.Default with { AlphaReference=1,DisableBackfaceCull=true } };
            var state=State(def,Vector3.Zero,.8f);
            state.Texture=sprite; state.ColorGradientTexture=ramp;
            state.Instances[5]=.5f;state.Instances[6]=1;state.Instances[7]=.25f;state.Instances[8]=.01f;
            if(mesh)renderer.UploadEmitterMesh(state,[-1,-1,0,1,-1,0,0,1,0],[0,0,1,0,0,1,0,0,1],[0,0,1,0,.5f,1],null);
            gl.Viewport(0,0,32,32);gl.ClearColor(0,0,0,0);gl.Clear(ClearBufferMask.ColorBufferBit);
            renderer.Render([new(state,0,0)],Matrix4x4.Identity,Matrix4x4.CreateTranslation(0,0,-10));
            var pixel=new byte[4];gl.ReadPixels(16,16,1,1,PixelFormat.Rgba,PixelType.UnsignedByte,pixel.AsSpan());
            Assert.InRange(pixel[0],mesh?127:31,mesh?129:33);
            Assert.InRange(pixel[1],mesh?127:31,mesh?129:33);
            Assert.Equal(0,pixel[2]);Assert.InRange(pixel[3],127,129);
            var failed = new byte[] {255,255,255,255};
            gl.BindTexture(TextureTarget.Texture2D,ramp);
            gl.TexSubImage2D(TextureTarget.Texture2D,0,0,0,1,1,PixelFormat.Rgba,PixelType.UnsignedByte,failed.AsSpan());
            gl.Clear(ClearBufferMask.ColorBufferBit);
            renderer.Render([new(state,0,0)],Matrix4x4.Identity,Matrix4x4.CreateTranslation(0,0,-10));
            gl.ReadPixels(16,16,1,1,PixelFormat.Rgba,PixelType.UnsignedByte,pixel.AsSpan());
            Assert.Equal(new byte[]{0,0,0,0},pixel);
            Assert.Equal(GLEnum.NoError,gl.GetError());
        }
        finally{gl.BindFramebuffer(FramebufferTarget.Framebuffer,0);gl.DeleteFramebuffer(frame);gl.DeleteTexture(output);gl.DeleteTexture(sprite);gl.DeleteTexture(ramp);}
    }

    [Fact]
    public void ParticleShadowsDarkenTheStageAndDisappearWithCoverageVisibilityOrQuality()
    {
        using var context=new HiddenWglContext();using var gl=GL.GetApi(context.GetProcAddress);
        using var renderer=new VfxOpenGlRenderer();renderer.Initialize(gl);
        using var shadow=new VfxParticleShadowScene(gl);
        using var surface=new PreviewSurfaceRenderer();surface.Initialize(gl,new(null));surface.Shadows=shadow;
        uint output=Texture(gl,64,64,new byte[64*64*4]), frame=Frame(gl,output);
        uint sprite=Texture(gl,1,1,[255,255,255,255]);
        try
        {
            Vector3 target=new(80-100f/3f,0,60+100f/15f);
            Matrix4x4 view=Matrix4x4.CreateLookAt(target+new Vector3(0,1000,700),target,Vector3.UnitY);
            var projection=Matrix4x4.CreateOrthographic(600,600,1,5000);
            var state=State(VfxQualityFlexTests.Definition() with { DoesCastShadow=true, BlendMode=3,
                RenderState=VfxEmitterRenderState.Default with { AlphaReference=255 } },new(80,100,60),80);
            state.Texture=sprite;
            var pass=new ShadowPass(renderer,[new(state,0,0)]);
            byte[] Draw(bool enabled)
            {
                pass.Enabled=enabled;
                gl.BindFramebuffer(FramebufferTarget.Framebuffer,frame);gl.Viewport(0,0,64,64);
                shadow.Render([pass],view);
                gl.GetInteger(GLEnum.DrawFramebufferBinding,out int bound);Assert.Equal((int)frame,bound);
                int[] viewport=new int[4];gl.GetInteger(GLEnum.Viewport,viewport);Assert.Equal([0,0,64,64],viewport);
                gl.ClearColor(0,0,0,0);gl.Clear(ClearBufferMask.ColorBufferBit|ClearBufferMask.DepthBufferBit);
                surface.Render(view*projection,false,false,true);
                var pixel=new byte[4];gl.ReadPixels(32,32,1,1,PixelFormat.Rgba,PixelType.UnsignedByte,pixel.AsSpan());
                return pixel;
            }
            byte[] lit=Draw(false), shaded=Draw(true);
            Assert.True(shaded[0]<lit[0],$"Stage did not receive a particle shadow: {string.Join(',',lit)} / {string.Join(',',shaded)}");
            Assert.InRange(shaded[0]/(float)lit[0],.5f,.7f);
            state.Instances[8]=.01f;Assert.Equal(lit,Draw(true));
            state.Instances[8]=1;state.IsVisible=false;Assert.Equal(lit,Draw(true));
            state.IsVisible=true;state.Def=state.Def with { EffectsQuality=VfxEffectsQuality.Medium };Assert.Equal(lit,Draw(true));
            Assert.Equal(GLEnum.NoError,gl.GetError());
        }
        finally{gl.BindFramebuffer(FramebufferTarget.Framebuffer,0);gl.DeleteFramebuffer(frame);gl.DeleteTexture(output);gl.DeleteTexture(sprite);}
    }

    private sealed class ShadowPass(VfxOpenGlRenderer renderer,List<VfxRenderQueueEntry> queue):IPreparedParticlePass
    {
        internal bool Enabled=true;
        public bool HasPreparedShadowCasters=>Enabled;
        public void RenderPreparedShadowPass(Matrix4x4 light,Matrix4x4 eye)=>renderer.RenderShadows(queue,light,eye);
        public IDisposable BeginPreparedRenderBatch()=>renderer.BeginRenderBatch();
        public void RenderPreparedColorPass(){} public void CapturePreparedDistortionFrame(){} public void RenderPreparedDistortionPass(){}
    }
    private static VfxPlaybackRuntime.EmitterState State(VfxEmitterDefinition def,Vector3 position,float size)
    {
        var data=new float[VfxPlaybackRuntime.InstanceStride];
        data[0]=position.X;data[1]=position.Y;data[2]=position.Z;data[3]=data[4]=size;
        data[5]=data[6]=data[7]=data[8]=1;data[18]=1;data[21]=data[22]=data[31]=data[32]=1;
        data[36]=data[40]=data[44]=1;
        return new(){Def=def,InstanceCount=1,Instances=data};
    }
    private static uint Frame(GL gl,uint texture)
    {uint frame=gl.GenFramebuffer();gl.BindFramebuffer(FramebufferTarget.Framebuffer,frame);gl.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,texture,0);Assert.Equal(GLEnum.FramebufferComplete,gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer));return frame;}
    private static uint Texture(GL gl,uint width,uint height,byte[] pixels)
    {uint texture=gl.GenTexture();gl.BindTexture(TextureTarget.Texture2D,texture);gl.TexImage2D(TextureTarget.Texture2D,0,InternalFormat.Rgba8,width,height,0,PixelFormat.Rgba,PixelType.UnsignedByte,pixels.AsSpan());gl.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)GLEnum.Nearest);gl.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)GLEnum.Nearest);return texture;}
}
