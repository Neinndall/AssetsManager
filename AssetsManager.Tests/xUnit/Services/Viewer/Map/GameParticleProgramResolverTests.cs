using Silk.NET.OpenGL;
using System;
using System.IO;
using System.Linq;
using System.Numerics;
using AssetsManager.Shaders;
using AssetsManager.Services.Viewer.Rendering.GameShaders;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Wad;
using Xunit;

namespace AssetsManager.Tests.xUnit.Services.Viewer.Map
{
    public sealed class GameParticleProgramResolverTests
    {
        [Fact]
        public void DefaultQuadUsesTheAuthoredHlslStageTocsAndTranslatesWhenTheCacheIsInstalled()
        {
            GameMaterialProgram program = GameParticleProgramResolver.Create(Emitter(), meshGeometry: false);
            GameMaterialPass pass = Assert.Single(program.Passes);

            Assert.Equal(GameMaterialKind.Particles, program.Kind);
            Assert.Equal("ParticleSystem/QUAD", pass.ShaderPath);
            Assert.Equal(
                "assets/shaders/hlsl/particlesystem/quad_vs.vs-dx11",
                GameShaderProgramResolver.TocPath(pass.VertexShaderPath, "vs"));
            Assert.Equal(
                "assets/shaders/hlsl/particlesystem/quad_ps.ps-dx11",
                GameShaderProgramResolver.TocPath(pass.PixelShaderPath, "ps"));
            Assert.Contains(pass.Defines, define => define.Name == "ALPHA_TEST");

            string root = FindInstalledShaderCacheRoot();
            if (root == null)
                return;

            string cachePath = Path.Combine(root, @"Game\DATA\FINAL\ShaderCache.dx11.wad.client");
            using var wad = new WadFile(cachePath);
            GameShaderProgramResolver.ShaderBytecodeMaterialProgram read =
                GameShaderProgramResolver.ReadProgram(program, wad, cachePath);
            GameShaderProgramResolver.ShaderBytecodeRead bytecode = Assert.Single(read.Passes).Bytecode;
            Assert.True(bytecode.Ready, bytecode.Failure);

            GameShaderTranslator.TranslationRead translated = GameShaderTranslator.Translate(
                bytecode.Program.Vertex,
                bytecode.Program.VertexReflection,
                bytecode.Program.Pixel,
                bytecode.Program.PixelReflection);
            Assert.True(translated.Ready, translated.Failure);
        }

        [Fact]
        public void PairSelectionUsesSeparateScreenFixedMeshAttachedAndDistortionHlslFiles()
        {
            AssertPair(
                GameParticleProgramResolver.ResolvePair(Emitter(), meshGeometry: false),
                "ParticleSystem/QUAD",
                GameParticleProgramResolver.QuadVertexPath,
                GameParticleProgramResolver.QuadPixelPath);
            AssertPair(
                GameParticleProgramResolver.ResolvePair(Emitter() with { UvMode = 1 }, meshGeometry: false),
                "ParticleSystem/QUAD_ScreenSpaceUV",
                GameParticleProgramResolver.ScreenSpaceQuadVertexPath,
                GameParticleProgramResolver.ScreenSpaceQuadPixelPath);
            AssertPair(
                GameParticleProgramResolver.ResolvePair(Emitter() with { UvMode = 2 }, meshGeometry: false),
                "ParticleSystem/QUAD_FixedAlphaUV",
                GameParticleProgramResolver.FixedAlphaQuadVertexPath,
                GameParticleProgramResolver.FixedAlphaQuadPixelPath);
            AssertPair(
                GameParticleProgramResolver.ResolvePair(Emitter(), meshGeometry: true),
                "ParticleSystem/MESH",
                GameParticleProgramResolver.MeshVertexPath,
                GameParticleProgramResolver.MeshPixelPath);
            AssertPair(
                GameParticleProgramResolver.ResolvePair(
                    Emitter() with { PrimitiveKind = VfxPrimitiveKind.AttachedMesh },
                    meshGeometry: true),
                "SkinnedMesh/PARTICLE",
                GameParticleProgramResolver.AttachedMeshVertexPath,
                GameParticleProgramResolver.AttachedMeshPixelPath);

            VfxEmitterDefinition distortion = Emitter() with
            {
                Distortion = new VfxDistortionDefinition(1f, 1, "normal.tex")
            };
            AssertPair(
                GameParticleProgramResolver.ResolvePair(
                    Emitter() with { MeshIsSkinned = true },
                    meshGeometry: true),
                "ParticleSystem/MESH",
                GameParticleProgramResolver.MeshVertexPath,
                GameParticleProgramResolver.MeshPixelPath);
            AssertPair(
                GameParticleProgramResolver.ResolvePair(distortion, meshGeometry: false),
                "ParticleSystem/DISTORTION",
                GameParticleProgramResolver.DistortionVertexPath,
                GameParticleProgramResolver.DistortionPixelPath);
            AssertPair(
                GameParticleProgramResolver.ResolvePair(distortion, meshGeometry: true),
                "ParticleSystem/DISTORTION_MESH",
                GameParticleProgramResolver.DistortionMeshVertexPath,
                GameParticleProgramResolver.DistortionMeshPixelPath);
            AssertPair(
                GameParticleProgramResolver.ResolvePair(
                    distortion with { PrimitiveKind = VfxPrimitiveKind.AttachedMesh },
                    meshGeometry: true),
                "SkinnedMesh/PARTICLE_DISTORTION",
                GameParticleProgramResolver.DistortionAttachedVertexPath,
                GameParticleProgramResolver.DistortionAttachedPixelPath);
        }

        [Fact]
        public void UnsupportedQuadModesDeferToTheExistingParticleRenderer()
        {
            Assert.Null(GameParticleProgramResolver.Create(
                Emitter() with { UvMode = 1 },
                meshGeometry: false));
            Assert.Null(GameParticleProgramResolver.Create(
                Emitter() with { UvMode = 2 },
                meshGeometry: false));
            Assert.Null(GameParticleProgramResolver.Create(
                Emitter() with
                {
                    Reflection = new VfxReflectionDefinition(
                        1f,
                        1f,
                        1f,
                        1f,
                        Vector4.One,
                        Vector4.One,
                        "reflection.dds")
                },
                meshGeometry: false));
        }

        [Fact]
        public void EmitterDefinesComeFromAuthoredFieldsAndMeshModes()
        {
            VfxEmitterDefinition emitter = Emitter() with
            {
                RenderState = VfxEmitterRenderState.Default with { AlphaReference = 0 },
                UvMode = 5,
                AlphaErosion = new VfxAlphaErosionDefinition(
                    "erosion.tex",
                    VfxCurveF.Const(1f),
                    0f,
                    0f,
                    0),
                TextureMultPath = "mult.tex",
                PaletteDefinition = new VfxPaletteDefinition(2, VfxCurve3.Const(Vector3.Zero)),
                SoftParticle = new VfxSoftParticleDefinition(0f, 1f, 0f, 1f),
                Reflection = new VfxReflectionDefinition(
                    1f,
                    1f,
                    1f,
                    1f,
                    Vector4.One,
                    Vector4.One,
                    "reflection.dds")
            };

            string[] names = GameParticleProgramResolver.BuildEmitterDefines(emitter, meshGeometry: true)
                .Select(define => define.Name)
                .ToArray();

            Assert.Equal(
                new[]
                {
                    "ALPHA_EROSION",
                    "MULT_PASS",
                    "LOCAL_SPACE_UV",
                    "USE_VERTEX_COLORS",
                    "PALETTIZE_TEXTURES",
                    "SOFT_PARTICLES",
                    "REFLECTIVE"
                },
                names);
            Assert.DoesNotContain("ALPHA_TEST", names);
            Assert.DoesNotContain(
                "SCREEN_SPACE_UV",
                GameParticleProgramResolver.BuildEmitterDefines(emitter, meshGeometry: false)
                    .Select(define => define.Name));
        }

        [Theory]
        [InlineData(0, 4, 0.125f)]
        [InlineData(1, 4, 0.375f)]
        [InlineData(2, 4, 0.625f)]
        [InlineData(3, 4, 0.875f)]
        public void PaletteRowNormalizedCalculatesCenterRowVCoordinate(float pickedRow, int count, float expectedV)
        {
            var palette = new VfxPaletteDefinition(count, VfxCurve3.Const(new Vector3(pickedRow, 0f, 0f)));
            Assert.Equal(expectedV, AssetsManager.Services.Viewer.Vfx.Rendering.VfxOpenGlRenderer.PaletteRowNormalized(palette), 5);
        }

        [Fact]
        public void StockParticlePermutationsComposeAndCompileAgainstAnInvisibleWglContext()
        {
            string root = FindInstalledShaderCacheRoot();
            if (root == null)
                return;

            string cachePath = Path.Combine(root, @"Game\DATA\FINAL\ShaderCache.dx11.wad.client");
            var alphaErosion = new VfxAlphaErosionDefinition(
                "erosion.tex",
                VfxCurveF.Const(1f),
                0f,
                0f,
                0);
            var softParticle = new VfxSoftParticleDefinition(0f, 1f, 0f, 1f);
            var distortion = new VfxDistortionDefinition(1f, 1, "normal.tex");
            var reflection = new VfxReflectionDefinition(
                1f,
                1f,
                1f,
                1f,
                Vector4.One,
                Vector4.One,
                "reflection.dds");
            var cases = new[]
            {
                new ParticleShaderCase("QUAD", Emitter(), false),
                new ParticleShaderCase(
                    "QUAD ALPHA_EROSION MULT_PASS SOFT_PARTICLES",
                    Emitter() with
                    {
                        AlphaErosion = alphaErosion,
                        TextureMultPath = "mult.tex",
                        SoftParticle = softParticle
                    },
                    false),
                new ParticleShaderCase(
                    "MESH REFLECTIVE",
                    Emitter() with { Reflection = reflection },
                    true),
                new ParticleShaderCase(
                    "ATTACHED MESH",
                    Emitter() with { PrimitiveKind = VfxPrimitiveKind.AttachedMesh },
                    true),
                new ParticleShaderCase(
                    "DISTORTION QUAD",
                    Emitter() with { Distortion = distortion },
                    false),
                new ParticleShaderCase(
                    "DISTORTION MESH",
                    Emitter() with { Distortion = distortion },
                    true),
                new ParticleShaderCase(
                    "DISTORTION ATTACHED MESH",
                    Emitter() with
                    {
                        Distortion = distortion,
                        PrimitiveKind = VfxPrimitiveKind.AttachedMesh
                    },
                    true)
            };

            using var wad = new WadFile(cachePath);
            using var context = new HiddenWglContext();
            Silk.NET.OpenGL.GL gl = Silk.NET.OpenGL.GL.GetApi(context.GetProcAddress);
            var glFailures = new System.Collections.Generic.List<string>();
            try
            {
                Assert.NotEmpty(gl.GetStringS(Silk.NET.OpenGL.StringName.Version));
                foreach (ParticleShaderCase shaderCase in cases)
                {
                    GameMaterialProgram program = GameParticleProgramResolver.Create(
                        shaderCase.Emitter,
                        shaderCase.Mesh);
                    Assert.NotNull(program);
                    GameShaderProgramResolver.ShaderBytecodeMaterialProgram bytecodes =
                        GameShaderProgramResolver.ReadProgram(program, wad, cachePath);
                    GameShaderProgramResolver.ShaderBytecodePassRead pass = Assert.Single(bytecodes.Passes);
                    Assert.True(pass.Bytecode.Ready, $"{shaderCase.Name}: {pass.Bytecode.Failure}");

                    GameShaderTranslator.TranslationRead translated = GameShaderTranslator.Translate(
                        pass.Bytecode.Program.Vertex,
                        pass.Bytecode.Program.VertexReflection,
                        pass.Bytecode.Program.Pixel,
                        pass.Bytecode.Program.PixelReflection);
                    Assert.True(translated.Ready, $"{shaderCase.Name}: {translated.Failure}");

                    GameShaderTranslator.TranslatedProgram composed = GameParticleShaderPrelude.Compose(
                        translated.Program,
                        shaderCase.Mesh);
                    Assert.Single(System.Text.RegularExpressions.Regex.Matches(
                        composed.Vertex.Glsl,
                        @"\bvoid\s+main\s*\("));
                    Assert.Single(System.Text.RegularExpressions.Regex.Matches(
                        composed.Pixel.Glsl,
                        @"\bvoid\s+main\s*\("));
                    Assert.Contains("void particleGeometry()", composed.Vertex.Glsl);
                    Assert.Contains("void particleFeed()", composed.Vertex.Glsl);
                    Assert.Contains("particleGeometry(); particleFeed();", composed.Vertex.Glsl);
                    Assert.Contains(
                        shaderCase.Mesh
                            ? "layout(location=0) in vec3 aPos;"
                            : "layout(location=0) in vec2 aCorner;",
                        composed.Vertex.Glsl);

                    string vertex = ToDesktopGlsl(composed.Vertex.Glsl);
                    string pixel = ToDesktopGlsl(composed.Pixel.Glsl);
                    uint linked = 0;
                    try
                    {
                        linked = AssetsManager.Utils.Rendering.GlShaderCompiler.CreateRawProgram(
                            gl,
                            vertex,
                            pixel);
                        Assert.Equal(
                            0,
                            gl.GetAttribLocation(linked, shaderCase.Mesh ? "aPos" : "aCorner"));
                    }
                    catch (System.Exception ex)
                    {
                        glFailures.Add($"{shaderCase.Name} prelude failed GLSL compile/link: {ex.Message}");
                    }
                    finally
                    {
                        if (linked != 0)
                            gl.DeleteProgram(linked);
                    }
                }
                Assert.Empty(glFailures);
            }
            finally
            {
                gl.Dispose();
            }
        }

        [Theory]
        [InlineData("ASSETS/Shaders/HLSL/ParticleSystem/QUAD_VS_FixedAlphaUV.vs", "vs", GameShaderTranslator.AppliedPatch.BaseVertexZero)]
        [InlineData("Shaders/StaticMesh/Mantis_Env_Baked_PBR", "ps", GameShaderTranslator.AppliedPatch.MipLevelsOne)]
        public void EsUnsupportedQueriesTranslateAndCompileOnDesktopOpenGl(
            string shader,
            string stage,
            GameShaderTranslator.AppliedPatch expected)
        {
            // The only two causes the ShaderCache sweep found: BaseVertex and textureQueryLevels have no GLSL ES form.
            string root = FindInstalledShaderCacheRoot();
            if (root == null)
                return;

            string cachePath = Path.Combine(root, @"Game\DATA\FINAL\ShaderCache.dx11.wad.client");
            using var wad = new WadFile(cachePath);
            string toc = GameShaderProgramResolver.TocPath(shader, stage);
            ulong tocHash = LeagueToolkit.Hashing.XxHash64Ext.Hash(toc);
            if (!wad.Chunks.ContainsKey(tocHash))
                return;

            LeagueToolkit.Core.Renderer.ShaderToc table;
            using (var tocBytes = wad.LoadChunkDecompressed(tocHash))
            using (var tocStream = new MemoryStream(tocBytes.Span.ToArray(), writable: false))
                table = new LeagueToolkit.Core.Renderer.ShaderToc(tocStream);

            using var context = new HiddenWglContext();
            Silk.NET.OpenGL.GL gl = Silk.NET.OpenGL.GL.GetApi(context.GetProcAddress);
            var failures = new System.Collections.Generic.List<string>();
            int patched = 0;
            try
            {
                foreach (uint shaderId in System.Linq.Enumerable.Distinct(table.ShaderIds))
                {
                    byte[] bundle;
                    using (var bundleBytes = wad.LoadChunkDecompressed(
                               LeagueToolkit.Hashing.XxHash64Ext.Hash(GameShaderProgramResolver.BundlePath(toc, shaderId))))
                        bundle = bundleBytes.Span.ToArray();
                    byte[] dxbc = GameShaderProgramResolver.ReadBundleRecord(bundle, shaderId % 100);

                    GameShaderTranslator.TranslatedStage translated = GameShaderTranslator.TranslateStage(
                        dxbc,
                        DxbcReflection.Reflect(dxbc),
                        stage == "vs" ? GameShaderTranslator.Stage.Vertex : GameShaderTranslator.Stage.Pixel);
                    if (translated.Applied.Contains(expected))
                        patched++;

                    Silk.NET.OpenGL.ShaderType type = stage == "vs"
                        ? Silk.NET.OpenGL.ShaderType.VertexShader
                        : Silk.NET.OpenGL.ShaderType.FragmentShader;
                    uint handle = gl.CreateShader(type);
                    try
                    {
                        gl.ShaderSource(handle, ToDesktopGlsl(translated.Glsl));
                        gl.CompileShader(handle);
                        gl.GetShader(handle, Silk.NET.OpenGL.ShaderParameterName.CompileStatus, out int compiled);
                        if (compiled == 0)
                            failures.Add($"{toc}#{shaderId}: {gl.GetShaderInfoLog(handle)}");
                    }
                    finally
                    {
                        gl.DeleteShader(handle);
                    }
                }
                Assert.Empty(failures);
                Assert.True(patched > 0, $"No permutation of {toc} needed {expected}.");
            }
            finally
            {
                gl.Dispose();
            }
        }

        [Theory]
        [InlineData("Maps/MapGeometry/Map11/Base_SRX")]
        // Bloom's base water refracts the captured scene colour and depth.
        [InlineData("Maps/MapGeometry/Map11/Bloom")]
        public async System.Threading.Tasks.Task InstalledMap11ProgramsCompileAndLinkOnDesktopOpenGl(string map)
        {
            string root = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory),
                "Map11.wad.client");
            if (!Directory.Exists(root) || FindInstalledShaderCacheRoot() == null)
                return;

            int linked = 0;
            var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
            await AssetsManager.Tests.Diagnostics.Viewer.MapShaderAuditDiagnostic.Run(
                root, map, program =>
                {
                    string vertex = ToDesktopGlsl(program.Vertex.Glsl);
                    string fragment = ToDesktopGlsl(GameParticleShaderPrelude.WithScreenCopy(program.Pixel.Glsl));
                    if (!seen.Add(vertex + fragment))
                        return;
                    using var context = new HiddenWglContext();
                    using Silk.NET.OpenGL.GL gl = Silk.NET.OpenGL.GL.GetApi(context.GetProcAddress);
                    uint handle = AssetsManager.Utils.Rendering.GlShaderCompiler.CreateRawProgram(
                        gl, vertex, fragment);
                    gl.DeleteProgram(handle);
                    linked++;
                });
            Assert.True(linked > 0, "Map11 must supply translated programs for the GPU check.");
        }

        [Fact]
        public void ScreenTexturesFlipUvReadsAndKeepFragCoordFetches()
        {
            const string source =
                "uniform highp sampler2D sDepthTexture_SharedTexture;\n" +
                "uniform highp sampler2D SAMPLER_BACK_BUFFER_COPY_SharedTexture;\n" +
                "void main(){ float d = texture(sDepthTexture_SharedTexture, uv).x;" +
                " vec4 c = texture(SAMPLER_BACK_BUFFER_COPY_SharedTexture, uv);" +
                " float f = texelFetch(sDepthTexture_SharedTexture, ivec2(gl_FragCoord.xy), 0).x; }";

            string patched = GameParticleShaderPrelude.WithScreenCopy(source);

            Assert.Contains("particleScreenDepth(uv)", patched);
            Assert.Contains("particleScreenCopy(uv)", patched);
            Assert.Contains("vec4 particleScreenDepth(vec2 at){ return texture(sDepthTexture_SharedTexture, vec2(at.x, 1.0-at.y)); }", patched);
            Assert.Contains("texelFetch(sDepthTexture_SharedTexture, ivec2(gl_FragCoord.xy), 0)", patched);
            Assert.Equal("void main(){}", GameParticleShaderPrelude.WithScreenCopy("void main(){}"));
        }

        [Fact]
        public void ScreenSamplersBindTheFrameCaptures()
        {
            var frame = new GameShaderRuntime.Frame(
                System.Numerics.Matrix4x4.Identity,
                System.Numerics.Matrix4x4.Identity,
                System.Numerics.Vector3.Zero,
                0f,
                null,
                SceneColor: 5,
                SceneDepth: 6);

            Assert.Equal(5u, GameShaderRuntime.ScreenTextureFor(GameShaderRuntime.SceneColorTexture, frame));
            Assert.Equal(6u, GameShaderRuntime.ScreenTextureFor(GameShaderRuntime.SceneDepthTexture, frame));
            Assert.Equal(0u, GameShaderRuntime.ScreenTextureFor("TERRAIN_BLEND_SharedTexture", frame));
            Assert.Equal(0u, GameShaderRuntime.ScreenTextureFor(GameShaderRuntime.SceneColorTexture, frame with { SceneColor = 0 }));
        }

        [Theory]
        [InlineData(0, false, true)]
        [InlineData(1, false, true)]
        [InlineData(3, false, false)]
        [InlineData(1, true, false)]
        public void ParticlePassClassificationPreservesOpaqueModeAndGround(int blend, bool ground, bool expected)
        {
            var emitter = Emitter() with { BlendMode = blend, IsGroundLayer = ground };
            Assert.Equal(expected, AssetsManager.Services.Viewer.Vfx.Rendering.VfxOpenGlRenderer.IsParticlePassTransparent(emitter, false));
            Assert.False(AssetsManager.Services.Viewer.Vfx.Rendering.VfxOpenGlRenderer.IsParticlePassTransparent(emitter, false, false));
            Assert.Equal(!ground, AssetsManager.Services.Viewer.Vfx.Rendering.VfxOpenGlRenderer.IsParticlePassTransparent(emitter, false, true));
        }

        [Fact]
        public void CustomParticlePassTextureWinsOverEmitterAliasAndDoesNotLeakAcrossPasses()
        {
            var handles = new System.Collections.Generic.Dictionary<string, uint>
            {
                ["TEXTURE"] = 99, ["assets/first.tex"] = 11, ["assets/second.tex"] = 22,
                ["0000000000001234"] = 33, ["sDepthTexture_SharedTexture"] = 44
            };
            uint? Lookup(string name) => handles.TryGetValue(name, out uint handle) ? handle : null;
            GameMaterialTexture Texture(string path, ulong hash = 0) => new(
                "TEXTURE", new MapTextureReference(path, hash),
                GameMaterialTextureSource.Material, null);
            Assert.Equal((uint)11, GameShaderRuntime.ResolveParticleTexture(true, "TEXTURE__TX", Texture("assets/first.tex"), Lookup));
            Assert.Equal((uint)22, GameShaderRuntime.ResolveParticleTexture(true, "TEXTURE__TX", Texture("assets/second.tex"), Lookup));
            Assert.Equal((uint)33, GameShaderRuntime.ResolveParticleTexture(true, "TEXTURE__TX", Texture(null, 0x1234), Lookup));
            Assert.Null(GameShaderRuntime.ResolveParticleTexture(true, "TEXTURE__TX", Texture("assets/missing.tex"), Lookup));
            Assert.Equal((uint)99, GameShaderRuntime.ResolveParticleTexture(false, "TEXTURE", null, Lookup));
            Assert.Equal((uint)44, GameShaderRuntime.ResolveParticleTexture(true, "sDepthTexture_SharedTexture", null, Lookup));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SmaaRunsAllThreePassesResizesAndRestoresFramebuffer(bool diagonal)
        {
            using var context = new HiddenWglContext();
            using Silk.NET.OpenGL.GL gl = Silk.NET.OpenGL.GL.GetApi(context.GetProcAddress);
            using var smaa = new AssetsManager.Services.Viewer.Rendering.SmaaPostEffectsRenderer();
            smaa.Initialize(gl);
            uint source = gl.GenTexture(), target = gl.GenTexture(), framebuffer = gl.GenFramebuffer();
            try
            {
                foreach (var size in new[] { (Width: 16, Height: 16), (Width: 7, Height: 9), (Width: 16, Height: 16) })
                {
                    byte[] pixels = new byte[size.Width * size.Height * 4];
                    for (int y = 0; y < size.Height; y++)
                    for (int x = 0; x < size.Width; x++)
                    {
                        int at = (y * size.Width + x) * 4;
                        byte color = diagonal ? (byte)(x > y ? 255 : 0) : (byte)96;
                        pixels[at] = pixels[at + 1] = pixels[at + 2] = color;
                        pixels[at + 3] = 255;
                    }
                    gl.ActiveTexture(Silk.NET.OpenGL.TextureUnit.Texture0);
                    gl.BindTexture(Silk.NET.OpenGL.TextureTarget.Texture2D, source);
                    gl.TexImage2D(Silk.NET.OpenGL.TextureTarget.Texture2D, 0, Silk.NET.OpenGL.InternalFormat.Rgba8,
                        (uint)size.Width, (uint)size.Height, 0, Silk.NET.OpenGL.PixelFormat.Rgba,
                        Silk.NET.OpenGL.PixelType.UnsignedByte, pixels.AsSpan());
                    gl.TexParameter(Silk.NET.OpenGL.TextureTarget.Texture2D, Silk.NET.OpenGL.TextureParameterName.TextureMinFilter,
                        (int)Silk.NET.OpenGL.TextureMinFilter.Linear);
                    gl.TexParameter(Silk.NET.OpenGL.TextureTarget.Texture2D, Silk.NET.OpenGL.TextureParameterName.TextureMagFilter,
                        (int)Silk.NET.OpenGL.TextureMagFilter.Linear);
                    gl.TexParameter(Silk.NET.OpenGL.TextureTarget.Texture2D, Silk.NET.OpenGL.TextureParameterName.TextureWrapS,
                        (int)Silk.NET.OpenGL.TextureWrapMode.ClampToEdge);
                    gl.TexParameter(Silk.NET.OpenGL.TextureTarget.Texture2D, Silk.NET.OpenGL.TextureParameterName.TextureWrapT,
                        (int)Silk.NET.OpenGL.TextureWrapMode.ClampToEdge);
                    gl.BindTexture(Silk.NET.OpenGL.TextureTarget.Texture2D, target);
                    gl.TexImage2D(Silk.NET.OpenGL.TextureTarget.Texture2D, 0, Silk.NET.OpenGL.InternalFormat.Rgba8,
                        (uint)size.Width, (uint)size.Height, 0, Silk.NET.OpenGL.PixelFormat.Rgba,
                        Silk.NET.OpenGL.PixelType.UnsignedByte, ReadOnlySpan<byte>.Empty);
                    gl.BindFramebuffer(Silk.NET.OpenGL.FramebufferTarget.Framebuffer, framebuffer);
                    gl.FramebufferTexture2D(Silk.NET.OpenGL.FramebufferTarget.Framebuffer,
                        Silk.NET.OpenGL.FramebufferAttachment.ColorAttachment0, Silk.NET.OpenGL.TextureTarget.Texture2D, target, 0);
                    gl.Viewport(0, 0, (uint)size.Width, (uint)size.Height);
                    gl.Enable(Silk.NET.OpenGL.EnableCap.ScissorTest);
                    gl.Enable(Silk.NET.OpenGL.EnableCap.DepthTest);
                    gl.DepthMask(true);
                    smaa.RenderTexture(source, size.Width, size.Height);
                    gl.GetInteger(Silk.NET.OpenGL.GLEnum.DrawFramebufferBinding, out int bound);
                    Assert.Equal(framebuffer, (uint)bound);
                    Assert.True(gl.IsEnabled(Silk.NET.OpenGL.EnableCap.ScissorTest));
                    Assert.True(gl.IsEnabled(Silk.NET.OpenGL.EnableCap.DepthTest));
                    byte[] result = new byte[pixels.Length];
                    gl.ReadPixels(0, 0, (uint)size.Width, (uint)size.Height, Silk.NET.OpenGL.PixelFormat.Rgba,
                        Silk.NET.OpenGL.PixelType.UnsignedByte, result.AsSpan());
                    if (!diagonal)
                    {
                        for (int i = 0; i < result.Length; i += 4)
                            Assert.InRange(result[i], 95, 97);
                    }
                    else
                    {
                        Assert.Contains(Enumerable.Range(0, size.Width * size.Height),
                            at => result[at * 4] > 0 && result[at * 4] < 255);
                        Assert.Contains(Enumerable.Range(0, size.Width * size.Height), at => result[at * 4] == 0);
                        Assert.Contains(Enumerable.Range(0, size.Width * size.Height), at => result[at * 4] == 255);
                    }
                    Assert.Equal(Silk.NET.OpenGL.GLEnum.NoError, gl.GetError());
                }
            }
            finally
            {
                gl.BindFramebuffer(Silk.NET.OpenGL.FramebufferTarget.Framebuffer, 0);
                gl.DeleteFramebuffer(framebuffer); gl.DeleteTexture(source); gl.DeleteTexture(target);
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void TransparentDoubleSideDrawsBackThenFrontAndRestoresCulling(bool doubleSide)
        {
            using var context = new HiddenWglContext();
            using Silk.NET.OpenGL.GL gl = Silk.NET.OpenGL.GL.GetApi(context.GetProcAddress);
            using var runtime = new GameShaderRuntime(gl, false, null);
            var faces = new System.Collections.Generic.List<int>();
            gl.Disable(Silk.NET.OpenGL.EnableCap.CullFace);
            gl.CullFace(Silk.NET.OpenGL.TriangleFace.Back);
            runtime.DrawIndexedPass((mode, count, type, offset) =>
            {
                gl.GetInteger(Silk.NET.OpenGL.GLEnum.CullFaceMode, out int face);
                faces.Add(face);
                Assert.Equal(3, count);
                Assert.Equal(System.IntPtr.Zero, offset);
            }, 3, System.IntPtr.Zero, doubleSide);

            Assert.Equal(
                doubleSide
                    ? new[] { (int)Silk.NET.OpenGL.TriangleFace.Front, (int)Silk.NET.OpenGL.TriangleFace.Back }
                    : new[] { (int)Silk.NET.OpenGL.TriangleFace.Back },
                faces);
            Assert.False(gl.IsEnabled(Silk.NET.OpenGL.EnableCap.CullFace));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void CapturedSolidDepthMatchesFreshSceneWithTransparentDepthWrites(bool writesDepth)
        {
            using var context = new HiddenWglContext();
            using Silk.NET.OpenGL.GL gl = Silk.NET.OpenGL.GL.GetApi(context.GetProcAddress);
            const string vertex = @"
uniform float uDepth;
void main()
{
    vec2 point = vec2((gl_VertexID << 1) & 2, gl_VertexID & 2);
    gl_Position = vec4(point * 2.0 - 1.0, uDepth, 1.0);
}";
            const string fragment = @"out vec4 FragColor;
void main() { FragColor = vec4(0.4, 0.6, 0.8, 0.5); }";
            uint program = AssetsManager.Utils.Rendering.GlShaderCompiler.CreateProgram(gl, false, vertex, fragment);
            uint vao = gl.GenVertexArray();
            using var capture = new AssetsManager.Services.Viewer.Rendering.Core.GlSceneCapture(gl);
            try
            {
                gl.Viewport(0, 0, 1, 1);
                gl.BindVertexArray(vao);
                gl.UseProgram(program);
                gl.Enable(Silk.NET.OpenGL.EnableCap.DepthTest);
                gl.DepthFunc(Silk.NET.OpenGL.DepthFunction.Lequal);
                int depth = gl.GetUniformLocation(program, "uDepth");

                void DrawSolidScene()
                {
                    gl.DepthMask(true);
                    gl.ClearDepth(1.0);
                    gl.Clear(Silk.NET.OpenGL.ClearBufferMask.DepthBufferBit);
                    gl.Disable(Silk.NET.OpenGL.EnableCap.Blend);
                    gl.Uniform1(depth, 0.6f);
                    gl.DrawArrays(Silk.NET.OpenGL.PrimitiveType.Triangles, 0, 3);
                    gl.Enable(Silk.NET.OpenGL.EnableCap.Blend);
                    gl.BlendFunc(Silk.NET.OpenGL.BlendingFactor.SrcAlpha, Silk.NET.OpenGL.BlendingFactor.OneMinusSrcAlpha);
                    gl.DepthMask(writesDepth);
                    gl.Uniform1(depth, -0.2f);
                    gl.DrawArrays(Silk.NET.OpenGL.PrimitiveType.Triangles, 0, 3);
                }

                DrawSolidScene();
                capture.Capture(1, 1, captureColor: false, captureDepth: true);
                float copied;
                gl.BindTexture(Silk.NET.OpenGL.TextureTarget.Texture2D, capture.DepthTexture);
                gl.GetTexImage<float>(Silk.NET.OpenGL.GLEnum.Texture2D, 0,
                    Silk.NET.OpenGL.GLEnum.DepthComponent, Silk.NET.OpenGL.GLEnum.Float, out copied);

                DrawSolidScene();
                float rebuilt;
                gl.ReadPixels<float>(0, 0, 1, 1,
                    Silk.NET.OpenGL.GLEnum.DepthComponent, Silk.NET.OpenGL.GLEnum.Float, out rebuilt);
                Assert.Equal(writesDepth ? 0.4f : 0.8f, copied, 5);
                Assert.Equal(rebuilt, copied, 5);
            }
            finally
            {
                gl.DeleteVertexArray(vao);
                gl.DeleteProgram(program);
            }
        }

        [Fact]
        public void MapPreviewShadersCompileAndLinkOnDesktopOpenGl()
        {
            using var context = new HiddenWglContext();
            using Silk.NET.OpenGL.GL gl = Silk.NET.OpenGL.GL.GetApi(context.GetProcAddress);
            var sources = new[]
            {
                (
                    AssetsManager.Services.Viewer.Rendering.MapGeometryShaderSource.Vertex,
                    AssetsManager.Services.Viewer.Rendering.MapGeometryShaderSource.Fragment),
                (
                    AssetsManager.Services.Viewer.Rendering.MapCharacterShaderSource.Vertex,
                    AssetsManager.Services.Viewer.Rendering.MapCharacterShaderSource.Fragment),
                (
                    AssetsManager.Services.Viewer.Rendering.Core.GlMeshShaderSource.Vertex,
                    AssetsManager.Services.Viewer.Rendering.Core.GlMeshShaderSource.Fragment)
            };
            foreach ((string vertex, string fragment) in sources)
            {
                uint program = AssetsManager.Utils.Rendering.GlShaderCompiler.CreateProgram(
                    gl, false, vertex, fragment);
                try
                {
                    Assert.NotEqual(0u, program);
                }
                finally
                {
                    gl.DeleteProgram(program);
                }
            }
        }

        private sealed record ParticleShaderCase(
            string Name,
            VfxEmitterDefinition Emitter,
            bool Mesh);

        private static string ToDesktopGlsl(string source)
        {
            string result = source ?? string.Empty;
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"^#version\s+300\s+es\s*$",
                "#version 330 core",
                System.Text.RegularExpressions.RegexOptions.Multiline |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            result = System.Text.RegularExpressions.Regex.Replace(
                result,
                @"^\s*precision\s+(?:lowp|mediump|highp)\s+\w+\s*;\s*$\r?\n?",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.Multiline |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            return System.Text.RegularExpressions.Regex.Replace(
                result,
                @"\b(?:lowp|mediump|highp)\s+",
                string.Empty,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        }

        [System.Runtime.InteropServices.DllImport(
            "opengl32.dll",
            EntryPoint = "wglGetProcAddress",
            CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern System.IntPtr WglGetProcAddress(string procName);

        [System.Runtime.InteropServices.DllImport(
            "kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
        private static extern System.IntPtr GetProcAddress(System.IntPtr module, string procName);

        [System.Runtime.InteropServices.DllImport(
            "kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern System.IntPtr LoadLibrary(string libraryName);

        private static readonly System.IntPtr OpenGlModule = LoadLibrary("opengl32.dll");

        private static System.IntPtr GetWglProcAddress(string procName)
        {
            System.IntPtr address = WglGetProcAddress(procName);
            long value = address.ToInt64();
            if (address == System.IntPtr.Zero || value is 1 or 2 or 3 or -1)
                address = GetProcAddress(OpenGlModule, procName);
            return address;
        }
        private sealed class HiddenWglContext : System.IDisposable
        {
            private const uint ClassOwnDeviceContext = 0x0020;
            private const uint WindowExToolWindow = 0x00000080;
            private const uint WindowPopup = 0x80000000;
            private const uint PixelFormatDrawToWindow = 0x00000004;
            private const uint PixelFormatSupportOpenGl = 0x00000020;
            private const uint PixelFormatDoubleBuffer = 0x00000001;
            private const byte PixelTypeRgba = 0;
            private const sbyte MainPlane = 0;
            private const int WglContextMajorVersion = 0x2091;
            private const int WglContextMinorVersion = 0x2092;
            private const int WglContextProfileMask = 0x9126;
            private const int WglContextCoreProfileBit = 0x00000001;

            private readonly string _className = "AssetsManager.ParticleShaderCompile." + System.Guid.NewGuid().ToString("N");
            private readonly System.IntPtr _instance = GetModuleHandle(null);
            private readonly WindowProcedure _windowProcedure = DefWindowProc;
            private System.IntPtr _window;
            private System.IntPtr _deviceContext;
            private System.IntPtr _legacyContext;
            private System.IntPtr _renderContext;
            private bool _disposed;

            internal HiddenWglContext()
            {
                try
                {
                    CreateWindowAndContext();
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            internal System.IntPtr GetProcAddress(string name)
            {
                System.IntPtr address = WglGetProcAddress(name);
                long value = address.ToInt64();
                return address == System.IntPtr.Zero || value is 1 or 2 or 3 or -1
                    ? NativeGetProcAddress(GetModuleHandle("opengl32.dll"), name)
                    : address;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;

                WglMakeCurrent(System.IntPtr.Zero, System.IntPtr.Zero);
                if (_renderContext != System.IntPtr.Zero)
                    WglDeleteContext(_renderContext);
                if (_legacyContext != System.IntPtr.Zero)
                    WglDeleteContext(_legacyContext);
                if (_deviceContext != System.IntPtr.Zero && _window != System.IntPtr.Zero)
                    ReleaseDC(_window, _deviceContext);
                if (_window != System.IntPtr.Zero)
                    DestroyWindow(_window);
                if (!string.IsNullOrWhiteSpace(_className))
                    UnregisterClass(_className, _instance);
            }

            private void CreateWindowAndContext()
            {
                var windowClass = new WindowClassEx
                {
                    Size = checked((uint)System.Runtime.InteropServices.Marshal.SizeOf<WindowClassEx>()),
                    Style = ClassOwnDeviceContext,
                    WindowProcedure = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_windowProcedure),
                    Instance = _instance,
                    ClassName = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(_className)
                };
                try
                {
                    if (RegisterClassEx(ref windowClass) == 0)
                        ThrowLastWin32Error("RegisterClassExW");
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(windowClass.ClassName);
                }

                _window = CreateWindowEx(
                    WindowExToolWindow,
                    _className,
                    "AssetsManager shader validation",
                    WindowPopup,
                    -32000,
                    -32000,
                    1,
                    1,
                    System.IntPtr.Zero,
                    System.IntPtr.Zero,
                    _instance,
                    System.IntPtr.Zero);
                if (_window == System.IntPtr.Zero)
                    ThrowLastWin32Error("CreateWindowExW");

                _deviceContext = GetDC(_window);
                if (_deviceContext == System.IntPtr.Zero)
                    ThrowLastWin32Error("GetDC");

                var descriptor = new PixelFormatDescriptor
                {
                    Size = checked((ushort)System.Runtime.InteropServices.Marshal.SizeOf<PixelFormatDescriptor>()),
                    Version = 1,
                    Flags = PixelFormatDrawToWindow | PixelFormatSupportOpenGl | PixelFormatDoubleBuffer,
                    PixelType = PixelTypeRgba,
                    ColorBits = 32,
                    AlphaBits = 8,
                    DepthBits = 24,
                    StencilBits = 8,
                    LayerType = MainPlane
                };
                int format = ChoosePixelFormat(_deviceContext, ref descriptor);
                if (format == 0)
                    ThrowLastWin32Error("ChoosePixelFormat");
                if (!SetPixelFormat(_deviceContext, format, ref descriptor))
                    ThrowLastWin32Error("SetPixelFormat");

                _legacyContext = WglCreateContext(_deviceContext);
                if (_legacyContext == System.IntPtr.Zero)
                    ThrowLastWin32Error("wglCreateContext");
                if (!WglMakeCurrent(_deviceContext, _legacyContext))
                    ThrowLastWin32Error("wglMakeCurrent(legacy)");

                System.IntPtr createContextAddress = GetProcAddress("wglCreateContextAttribsARB");
                if (createContextAddress == System.IntPtr.Zero)
                    throw new System.InvalidOperationException("WGL_ARB_create_context is unavailable.");
                var createContext = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<WglCreateContextAttribs>(
                    createContextAddress);
                int[] attributes =
                {
                    WglContextMajorVersion, 3,
                    WglContextMinorVersion, 3,
                    WglContextProfileMask, WglContextCoreProfileBit,
                    0
                };
                _renderContext = createContext(_deviceContext, System.IntPtr.Zero, attributes);
                if (_renderContext == System.IntPtr.Zero)
                    throw new System.InvalidOperationException("WGL failed to create an OpenGL 3.3 core context.");
                if (!WglMakeCurrent(_deviceContext, _renderContext))
                    ThrowLastWin32Error("wglMakeCurrent(core)");
                WglDeleteContext(_legacyContext);
                _legacyContext = System.IntPtr.Zero;
            }

            private static void ThrowLastWin32Error(string operation) =>
                throw new System.ComponentModel.Win32Exception(
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                    operation + " failed.");

            [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
            private delegate System.IntPtr WindowProcedure(
                System.IntPtr window,
                uint message,
                System.IntPtr wParam,
                System.IntPtr lParam);

            [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.StdCall)]
            private delegate System.IntPtr WglCreateContextAttribs(
                System.IntPtr deviceContext,
                System.IntPtr shareContext,
                [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPArray)] int[] attributes);

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            private struct WindowClassEx
            {
                internal uint Size;
                internal uint Style;
                internal System.IntPtr WindowProcedure;
                internal int ClassExtra;
                internal int WindowExtra;
                internal System.IntPtr Instance;
                internal System.IntPtr Icon;
                internal System.IntPtr Cursor;
                internal System.IntPtr Background;
                internal System.IntPtr MenuName;
                internal System.IntPtr ClassName;
                internal System.IntPtr SmallIcon;
            }

            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            private struct PixelFormatDescriptor
            {
                internal ushort Size;
                internal ushort Version;
                internal uint Flags;
                internal byte PixelType;
                internal byte ColorBits;
                internal byte RedBits;
                internal byte RedShift;
                internal byte GreenBits;
                internal byte GreenShift;
                internal byte BlueBits;
                internal byte BlueShift;
                internal byte AlphaBits;
                internal byte AlphaShift;
                internal byte AccumBits;
                internal byte AccumRedBits;
                internal byte AccumGreenBits;
                internal byte AccumBlueBits;
                internal byte AccumAlphaBits;
                internal byte DepthBits;
                internal byte StencilBits;
                internal byte AuxiliaryBuffers;
                internal sbyte LayerType;
                internal byte Reserved;
                internal uint LayerMask;
                internal uint VisibleMask;
                internal uint DamageMask;
            }

            [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            private static extern System.IntPtr GetModuleHandle(string moduleName);

            [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "GetProcAddress", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
            private static extern System.IntPtr NativeGetProcAddress(System.IntPtr module, string procName);

            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            private static extern bool UnregisterClass(string className, System.IntPtr instance);

            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "DefWindowProcW", SetLastError = true)]
            private static extern System.IntPtr DefWindowProc(
                System.IntPtr window,
                uint message,
                System.IntPtr wParam,
                System.IntPtr lParam);

            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
            private static extern System.IntPtr CreateWindowEx(
                uint extendedStyle,
                string className,
                string windowName,
                uint style,
                int x,
                int y,
                int width,
                int height,
                System.IntPtr parent,
                System.IntPtr menu,
                System.IntPtr instance,
                System.IntPtr parameter);

            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetDC", SetLastError = true)]
            private static extern System.IntPtr GetDC(System.IntPtr window);

            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "ReleaseDC", SetLastError = true)]
            private static extern int ReleaseDC(System.IntPtr window, System.IntPtr deviceContext);

            [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            private static extern bool DestroyWindow(System.IntPtr window);

            [System.Runtime.InteropServices.DllImport("gdi32.dll", EntryPoint = "ChoosePixelFormat", SetLastError = true)]
            private static extern int ChoosePixelFormat(System.IntPtr deviceContext, ref PixelFormatDescriptor descriptor);

            [System.Runtime.InteropServices.DllImport("gdi32.dll", EntryPoint = "SetPixelFormat", SetLastError = true)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            private static extern bool SetPixelFormat(
                System.IntPtr deviceContext,
                int format,
                ref PixelFormatDescriptor descriptor);

            [System.Runtime.InteropServices.DllImport("opengl32.dll", EntryPoint = "wglCreateContext", SetLastError = true)]
            private static extern System.IntPtr WglCreateContext(System.IntPtr deviceContext);

            [System.Runtime.InteropServices.DllImport("opengl32.dll", EntryPoint = "wglDeleteContext", SetLastError = true)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            private static extern bool WglDeleteContext(System.IntPtr context);

            [System.Runtime.InteropServices.DllImport("opengl32.dll", EntryPoint = "wglMakeCurrent", SetLastError = true)]
            [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
            private static extern bool WglMakeCurrent(System.IntPtr deviceContext, System.IntPtr context);

            [System.Runtime.InteropServices.DllImport("opengl32.dll", EntryPoint = "wglGetProcAddress", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
            private static extern System.IntPtr WglGetProcAddress(string procName);
        }
        private static void AssertPair(
            GameParticleProgramResolver.ShaderPair pair,
            string name,
            string vertex,
            string pixel)
        {
            Assert.Equal(name, pair.Name);
            Assert.Equal(vertex, pair.VertexPath);
            Assert.Equal(pixel, pair.PixelPath);
        }

        private static string FindInstalledShaderCacheRoot() =>
            new[]
                {
                    @"C:\Riot Games\League of Legends (PBE)",
                    @"C:\Riot Games\League of Legends"
                }
                .FirstOrDefault(candidate =>
                    File.Exists(Path.Combine(candidate, @"Game\DATA\FINAL\ShaderCache.dx11.wad.client")));

        private static VfxEmitterDefinition Emitter() =>
            new(
                Name: "test",
                Rate: VfxCurveF.Const(1f),
                ParticleLifetime: VfxCurveF.Const(1f),
                EmitterLifetime: null,
                ParticleLinger: 0f,
                TimeBeforeFirstEmission: 0f,
                IsSingleParticle: true,
                Disabled: false,
                BlendMode: 1,
                BirthScale: VfxCurve3.Const(Vector3.One),
                ScaleOverLife: null,
                BirthColor: VfxCurve4.Const(Vector4.One),
                ColorOverLife: null,
                BirthVelocity: null,
                Acceleration: null,
                BirthRotationalVelocity: null,
                EmitterPosition: VfxCurve3.Const(Vector3.Zero),
                TexturePath: "particle.tex",
                TexDiv: Vector2.One,
                NumFrames: 1,
                RandomStartFrame: false,
                IsMeshPrimitive: false,
                RenderState: VfxEmitterRenderState.Default);
    }
}
