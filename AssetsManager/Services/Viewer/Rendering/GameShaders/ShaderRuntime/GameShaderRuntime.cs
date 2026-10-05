using AssetsManager.Services.Viewer.Resources;
using System;
using System.Collections.Generic;
using AssetsManager.Shaders;
using System.Numerics;
using System.Runtime.InteropServices;
using AssetsManager.Services.Viewer.Vfx.Resources;
using AssetsManager.Utils;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Wad;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    /// <summary>
    /// OpenGL execution layer for translated game material programs. Resolution/translation stay in
    /// GameShaderProgramResolver/Translator; this class owns only GL programs, UBOs and bindings.
    /// </summary>
    internal sealed partial class GameShaderRuntime : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate void DrawElementsDelegate(uint mode, int count, uint type, IntPtr indices);

        private bool _doubleSidedTransparent;

        internal void DrawBoundPass(DrawElementsDelegate draw, int count, IntPtr offset) =>
            DrawIndexedPass(draw, count, offset, _doubleSidedTransparent);

        internal void DrawBoundArrays(PrimitiveType mode, int count, uint instances = 0)
        {
            void Draw()
            {
                if (instances == 0) _gl.DrawArrays(mode, 0, (uint)count);
                else _gl.DrawArraysInstanced(mode, 0, (uint)count, instances);
            }
            if (!_doubleSidedTransparent) { Draw(); return; }
            _gl.Enable(EnableCap.CullFace);
            try
            {
                _gl.CullFace(TriangleFace.Front); Draw();
                _gl.CullFace(TriangleFace.Back); Draw();
            }
            finally { _gl.Disable(EnableCap.CullFace); }
        }

        internal void DrawIndexedPass(DrawElementsDelegate draw, int count, IntPtr offset, bool doubleSidedTransparent)
        {
            if (draw == null)
                return;
            if (!doubleSidedTransparent)
            {
                draw((uint)PrimitiveType.Triangles, count, (uint)DrawElementsType.UnsignedInt, offset);
                return;
            }

            // Three.js r185 renders transparent DoubleSide materials back, then front.
            _gl.Enable(EnableCap.CullFace);
            try
            {
                _gl.CullFace(TriangleFace.Front);
                draw((uint)PrimitiveType.Triangles, count, (uint)DrawElementsType.UnsignedInt, offset);
                _gl.CullFace(TriangleFace.Back);
                draw((uint)PrimitiveType.Triangles, count, (uint)DrawElementsType.UnsignedInt, offset);
            }
            finally
            {
                _gl.Disable(EnableCap.CullFace);
            }
        }

        private const string Globals = "$Globals";
        private const string MaterialTextureSuffix = "__TX";
        private const string SharedTextureSuffix = "_SharedTexture";
        private const string BakedLightTexture = "BAKED_LIGHT__TX";
        private const string StationaryLightTexture = "STATIONARY_LIGHT__TX";
        private const string BakedLightTransform = "BAKED_LIGHT_SCALE_AND_BIAS";
        private const string StationaryLightTransform = "STATIONARY_LIGHT_SCALE_AND_BIAS";
        private const int WriteDepth = 16;
        private const int WriteColor = 15;
        private const float FogStart = -100_000f;
        private const float FogEnd = -100_001f;

        internal readonly record struct Frame(
            Matrix4x4 View,
            Matrix4x4 Projection,
            Vector3 Eye,
            float TimeSeconds,
            MapSunData Sun,
            MapLightGridData LightGrid = null,
            Vector3 CharacterPosition = default,
            EnvironmentFrame Environment = default,
            uint SceneColor = 0,
            uint SceneDepth = 0,
            CubeMapData ImageLight = null);

        internal const string SceneColorTexture = "SAMPLER_BACK_BUFFER_COPY_SharedTexture";
        internal const string SceneDepthTexture = "sDepthTexture_SharedTexture";

        /// <summary>
        /// MapSkin inputs of the map shaders: <c>TERRAIN_XFORM</c>, the grass tint maps it addresses with
        /// the <c>GRASS_INTERP</c> weight of the alternate tint, and the environment cube of reflective
        /// materials. Zero textures fall back to the neutral tint and a black cube.
        /// </summary>
        internal readonly record struct EnvironmentFrame(
            Vector4 TerrainTransform,
            uint GrassTint,
            uint GrassTintAlternate,
            float GrassInterp,
            uint EnvironmentCube = 0,
            uint TerrainPaint = 0,
            float TransitionFactor = 1f);

        internal const string GrassTintTexture = "GRASS_TINT_MAP_SharedTexture";
        internal const string GrassTintAlternateTexture = "GRASS_TINT_MAP_ALTERNATE_SharedTexture";
        internal const string EnvironmentCubeTexture = "ENV_CUBE_SharedTexture";
        internal const string TerrainPaintTexture = "TERRAIN_BLEND_SharedTexture";
        internal const string LightGridTexture = "LIGHT_GRID_TEXTURE_SharedTexture";
        internal const string ImageLightTexture = "IBL_CUBEMAP_SharedTexture";
        private GameShaderLightGridTexture _lightGridTexture;
        private GameShaderImageLight _imageLight;
        private const string MeshCenter = "MESH_CENTER";

        private readonly record struct CharacterDraw(
            Matrix4x4 World,
            IReadOnlyList<Matrix4x4> Bones,
            float SelfIllumination = 0f);

        private sealed class BlockRuntime
        {
            internal GameShaderTranslator.UniformBlock Block;
            internal uint Buffer;
            internal uint Binding;
            internal float[] Data;
            internal float[] UploadedData;
            internal bool HasUploadedData;
        }

        private sealed record SamplerRuntime(
            string TextureName,
            GameShaderTranslator.TextureDimension Dimension,
            string LogicalSampler,
            int Location,
            uint Unit)
        {
            internal string MaterialName { get; } = TextureName.EndsWith(MaterialTextureSuffix, StringComparison.Ordinal)
                ? TextureName[..^MaterialTextureSuffix.Length] : TextureName;
        }

        private sealed class ProgramRuntime : IDisposable
        {
            private readonly GL _gl;
            internal readonly uint Program;
            internal readonly IReadOnlyList<BlockRuntime> Blocks;
            internal readonly IReadOnlyList<SamplerRuntime> Samplers;
            internal readonly IReadOnlyDictionary<uint, string> Attributes;

            /// <summary>The pixel stage writes a second target: the glow <c>FEATURE_BLOOM</c> shaders output.</summary>
            internal bool WritesBloom { get; set; }

            internal ProgramRuntime(
                GL gl,
                uint program,
                IReadOnlyList<BlockRuntime> blocks,
                IReadOnlyList<SamplerRuntime> samplers,
                IReadOnlyDictionary<uint, string> attributes)
            {
                _gl = gl;
                Program = program;
                Blocks = blocks;
                Samplers = samplers;
                Attributes = attributes;
            }

            public void Dispose()
            {
                foreach (BlockRuntime block in Blocks)
                    if (block.Buffer != 0)
                        _gl.DeleteBuffer(block.Buffer);
                if (Program != 0)
                    _gl.DeleteProgram(Program);
            }
        }

        private sealed class PassGlobals
        {
            internal readonly Dictionary<string, Vector4> Parameters = new(StringComparer.Ordinal);
            internal readonly Dictionary<string, bool> RuntimeSwitches = new(StringComparer.Ordinal);

            internal PassGlobals(GameMaterialPass pass)
            {
                foreach (GameMaterialParameter parameter in pass?.Parameters ?? Array.Empty<GameMaterialParameter>())
                {
                    if (!string.IsNullOrEmpty(parameter?.Name))
                        Parameters[parameter.Name] = parameter.Value;
                }
                foreach (KeyValuePair<string, bool> pair in pass?.RuntimeSwitches ?? Array.Empty<KeyValuePair<string, bool>>())
                {
                    if (!string.IsNullOrEmpty(pair.Key))
                        RuntimeSwitches[pair.Key] = pair.Value;
                }
            }
        }

        private sealed record PassRuntimeEntry(
            int PassIndex,
            ProgramRuntime Program,
            GameMaterialPass Pass,
            PassGlobals Globals);

        private sealed record CacheEntry(
            IReadOnlyList<PassRuntimeEntry> Passes,
            string Failure)
        {
            private GameMaterialState _dynamicState;
            private readonly Dictionary<string, Vector4> _dynamicParameters = new(StringComparer.Ordinal);

            // Re-evaluated only when the preview hands over a different game state.
            internal IReadOnlyDictionary<string, Vector4> DynamicParameters(ModelMaterialDefinition material, GameMaterialState state)
            {
                if (!ReferenceEquals(_dynamicState, state))
                {
                    _dynamicParameters.Clear();
                    // A state whose branch uses drivers the preview cannot evaluate keeps the resting value, never the
                    // static authoring value (Aatrox Skin33's body: Bloom_Intensity 10 static, 0 at rest).
                    foreach (var parameter in material.DynamicParameters)
                        if ((parameter.Evaluate(state) ?? parameter.Evaluate(GameMaterialState.Resting with { Gear = state.Gear, Time = state.Time })) is Vector4 value)
                            _dynamicParameters[parameter.Name] = value;
                    _dynamicState = state;
                }
                return _dynamicParameters;
            }
        }

        private readonly GL _gl;
        private readonly bool _gles;
        private readonly AppSettings _settings;
        private readonly string _shaderCachePath;
        private readonly WadFile _shaderCache;
        private readonly Dictionary<object, CacheEntry> _programs =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, ProgramRuntime> _sharedPrograms =
            new(StringComparer.Ordinal);
        private readonly Dictionary<(MapTextureWrap U, MapTextureWrap V, bool Min, bool Mag, string Shared), uint> _samplers = new();
        private readonly Dictionary<(string Material, int Pass, string Texture), string> _staticTextureKeys = new();
        private uint _neutralGrey2D;
        private uint _neutralBlack2D;
        private uint _neutralWhite2D;
        private uint _neutralBuffer2D;
        private readonly Dictionary<(GameShaderTranslator.TextureDimension Dimension, bool Black), uint> _neutralTypedTextures = new();
        private int _maxTextureUnits;
        private bool _disposed;

        internal GameShaderRuntime(GL gl, bool gles, AppSettings settings)
        {
            _gl = gl ?? throw new ArgumentNullException(nameof(gl));
            _gles = gles;
            _settings = settings;
            _shaderCachePath = GameShaderProgramResolver.FindShaderCachePath(settings);
            if (!string.IsNullOrWhiteSpace(_shaderCachePath))
            {
                try
                {
                    _shaderCache = new WadFile(_shaderCachePath);
                }
                catch
                {
                    _shaderCache = null;
                }
            }
        }

        internal int GetStaticPassCount(MapMaterialDefinition material)
        {
            if (_disposed || material?.Program == null || material.Program.Kind != GameMaterialKind.StaticMesh)
                return 0;
            CacheEntry entry = GetOrCreate(material, material.Program);
            // LTK Backdrop uses programWith: only the first translated terrain pass.
            return Math.Min(1, entry?.Passes?.Count ?? 0);
        }

        internal GameMaterialPassState GetStaticPassState(MapMaterialDefinition material) =>
            GetStaticPassCount(material) > 0
                ? GetOrCreate(material, material.Program).Passes[0].Pass.State
                : null;

        /// <returns>Whether the pass's pixel shader writes glow to its second target (<see cref="GameShaderBloom"/>).</returns>
        internal bool WritesBloom(ModelMaterialDefinition material, int passIndex)
        {
            int count = GetSkinnedPassCount(material);
            return passIndex >= 0 && passIndex < count &&
                   GetOrCreate(material, material.Program).Passes[passIndex].Program?.WritesBloom == true;
        }

        internal static bool WritesSecondTarget(string pixelGlsl) =>
            pixelGlsl?.Contains("layout(location = 1) out", StringComparison.Ordinal) == true;

        internal GameMaterialPassState GetSkinnedPassState(ModelMaterialDefinition material, int passIndex)
        {
            int count = GetSkinnedPassCount(material);
            return passIndex >= 0 && passIndex < count
                ? GetOrCreate(material, material.Program).Passes[passIndex].Pass.State
                : null;
        }

        internal bool TryBind(
            MapMaterialDefinition material,
            MapGeometryMeshData mesh,
            bool meshDoubleSided,
            in Frame frame,
            Func<string, uint?> programTexture,
            Func<string, uint?> lightmapTexture) =>
            TryBind(material, 0, mesh, meshDoubleSided, in frame, programTexture, lightmapTexture);

        /// <summary>Static-mesh stream locations for COLOR and TEXCOORD5 (see <see cref="AttributeLocations"/>).</summary>
        internal const uint StaticColorLocation = 4;
        internal const uint StaticPivotLocation = 5;

        internal bool TryBind(
            MapMaterialDefinition material,
            int passIndex,
            MapGeometryMeshData mesh,
            bool meshDoubleSided,
            in Frame frame,
            Func<string, uint?> programTexture,
            Func<string, uint?> lightmapTexture,
            bool hasColors = false,
            bool hasPivots = false)
        {
            if (_disposed || material?.Program == null || material.Program.Kind != GameMaterialKind.StaticMesh)
                return false;

            CacheEntry entry = GetOrCreate(material, material.Program);
            if (entry?.Passes == null || passIndex < 0 || passIndex >= entry.Passes.Count)
                return false;

            PassRuntimeEntry passEntry = entry.Passes[passIndex];
            ProgramRuntime runtime = passEntry.Program;
            if (runtime == null)
                return false;

            _gl.UseProgram(runtime.Program);
            ApplyGenericAttributeDefaults(
                runtime.Attributes,
                GameMaterialKind.StaticMesh,
                hasTangents: false,
                hasColors,
                hasPivots);
            UpdateBlocks(runtime, passEntry.Globals, mesh, frame, null);
            BindTextures(runtime, passEntry.Pass, passEntry.PassIndex, material, mesh, frame, programTexture, lightmapTexture);
            ApplyPassState(passEntry.Pass.State, meshDoubleSided);
            return true;
        }

        internal int GetSkinnedPassCount(ModelMaterialDefinition material)
        {
            if (_disposed || material?.Program == null || material.Program.Kind != GameMaterialKind.SkinnedMesh)
                return 0;
            CacheEntry entry = GetOrCreate(material, material.Program);
            return entry?.Passes?.Count ?? 0;
        }

        internal bool TryBindSkinned(
            ModelMaterialDefinition material,
            Matrix4x4 world,
            IReadOnlyList<Matrix4x4> bones,
            bool hasTangents,
            in Frame frame,
            Func<string, uint?> programTexture,
            float selfIllumination = 0f) =>
            TryBindSkinned(material, 0, world, bones, hasTangents, in frame, programTexture, selfIllumination);

        internal bool TryBindSkinned(
            ModelMaterialDefinition material,
            int passIndex,
            Matrix4x4 world,
            IReadOnlyList<Matrix4x4> bones,
            bool hasTangents,
            in Frame frame,
            Func<string, uint?> programTexture,
            float selfIllumination = 0f,
            GameMaterialState state = null,
            bool hasColors = false)
        {
            // Time drivers read the frame's preview clock.
            state = (state ?? GameMaterialState.Resting) with { Time = frame.TimeSeconds };
            if (_disposed || material?.Program == null || material.Program.Kind != GameMaterialKind.SkinnedMesh)
                return false;

            CacheEntry entry = GetOrCreate(material, material.Program);
            if (entry?.Passes == null || passIndex < 0 || passIndex >= entry.Passes.Count)
                return false;

            PassRuntimeEntry passEntry = entry.Passes[passIndex];
            ProgramRuntime runtime = passEntry.Program;
            if (runtime == null)
                return false;

            _gl.UseProgram(runtime.Program);
            ApplyGenericAttributeDefaults(runtime.Attributes, GameMaterialKind.SkinnedMesh, hasTangents, hasColors);
            UpdateBlocks(runtime, passEntry.Globals, null, frame, new CharacterDraw(world, bones, selfIllumination),
                overrides: entry.DynamicParameters(material, state));
            BindSkinnedTextures(runtime, passEntry.Pass, programTexture, material, state, frame);
            ApplyPassState(passEntry.Pass.State, meshDoubleSided: false);
            return true;
        }

        internal string FailureFor(MapMaterialDefinition material) =>
            material != null && _programs.TryGetValue(material, out CacheEntry entry)
                ? entry.Failure
                : null;

        internal string FailureFor(ModelMaterialDefinition material) =>
            material != null && _programs.TryGetValue(material, out CacheEntry entry)
                ? entry.Failure
                : null;

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _programs.Clear();
            _staticTextureKeys.Clear();
            _particleSamplerStates.Clear();
            foreach (ProgramRuntime program in _sharedPrograms.Values)
                program?.Dispose();
            _sharedPrograms.Clear();
            _bytecodeReads.Clear();
            _cachedBytecodeBytes = 0;
            _shaderCache?.Dispose();
            _lightGridTexture?.Dispose();
            _lightGridTexture = null;
            _imageLight?.Dispose();
            _imageLight = null;
            foreach (uint sampler in _samplers.Values)
                if (sampler != 0)
                    _gl.DeleteSampler(sampler);
            _samplers.Clear();
            if (_neutralGrey2D != 0) _gl.DeleteTexture(_neutralGrey2D);
            if (_neutralBlack2D != 0) _gl.DeleteTexture(_neutralBlack2D);
            if (_neutralWhite2D != 0) _gl.DeleteTexture(_neutralWhite2D);
            if (_neutralBuffer2D != 0) _gl.DeleteTexture(_neutralBuffer2D);
            foreach (uint texture in _neutralTypedTextures.Values)
                if (texture != 0)
                    _gl.DeleteTexture(texture);
            _neutralTypedTextures.Clear();
            _neutralGrey2D = _neutralBlack2D = _neutralWhite2D = _neutralBuffer2D = 0;
        }
    }
}

