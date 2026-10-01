using System;
using System.Linq;
using AssetsManager.Shaders;
using System.Numerics;
using AssetsManager.Services.Viewer.Loading;
using AssetsManager.Views.Models.Viewer;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    internal sealed partial class GameShaderRuntime
    {
        private void BindSkinnedTextures(
            ProgramRuntime runtime,
            GameMaterialPass pass,
            Func<string, uint?> programTexture,
            ModelMaterialDefinition material,
            GameMaterialState state,
            in Frame frame)
        {
            foreach (SamplerRuntime sampler in runtime.Samplers)
            {
                // Resolving a texture may upload it, and an upload binds on the active unit. Activating this
                // sampler's unit first keeps a lazy upload from replacing the previous sampler's texture.
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + sampler.Unit));
                string name = sampler.TextureName;
                uint texture;
                TextureTarget target;
                uint samplerObject;

                if (name == ImageLightTexture &&
                    sampler.Dimension == GameShaderTranslator.TextureDimension.CubeArray &&
                    (_imageLight ??= new GameShaderImageLight(_gl)).Resolve(frame.ImageLight) is uint imageLight &&
                    imageLight != 0)
                {
                    texture = imageLight;
                    target = TextureTarget.Texture2DArray;
                    samplerObject = ResolveSampler(new GameMaterialSamplerState(
                        null, MapTextureWrap.Clamp, MapTextureWrap.Clamp, MapTextureWrap.Clamp, true, true));
                }
                else if (name == EnvironmentCubeTexture &&
                         sampler.Dimension == GameShaderTranslator.TextureDimension.Cube &&
                         (_imageLight ??= new GameShaderImageLight(_gl)).ResolveCube(frame.ImageLight) is uint reflection &&
                         reflection != 0)
                {
                    texture = reflection;
                    target = TextureTarget.TextureCubeMap;
                    samplerObject = ResolveSampler(new GameMaterialSamplerState(
                        null, MapTextureWrap.Clamp, MapTextureWrap.Clamp, MapTextureWrap.Clamp, true, true));
                }
                else if (name == LightGridTexture && sampler.Dimension == GameShaderTranslator.TextureDimension.Texture2DArray)
                {
                    Span<Vector3> cube = stackalloc Vector3[6];
                    ResolveAmbientCube(frame, cube);
                    texture = (_lightGridTexture ??= new GameShaderLightGridTexture(_gl)).Update(cube);
                    target = TextureTarget.Texture2DArray;
                    samplerObject = ResolveNeutralSampler(clamp: true, sampler.Dimension);
                }
                else if (name.EndsWith(SharedTextureSuffix, StringComparison.Ordinal))
                {
                    (texture, target) = NeutralFor(sampler.Dimension, black: true);
                    samplerObject = ResolveNeutralSampler(clamp: true, sampler.Dimension);
                }
                else
                {
                    string own = name.EndsWith(MaterialTextureSuffix, StringComparison.Ordinal)
                        ? name[..^MaterialTextureSuffix.Length]
                        : name;
                    GameMaterialTexture declared = pass.Textures?
                        .FirstOrDefault(item => string.Equals(item.Name, own, StringComparison.Ordinal));
                    string authoredPath = material.ResolveTextureSwap(own, state) ?? declared?.Texture?.VirtualPath;
                    if (string.IsNullOrWhiteSpace(authoredPath) && declared?.Texture?.PathHash > 0)
                        authoredPath = declared.Texture.PathHash.ToString("x16");
                    uint? loaded = sampler.Dimension == GameShaderTranslator.TextureDimension.Texture2D &&
                                   !string.IsNullOrWhiteSpace(authoredPath)
                        ? programTexture?.Invoke(authoredPath)
                        : null;
                    if (loaded.HasValue && loaded.Value != 0)
                    {
                        texture = loaded.Value;
                        target = TextureTarget.Texture2D;
                        samplerObject = declared != null
                            ? ResolveSampler(declared.Sampler)
                            : ResolveNeutralSampler(clamp: false);
                    }
                    else
                    {
                        bool isBlackDefault = string.Equals(own, "EMISSIVE_MAP", StringComparison.OrdinalIgnoreCase) ||
                                              own.IndexOf("emissive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              own.IndexOf("glow", StringComparison.OrdinalIgnoreCase) >= 0;
                        (texture, target) = NeutralFor(sampler.Dimension, black: isBlackDefault);
                        samplerObject = ResolveNeutralSampler(clamp: true, sampler.Dimension);
                    }
                }

                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + sampler.Unit));
                _gl.BindTexture(target, texture);
                _gl.BindSampler(sampler.Unit, samplerObject);
            }
            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        private void BindTextures(
            ProgramRuntime runtime,
            GameMaterialPass pass,
            int passIndex,
            MapMaterialDefinition material,
            MapGeometryMeshData mesh,
            in Frame frame,
            Func<string, uint?> programTexture,
            Func<string, uint?> lightmapTexture)
        {
            foreach (SamplerRuntime sampler in runtime.Samplers)
            {
                // Resolving a texture may upload it, and an upload binds on the active unit. Activating this
                // sampler's unit first keeps a lazy upload from replacing the previous sampler's texture.
                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + sampler.Unit));
                string name = sampler.TextureName;
                uint texture;
                TextureTarget target;
                uint samplerObject;

                if (name == BakedLightTexture)
                {
                    uint? loaded = ResolveLightmap(mesh?.BakedLight, lightmapTexture);
                    texture = loaded ?? NeutralWhite2D();
                    target = TextureTarget.Texture2D;
                    samplerObject = loaded.HasValue ? ResolveLightmapSampler() : ResolveNeutralSampler(clamp: true);
                }
                else if (name == StationaryLightTexture)
                {
                    uint? loaded = ResolveLightmap(mesh?.StationaryLight, lightmapTexture);
                    texture = loaded ?? NeutralWhite2D();
                    target = TextureTarget.Texture2D;
                    samplerObject = loaded.HasValue ? ResolveLightmapSampler() : ResolveNeutralSampler(clamp: true);
                }
                else if (ScreenTextureFor(name, frame) is uint screen && screen != 0)
                {
                    texture = screen;
                    target = TextureTarget.Texture2D;
                    samplerObject = ResolveNeutralSampler(clamp: true);
                }
                else if (name == EnvironmentCubeTexture &&
                         sampler.Dimension == GameShaderTranslator.TextureDimension.Cube &&
                         frame.Environment.EnvironmentCube != 0)
                {
                    texture = frame.Environment.EnvironmentCube;
                    target = TextureTarget.TextureCubeMap;
                    samplerObject = ResolveNeutralSampler(clamp: true, sampler.Dimension);
                }
                else if (name == TerrainPaintTexture &&
                         sampler.Dimension == GameShaderTranslator.TextureDimension.Texture2DArray &&
                         frame.Environment.TerrainPaint != 0)
                {
                    texture = frame.Environment.TerrainPaint;
                    target = TextureTarget.Texture2DArray;
                    samplerObject = ResolveNeutralSampler(clamp: true, sampler.Dimension);
                }
                else if (IsMultiplicativeSharedTexture(name, sampler.Dimension))
                {
                    uint tint = GrassTintFor(name, frame.Environment);
                    texture = tint != 0 ? tint : NeutralWhite2D();
                    target = TextureTarget.Texture2D;
                    samplerObject = ResolveNeutralSampler(clamp: true, sampler.Dimension);
                }
                else if (name.EndsWith(SharedTextureSuffix, StringComparison.Ordinal))
                {
                    (texture, target) = NeutralFor(sampler.Dimension, black: true);
                    samplerObject = ResolveNeutralSampler(clamp: true, sampler.Dimension);
                }
                else
                {
                    string own = name.EndsWith(MaterialTextureSuffix, StringComparison.Ordinal)
                        ? name[..^MaterialTextureSuffix.Length]
                        : name;
                    GameMaterialTexture declared = pass.Textures?
                        .FirstOrDefault(item => string.Equals(item.Name, own, StringComparison.Ordinal));
                    uint? loaded = sampler.Dimension == GameShaderTranslator.TextureDimension.Texture2D
                        ? ResolveStaticProgramTexture(material.Name, passIndex, own, programTexture)
                        : null;
                    if (loaded.HasValue && loaded.Value != 0)
                    {
                        texture = loaded.Value;
                        target = TextureTarget.Texture2D;
                        samplerObject = declared != null ? ResolveSampler(declared.Sampler) : ResolveNeutralSampler(clamp: false);
                    }
                    else
                    {
                        (texture, target) = NeutralFor(sampler.Dimension, black: false);
                        samplerObject = ResolveNeutralSampler(clamp: true, sampler.Dimension);
                    }
                }

                _gl.ActiveTexture((TextureUnit)((int)TextureUnit.Texture0 + sampler.Unit));
                _gl.BindTexture(target, texture);
                _gl.BindSampler(sampler.Unit, samplerObject);
            }
            _gl.ActiveTexture(TextureUnit.Texture0);
        }

        /// <summary>
        /// Engine textures the shader multiplies into the colour (the map's grass tint). They bind the MapSkin
        /// tint when the scene has one; otherwise white leaves the albedo as authored where black would draw nothing.
        /// </summary>
        internal static bool IsMultiplicativeSharedTexture(string name, GameShaderTranslator.TextureDimension dimension) =>
            dimension == GameShaderTranslator.TextureDimension.Texture2D &&
            name != null &&
            name.StartsWith("GRASS_TINT_MAP", StringComparison.Ordinal) &&
            name.EndsWith(SharedTextureSuffix, StringComparison.Ordinal);

        /// <summary>The framebuffer capture bound to a scene colour/depth sampler, or 0 when none was taken.</summary>
        internal static uint ScreenTextureFor(string name, in Frame frame) => name switch
        {
            SceneColorTexture => frame.SceneColor,
            SceneDepthTexture => frame.SceneDepth,
            _ => 0
        };

        /// <summary>Whether a static material samples the scene colour or depth (e.g. refracting water).</summary>
        internal bool ReadsScreenTextures(MapMaterialDefinition material)
        {
            if (_disposed || material?.Program == null || material.Program.Kind != GameMaterialKind.StaticMesh)
                return false;
            CacheEntry entry = GetOrCreate(material, material.Program);
            return entry?.Passes?.Any(pass => pass.Program?.Samplers.Any(sampler =>
                sampler.TextureName is SceneColorTexture or SceneDepthTexture) == true) == true;
        }

        /// <summary>The MapSkin grass tint bound to a grass tint sampler; the alternate falls back to the base tint.</summary>
        internal static uint GrassTintFor(string name, in EnvironmentFrame environment) => name switch
        {
            GrassTintTexture => environment.GrassTint,
            GrassTintAlternateTexture => environment.GrassTintAlternate != 0 ? environment.GrassTintAlternate : environment.GrassTint,
            _ => 0
        };

        internal static uint? ResolveStaticProgramTexture(
            string material, int authoredPassIndex, string texture, Func<string, uint?> lookup) =>
            lookup?.Invoke(MapTextureLoadingService.ProgramTextureKey(material, authoredPassIndex, texture));

        private static uint? ResolveLightmap(
            MapGeometryLightChannelData channel,
            Func<string, uint?> lookup) =>
            channel?.IsEmpty == false ? lookup?.Invoke(channel.Texture) : null;

        private uint ResolveSampler(GameMaterialSamplerState state)
        {
            if (state == null)
                return ResolveNeutralSampler(clamp: false);

            MapTextureWrap u = state.WrapU;
            MapTextureWrap v = state.WrapV;
            bool min = state.FilterMin;
            bool mag = state.FilterMag;
            string shared = state.SharedSampler ?? string.Empty;
            if (shared.Contains("Clamp", StringComparison.Ordinal))
                u = v = MapTextureWrap.Clamp;
            else if (shared.Contains("Wrap", StringComparison.Ordinal))
                u = v = MapTextureWrap.Repeat;
            if (shared.Contains("No_Mip", StringComparison.Ordinal))
                min = true;

            var key = (u, v, min, mag, shared);
            if (_samplers.TryGetValue(key, out uint cached))
                return cached;

            uint sampler = _gl.GenSampler();
            _gl.SamplerParameter(
                sampler,
                SamplerParameterI.MinFilter,
                shared.Contains("No_Mip", StringComparison.Ordinal)
                    ? (int)TextureMinFilter.Linear
                    : min ? (int)TextureMinFilter.LinearMipmapLinear : (int)TextureMinFilter.NearestMipmapNearest);
            _gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, mag ? (int)TextureMagFilter.Linear : (int)TextureMagFilter.Nearest);
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)ToWrap(u));
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)ToWrap(v));
            _samplers[key] = sampler;
            return sampler;
        }

        private uint ResolveLightmapSampler()
        {
            var key = (MapTextureWrap.Clamp, MapTextureWrap.Clamp, true, true, "lightmap-mips");
            if (_samplers.TryGetValue(key, out uint sampler))
                return sampler;
            sampler = _gl.GenSampler();
            _gl.SamplerParameter(sampler, SamplerParameterI.MinFilter, (int)TextureMinFilter.LinearMipmapLinear);
            _gl.SamplerParameter(sampler, SamplerParameterI.MagFilter, (int)TextureMagFilter.Linear);
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)TextureWrapMode.ClampToEdge);
            _samplers[key] = sampler;
            return sampler;
        }

        private uint ResolveNeutralSampler(
            bool clamp,
            GameShaderTranslator.TextureDimension dimension = GameShaderTranslator.TextureDimension.Texture2D)
        {
            bool integerBuffer = RequiresIntegerNeutral(dimension);
            var key = (
                clamp ? MapTextureWrap.Clamp : MapTextureWrap.Repeat,
                clamp ? MapTextureWrap.Clamp : MapTextureWrap.Repeat,
                !integerBuffer,
                !integerBuffer,
                integerBuffer ? "neutral-buffer" : "neutral-no-mip");
            if (_samplers.TryGetValue(key, out uint sampler))
                return sampler;
            sampler = _gl.GenSampler();
            _gl.SamplerParameter(
                sampler,
                SamplerParameterI.MinFilter,
                (int)(integerBuffer ? TextureMinFilter.Nearest : TextureMinFilter.Linear));
            _gl.SamplerParameter(
                sampler,
                SamplerParameterI.MagFilter,
                (int)(integerBuffer ? TextureMagFilter.Nearest : TextureMagFilter.Linear));
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapS, (int)(clamp ? TextureWrapMode.ClampToEdge : TextureWrapMode.Repeat));
            _gl.SamplerParameter(sampler, SamplerParameterI.WrapT, (int)(clamp ? TextureWrapMode.ClampToEdge : TextureWrapMode.Repeat));
            _samplers[key] = sampler;
            return sampler;
        }

        internal static bool RequiresIntegerNeutral(GameShaderTranslator.TextureDimension dimension) =>
            dimension == GameShaderTranslator.TextureDimension.Buffer;

        private (uint Texture, TextureTarget Target) NeutralFor(
            GameShaderTranslator.TextureDimension dimension,
            bool black)
        {
            return dimension switch
            {
                GameShaderTranslator.TextureDimension.Texture2DArray or
                GameShaderTranslator.TextureDimension.CubeArray =>
                    (NeutralTyped(dimension, black), TextureTarget.Texture2DArray),
                GameShaderTranslator.TextureDimension.Texture3D =>
                    (NeutralTyped(dimension, black), TextureTarget.Texture3D),
                GameShaderTranslator.TextureDimension.Cube =>
                    (NeutralTyped(dimension, black), TextureTarget.TextureCubeMap),
                // Hexshade lowers texel buffers to integer sampler2D data textures. Their neutral
                // must therefore be R32UI/nearest rather than an RGBA colour texture.
                GameShaderTranslator.TextureDimension.Buffer =>
                    (NeutralBuffer2D(), TextureTarget.Texture2D),
                // Unknown dimensions follow the neutral fallback rather than binding a real asset to a wrong target.
                _ => (black ? NeutralBlack2D() : NeutralGrey2D(), TextureTarget.Texture2D)
            };
        }

        private uint NeutralTyped(GameShaderTranslator.TextureDimension dimension, bool black)
        {
            var key = (dimension, black);
            if (_neutralTypedTextures.TryGetValue(key, out uint cached))
                return cached;

            byte value = black ? (byte)0 : (byte)128;
            uint texture = dimension switch
            {
                GameShaderTranslator.TextureDimension.Texture2DArray => CreateNeutralArray(value, value, value, black ? (byte)0 : (byte)255, 1),
                GameShaderTranslator.TextureDimension.CubeArray => CreateNeutralArray(value, value, value, black ? (byte)0 : (byte)255, 6),
                GameShaderTranslator.TextureDimension.Texture3D => CreateNeutral3D(value, value, value, black ? (byte)0 : (byte)255),
                GameShaderTranslator.TextureDimension.Cube => CreateNeutralCube(value, value, value, black ? (byte)0 : (byte)255),
                _ => black ? NeutralBlack2D() : NeutralGrey2D()
            };
            _neutralTypedTextures[key] = texture;
            return texture;
        }

        private uint NeutralGrey2D() => _neutralGrey2D != 0 ? _neutralGrey2D : (_neutralGrey2D = CreateNeutral(128, 128, 128, 255));
        private uint NeutralBlack2D() => _neutralBlack2D != 0 ? _neutralBlack2D : (_neutralBlack2D = CreateNeutral(0, 0, 0, 0));
        private uint NeutralWhite2D() => _neutralWhite2D != 0 ? _neutralWhite2D : (_neutralWhite2D = CreateNeutral(255, 255, 255, 255));

        private uint NeutralBuffer2D()
        {
            if (_neutralBuffer2D != 0)
                return _neutralBuffer2D;

            _neutralBuffer2D = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _neutralBuffer2D);
            uint[] zero = { 0u };
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.R32ui,
                1,
                1,
                0,
                PixelFormat.RedInteger,
                PixelType.UnsignedInt,
                new ReadOnlySpan<uint>(zero));
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return _neutralBuffer2D;
        }

        private uint CreateNeutral(byte r, byte g, byte b, byte a)
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, texture);
            byte[] pixel = { r, g, b, a };
            _gl.TexImage2D(
                TextureTarget.Texture2D,
                0,
                InternalFormat.Rgba8,
                1,
                1,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(pixel));
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.BindTexture(TextureTarget.Texture2D, 0);
            return texture;
        }

        private uint CreateNeutralArray(byte r, byte g, byte b, byte a, int layers)
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2DArray, texture);
            byte[] pixels = new byte[Math.Max(1, layers) * 4];
            for (int layer = 0; layer < Math.Max(1, layers); layer++)
            {
                int at = layer * 4;
                pixels[at] = r;
                pixels[at + 1] = g;
                pixels[at + 2] = b;
                pixels[at + 3] = a;
            }
            _gl.TexImage3D(
                TextureTarget.Texture2DArray,
                0,
                InternalFormat.Rgba8,
                1,
                1,
                (uint)Math.Max(1, layers),
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(pixels));
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture2DArray, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.BindTexture(TextureTarget.Texture2DArray, 0);
            return texture;
        }

        private uint CreateNeutral3D(byte r, byte g, byte b, byte a)
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture3D, texture);
            byte[] pixel = { r, g, b, a };
            _gl.TexImage3D(
                TextureTarget.Texture3D,
                0,
                InternalFormat.Rgba8,
                1,
                1,
                1,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedByte,
                new ReadOnlySpan<byte>(pixel));
            _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.Texture3D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.BindTexture(TextureTarget.Texture3D, 0);
            return texture;
        }

        private uint CreateNeutralCube(byte r, byte g, byte b, byte a)
        {
            uint texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.TextureCubeMap, texture);
            byte[] pixel = { r, g, b, a };
            TextureTarget[] faces =
            {
                TextureTarget.TextureCubeMapPositiveX,
                TextureTarget.TextureCubeMapNegativeX,
                TextureTarget.TextureCubeMapPositiveY,
                TextureTarget.TextureCubeMapNegativeY,
                TextureTarget.TextureCubeMapPositiveZ,
                TextureTarget.TextureCubeMapNegativeZ
            };
            foreach (TextureTarget face in faces)
            {
                _gl.TexImage2D(
                    face,
                    0,
                    InternalFormat.Rgba8,
                    1,
                    1,
                    0,
                    PixelFormat.Rgba,
                    PixelType.UnsignedByte,
                    new ReadOnlySpan<byte>(pixel));
            }
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            _gl.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            _gl.BindTexture(TextureTarget.TextureCubeMap, 0);
            return texture;
        }
    }
}
