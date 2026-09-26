using System;
using System.Collections.Generic;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Rendering.GameShaders
{
    /// <summary>
    /// Selects the stock HLSL pair and compile-time flags used by a default VFX emitter.
    /// Custom material passes continue through their authored program path.
    /// </summary>
    internal static class GameParticleProgramResolver
    {
        internal sealed record ShaderPair(string Name, string VertexPath, string PixelPath);

        internal const string QuadVertexPath = "ASSETS/Shaders/HLSL/ParticleSystem/QUAD_VS.vs";
        internal const string QuadPixelPath = "ASSETS/Shaders/HLSL/ParticleSystem/QUAD_PS.ps";
        internal const string FixedAlphaQuadVertexPath = "ASSETS/Shaders/HLSL/ParticleSystem/QUAD_VS_FixedAlphaUV.vs";
        internal const string FixedAlphaQuadPixelPath = "ASSETS/Shaders/HLSL/ParticleSystem/QUAD_PS_FixedAlphaUV.ps";
        internal const string ScreenSpaceQuadVertexPath = "ASSETS/Shaders/HLSL/ParticleSystem/QUAD_ScreenSpaceUV.vs";
        internal const string ScreenSpaceQuadPixelPath = "ASSETS/Shaders/HLSL/ParticleSystem/QUAD_ScreenSpaceUV.ps";
        internal const string MeshVertexPath = "ASSETS/Shaders/HLSL/ParticleSystem/MESH_VS.vs";
        internal const string MeshPixelPath = "ASSETS/Shaders/HLSL/ParticleSystem/MESH_PS.ps";
        internal const string AttachedMeshVertexPath = "ASSETS/Shaders/HLSL/SkinnedMesh/PARTICLE_VS.vs";
        internal const string AttachedMeshPixelPath = "ASSETS/Shaders/HLSL/SkinnedMesh/PARTICLE_PS.ps";
        internal const string DistortionVertexPath = "ASSETS/Shaders/HLSL/ParticleSystem/DISTORTION_VS.vs";
        internal const string DistortionPixelPath = "ASSETS/Shaders/HLSL/ParticleSystem/DISTORTION_PS.ps";
        internal const string DistortionMeshVertexPath = "ASSETS/Shaders/HLSL/ParticleSystem/DISTORTION_MESH_VS.vs";
        internal const string DistortionMeshPixelPath = "ASSETS/Shaders/HLSL/ParticleSystem/DISTORTION_MESH_PS.ps";
        internal const string DistortionAttachedVertexPath = "ASSETS/Shaders/HLSL/SkinnedMesh/PARTICLE_DISTORTION_VS.vs";
        internal const string DistortionAttachedPixelPath = "ASSETS/Shaders/HLSL/SkinnedMesh/PARTICLE_DISTORTION_PS.ps";

        internal static GameMaterialProgram Create(VfxEmitterDefinition emitter, bool meshGeometry)
        {
            ArgumentNullException.ThrowIfNull(emitter);
            if (!CanCreateNativeProgram(emitter, meshGeometry))
                return null;

            ShaderPair pair = ResolvePair(emitter, meshGeometry);
            var pass = new GameMaterialPass(
                ShaderHash: 0,
                ShaderPath: pair.Name,
                Defines: BuildEmitterDefines(emitter, meshGeometry),
                RuntimeSwitches: Array.Empty<KeyValuePair<string, bool>>(),
                Textures: Array.Empty<GameMaterialTexture>(),
                Parameters: Array.Empty<GameMaterialParameter>(),
                State: GameMaterialPassState.Default)
            {
                VertexShaderPath = pair.VertexPath,
                PixelShaderPath = pair.PixelPath
            };

            return new GameMaterialProgram(
                GameMaterialKind.Particles,
                Animated: false,
                Passes: new[] { pass });
        }

        internal static bool CanCreateNativeProgram(VfxEmitterDefinition emitter, bool meshGeometry) =>
            emitter.Distortion is not null ||
            meshGeometry ||
            (emitter.Reflection is null && emitter.UvMode is not (1 or 2));

        internal static ShaderPair ResolvePair(VfxEmitterDefinition emitter, bool meshGeometry)
        {
            bool attachedMesh = meshGeometry &&
                                emitter.PrimitiveKind == VfxPrimitiveKind.AttachedMesh;
            if (emitter.Distortion is not null)
            {
                if (attachedMesh)
                {
                    return new ShaderPair(
                        "SkinnedMesh/PARTICLE_DISTORTION",
                        DistortionAttachedVertexPath,
                        DistortionAttachedPixelPath);
                }

                return meshGeometry
                    ? new ShaderPair(
                        "ParticleSystem/DISTORTION_MESH",
                        DistortionMeshVertexPath,
                        DistortionMeshPixelPath)
                    : new ShaderPair(
                        "ParticleSystem/DISTORTION",
                        DistortionVertexPath,
                        DistortionPixelPath);
            }

            if (meshGeometry)
            {
                return attachedMesh
                    ? new ShaderPair(
                        "SkinnedMesh/PARTICLE",
                        AttachedMeshVertexPath,
                        AttachedMeshPixelPath)
                    : new ShaderPair("ParticleSystem/MESH", MeshVertexPath, MeshPixelPath);
            }

            if (emitter.Reflection is not null)
                return new ShaderPair("ParticleSystem/MESH", MeshVertexPath, MeshPixelPath);

            return emitter.UvMode switch
            {
                1 => new ShaderPair(
                    "ParticleSystem/QUAD_ScreenSpaceUV",
                    ScreenSpaceQuadVertexPath,
                    ScreenSpaceQuadPixelPath),
                2 => new ShaderPair(
                    "ParticleSystem/QUAD_FixedAlphaUV",
                    FixedAlphaQuadVertexPath,
                    FixedAlphaQuadPixelPath),
                _ => new ShaderPair("ParticleSystem/QUAD", QuadVertexPath, QuadPixelPath)
            };
        }

        internal static IReadOnlyList<GameMaterialDefine> BuildEmitterDefines(
            VfxEmitterDefinition emitter,
            bool meshGeometry)
        {
            var defines = new List<GameMaterialDefine>();
            void Add(string name) =>
                defines.Add(new GameMaterialDefine(name, "1", GameMaterialDefineSource.Pass));

            byte alphaReference = emitter.RenderState?.AlphaReference ??
                                  VfxEmitterRenderState.Default.AlphaReference;
            if (alphaReference != 0)
                Add("ALPHA_TEST");
            if (emitter.AlphaErosion is not null)
                Add("ALPHA_EROSION");
            if (!string.IsNullOrWhiteSpace(emitter.TextureMultPath) ||
                emitter.AuthoredFeatures?.HasTextureMultLayer == true)
            {
                Add("MULT_PASS");
            }

            if (meshGeometry)
            {
                switch (emitter.UvMode)
                {
                    case 1:
                        Add("SCREEN_SPACE_UV");
                        break;
                    case 2:
                        Add("SEPARATE_ALPHA_UV");
                        break;
                    case 3:
                    case 4:
                    case 5:
                        Add("LOCAL_SPACE_UV");
                        break;
                }
                Add("USE_VERTEX_COLORS");
            }

            if (emitter.PaletteDefinition is not null)
                Add("PALETTIZE_TEXTURES");
            if (emitter.SoftParticle is not null)
                Add("SOFT_PARTICLES");
            if (emitter.Reflection is not null)
                Add("REFLECTIVE");

            return defines;
        }
    }
}
