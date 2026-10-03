using System;
using Silk.NET.OpenGL;

namespace AssetsManager.Services.Viewer.Vfx.Rendering
{
    internal sealed class VfxShaderUniforms
    {
        private readonly GL _gl;
        private UniformValue[] _values = Array.Empty<UniformValue>();
        internal int UploadCount { get; private set; }

        private struct UniformValue
        {
            internal int Kind, X, Y, Z, W;
        }

        private bool Changed(int location, int kind, int x, int y = 0, int z = 0, int w = 0)
        {
            if (location < 0) return false;
            if (location >= _values.Length)
                Array.Resize(ref _values, Math.Max(location + 1, Math.Max(32, _values.Length * 2)));
            ref UniformValue previous = ref _values[location];
            if (previous.Kind == kind && previous.X == x && previous.Y == y && previous.Z == z && previous.W == w)
                return false;
            previous.Kind = kind;
            previous.X = x;
            previous.Y = y;
            previous.Z = z;
            previous.W = w;
            UploadCount++;
            return true;
        }

        // Uniform state belongs to the linked program, so it survives draws of other programs.
        // Keep float bits: signed zero and NaN payloads can carry authored shader semantics.
        internal void Uniform1(int location, int value)
        {
            if (location >= 0 && Changed(location, 1, value)) _gl.Uniform1(location, value);
        }

        internal void Uniform1(int location, float value)
        {
            if (location >= 0 && Changed(location, 2, BitConverter.SingleToInt32Bits(value))) _gl.Uniform1(location, value);
        }

        internal void Uniform2(int location, float x, float y)
        {
            if (location >= 0 && Changed(location, 3, BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(y)))
                _gl.Uniform2(location, x, y);
        }

        internal void Uniform3(int location, float x, float y, float z)
        {
            if (location >= 0 && Changed(location, 4, BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(y), BitConverter.SingleToInt32Bits(z)))
                _gl.Uniform3(location, x, y, z);
        }

        internal void Uniform4(int location, float x, float y, float z, float w)
        {
            if (location >= 0 && Changed(location, 5, BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(y), BitConverter.SingleToInt32Bits(z), BitConverter.SingleToInt32Bits(w)))
                _gl.Uniform4(location, x, y, z, w);
        }

        internal readonly int AddressMode;
        internal readonly int AddressModeMult;
        internal readonly int AlignPitchToCamera;
        internal readonly int AlignYawToCamera;
        internal readonly int AlphaCutoff;
        internal readonly int AlphaTest;
        internal readonly int ArbitraryQuad;
        internal readonly int AttachedMesh;
        internal readonly int BirthUvOffset;
        internal readonly int CamPos;
        internal readonly int CamRight;
        internal readonly int CamUp;
        internal readonly int ClampUv;
        internal readonly int ClampUvMult;
        internal readonly int Color;
        internal readonly int ColorLookUpOffsets;
        internal readonly int ColorLookUpScales;
        internal readonly int ColorLookUpTypeX;
        internal readonly int ColorLookUpTypeY;
        internal readonly int ColorMap;
        internal readonly int ColorRenderFlags;
        internal readonly int DepthProjection;
        internal readonly int DepthPushPull;
        internal readonly int DirectionOriented;
        internal readonly int DistortionStrength;
        internal readonly int DistortionTex;
        internal readonly int EmissiveStrength;
        internal readonly int EmitterUvOffset;
        internal readonly int EmitterUvOffsetMult;
        internal readonly int ErosionAddressMode;
        internal readonly int ErosionDefault;
        internal readonly int ErosionDrive;
        internal readonly int ErosionFeatherIn;
        internal readonly int ErosionFeatherOut;
        internal readonly int ErosionMixer;
        internal readonly int ErosionSliceWidth;
        internal readonly int ErosionTex;
        internal readonly int FlipU;
        internal readonly int FlipUMult;
        internal readonly int FlipV;
        internal readonly int FlipVMult;
        internal readonly int Frame;
        internal readonly int Fresnel;
        internal readonly int HasColor;
        internal readonly int HasErosion;
        internal readonly int HasErosionMap;
        internal readonly int HasPalette;
        internal readonly int HasReflection;
        internal readonly int HasSoftParticle;
        internal readonly int HasTex;
        internal readonly int HasTexMult;
        internal readonly int IsDistortion;
        internal readonly int IsGroundLayer;
        internal readonly int LegacyOrientation;
        internal readonly int MaterialAddressU;
        internal readonly int MaterialAddressV;
        internal readonly int MaterialPremultiplied;
        internal readonly int MaterialRepeat;
        internal readonly int MaterialTint;
        internal readonly int MeshSkinned;
        internal readonly int OrbitRotation;
        internal readonly int OwnerWorld;
        internal readonly int PaletteAddressMode;
        internal readonly int PaletteCount;
        internal readonly int PaletteMap;
        internal readonly int PaletteMixMask;
        internal readonly int PaletteScroll;
        internal readonly int PaletteSelector;
        internal readonly int PivotUp;
        internal readonly int PlacementForward;
        internal readonly int PlacementRight;
        internal readonly int PlacementUp;
        internal readonly int PrimitiveKind;
        internal readonly int RampAtMult;
        internal readonly int Reflection;
        internal readonly int ReflectionColor;
        internal readonly int ReflectionTex;
        internal readonly int Rotation;
        internal readonly int Scale;
        internal readonly int SceneDepthTex;
        internal readonly int SceneTex;
        internal readonly int SoftParticleControl;
        internal readonly int SoftParticleParams;
        internal readonly int Tex;
        internal readonly int TexDiv;
        internal readonly int TexDivMult;
        internal readonly int TexMult;
        internal readonly int TexSize;
        internal readonly int TexSizeMult;
        internal readonly int UseCustomMaterial;
        internal readonly int UseSkinning;
        internal readonly int UvMode;
        internal readonly int UvOffsetMult;
        internal readonly int UvRotation;
        internal readonly int UvRotationMult;
        internal readonly int UvScale;
        internal readonly int UvScaleMult;
        internal readonly int UvScrollRateMult;
        internal readonly int UvTransformCenter;
        internal readonly int UvTransformCenterMult;
        internal readonly int ViewProj;
        internal readonly int ViewportSize;
        internal readonly int InverseViewProj;
        internal readonly int TerrainMode;
        internal readonly int TerrainDepth;
        internal readonly int ProjectionBand;
        internal readonly int WireframeColor;
        internal readonly int WireframePass;
        internal readonly int WorldPos;
        internal readonly int GamePremultiplied;
        internal readonly int GameLookupDrivers;

        internal VfxShaderUniforms(GL gl, uint program)
        {
            _gl = gl;
            AddressMode = gl.GetUniformLocation(program, "uAddressMode");
            AddressModeMult = gl.GetUniformLocation(program, "uAddressModeMult");
            AlignPitchToCamera = gl.GetUniformLocation(program, "uAlignPitchToCamera");
            AlignYawToCamera = gl.GetUniformLocation(program, "uAlignYawToCamera");
            AlphaCutoff = gl.GetUniformLocation(program, "uAlphaCutoff");
            AlphaTest = gl.GetUniformLocation(program, "uAlphaTest");
            ArbitraryQuad = gl.GetUniformLocation(program, "uArbitraryQuad");
            AttachedMesh = gl.GetUniformLocation(program, "uAttachedMesh");
            BirthUvOffset = gl.GetUniformLocation(program, "uBirthUvOffset");
            CamPos = gl.GetUniformLocation(program, "uCamPos");
            CamRight = gl.GetUniformLocation(program, "uCamRight");
            CamUp = gl.GetUniformLocation(program, "uCamUp");
            ClampUv = gl.GetUniformLocation(program, "uClampUv");
            ClampUvMult = gl.GetUniformLocation(program, "uClampUvMult");
            Color = gl.GetUniformLocation(program, "uColor");
            ColorLookUpOffsets = gl.GetUniformLocation(program, "uColorLookUpOffsets");
            ColorLookUpScales = gl.GetUniformLocation(program, "uColorLookUpScales");
            ColorLookUpTypeX = gl.GetUniformLocation(program, "uColorLookUpTypeX");
            ColorLookUpTypeY = gl.GetUniformLocation(program, "uColorLookUpTypeY");
            ColorMap = gl.GetUniformLocation(program, "uColorMap");
            ColorRenderFlags = gl.GetUniformLocation(program, "uColorRenderFlags");
            DepthProjection = gl.GetUniformLocation(program, "uDepthProjection");
            DepthPushPull = gl.GetUniformLocation(program, "uDepthPushPull");
            DirectionOriented = gl.GetUniformLocation(program, "uDirectionOriented");
            DistortionStrength = gl.GetUniformLocation(program, "uDistortionStrength");
            DistortionTex = gl.GetUniformLocation(program, "uDistortionTex");
            EmissiveStrength = gl.GetUniformLocation(program, "uEmissiveStrength");
            EmitterUvOffset = gl.GetUniformLocation(program, "uEmitterUvOffset");
            EmitterUvOffsetMult = gl.GetUniformLocation(program, "uEmitterUvOffsetMult");
            ErosionAddressMode = gl.GetUniformLocation(program, "uErosionAddressMode");
            ErosionDefault = gl.GetUniformLocation(program, "uErosionDefault");
            ErosionDrive = gl.GetUniformLocation(program, "uErosionDrive");
            ErosionFeatherIn = gl.GetUniformLocation(program, "uErosionFeatherIn");
            ErosionFeatherOut = gl.GetUniformLocation(program, "uErosionFeatherOut");
            ErosionMixer = gl.GetUniformLocation(program, "uErosionMixer");
            ErosionSliceWidth = gl.GetUniformLocation(program, "uErosionSliceWidth");
            ErosionTex = gl.GetUniformLocation(program, "uErosionTex");
            FlipU = gl.GetUniformLocation(program, "uFlipU");
            FlipUMult = gl.GetUniformLocation(program, "uFlipUMult");
            FlipV = gl.GetUniformLocation(program, "uFlipV");
            FlipVMult = gl.GetUniformLocation(program, "uFlipVMult");
            Frame = gl.GetUniformLocation(program, "uFrame");
            Fresnel = gl.GetUniformLocation(program, "uFresnel");
            HasColor = gl.GetUniformLocation(program, "uHasColor");
            HasErosion = gl.GetUniformLocation(program, "uHasErosion");
            HasErosionMap = gl.GetUniformLocation(program, "uHasErosionMap");
            HasPalette = gl.GetUniformLocation(program, "uHasPalette");
            HasReflection = gl.GetUniformLocation(program, "uHasReflection");
            HasSoftParticle = gl.GetUniformLocation(program, "uHasSoftParticle");
            HasTex = gl.GetUniformLocation(program, "uHasTex");
            HasTexMult = gl.GetUniformLocation(program, "uHasTexMult");
            IsDistortion = gl.GetUniformLocation(program, "uIsDistortion");
            IsGroundLayer = gl.GetUniformLocation(program, "uIsGroundLayer");
            LegacyOrientation = gl.GetUniformLocation(program, "uLegacyOrientation");
            MaterialAddressU = gl.GetUniformLocation(program, "uMaterialAddressU");
            MaterialAddressV = gl.GetUniformLocation(program, "uMaterialAddressV");
            MaterialPremultiplied = gl.GetUniformLocation(program, "uMaterialPremultiplied");
            MaterialRepeat = gl.GetUniformLocation(program, "uMaterialRepeat");
            MaterialTint = gl.GetUniformLocation(program, "uMaterialTint");
            MeshSkinned = gl.GetUniformLocation(program, "uMeshSkinned");
            OrbitRotation = gl.GetUniformLocation(program, "uOrbitRotation");
            OwnerWorld = gl.GetUniformLocation(program, "uOwnerWorld");
            PaletteAddressMode = gl.GetUniformLocation(program, "uPaletteAddressMode");
            PaletteCount = gl.GetUniformLocation(program, "uPaletteCount");
            PaletteMap = gl.GetUniformLocation(program, "uPaletteMap");
            PaletteMixMask = gl.GetUniformLocation(program, "uPaletteMixMask");
            PaletteScroll = gl.GetUniformLocation(program, "uPaletteScroll");
            PaletteSelector = gl.GetUniformLocation(program, "uPaletteSelector");
            PivotUp = gl.GetUniformLocation(program, "uPivotUp");
            PlacementForward = gl.GetUniformLocation(program, "uPlacementForward");
            PlacementRight = gl.GetUniformLocation(program, "uPlacementRight");
            PlacementUp = gl.GetUniformLocation(program, "uPlacementUp");
            PrimitiveKind = gl.GetUniformLocation(program, "uPrimitiveKind");
            RampAtMult = gl.GetUniformLocation(program, "uRampAtMult");
            Reflection = gl.GetUniformLocation(program, "uReflection");
            ReflectionColor = gl.GetUniformLocation(program, "uReflectionColor");
            ReflectionTex = gl.GetUniformLocation(program, "uReflectionTex");
            Rotation = gl.GetUniformLocation(program, "uRotation");
            Scale = gl.GetUniformLocation(program, "uScale");
            SceneDepthTex = gl.GetUniformLocation(program, "uSceneDepthTex");
            SceneTex = gl.GetUniformLocation(program, "uSceneTex");
            SoftParticleControl = gl.GetUniformLocation(program, "uSoftParticleControl");
            SoftParticleParams = gl.GetUniformLocation(program, "uSoftParticleParams");
            Tex = gl.GetUniformLocation(program, "uTex");
            TexDiv = gl.GetUniformLocation(program, "uTexDiv");
            TexDivMult = gl.GetUniformLocation(program, "uTexDivMult");
            TexMult = gl.GetUniformLocation(program, "uTexMult");
            TexSize = gl.GetUniformLocation(program, "uTexSize");
            TexSizeMult = gl.GetUniformLocation(program, "uTexSizeMult");
            UseCustomMaterial = gl.GetUniformLocation(program, "uUseCustomMaterial");
            UseSkinning = gl.GetUniformLocation(program, "uUseSkinning");
            UvMode = gl.GetUniformLocation(program, "uUvMode");
            UvOffsetMult = gl.GetUniformLocation(program, "uUvOffsetMult");
            UvRotation = gl.GetUniformLocation(program, "uUvRotation");
            UvRotationMult = gl.GetUniformLocation(program, "uUvRotationMult");
            UvScale = gl.GetUniformLocation(program, "uUvScale");
            UvScaleMult = gl.GetUniformLocation(program, "uUvScaleMult");
            UvScrollRateMult = gl.GetUniformLocation(program, "uUvScrollRateMult");
            UvTransformCenter = gl.GetUniformLocation(program, "uUvTransformCenter");
            UvTransformCenterMult = gl.GetUniformLocation(program, "uUvTransformCenterMult");
            ViewProj = gl.GetUniformLocation(program, "uViewProj");
            ViewportSize = gl.GetUniformLocation(program, "uViewportSize");
            InverseViewProj = gl.GetUniformLocation(program, "uInverseViewProj");
            TerrainMode = gl.GetUniformLocation(program, "uTerrainMode");
            TerrainDepth = gl.GetUniformLocation(program, "uTerrainDepth");
            ProjectionBand = gl.GetUniformLocation(program, "uProjectionBand");
            WireframeColor = gl.GetUniformLocation(program, "uWireframeColor");
            WireframePass = gl.GetUniformLocation(program, "uWireframePass");
            WorldPos = gl.GetUniformLocation(program, "uWorldPos");
            GamePremultiplied = gl.GetUniformLocation(program, "uGamePremultiplied");
            GameLookupDrivers = gl.GetUniformLocation(program, "uGameLookupDrivers");
        }
    }
}
