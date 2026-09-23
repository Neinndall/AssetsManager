using System.Collections.Generic;
using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    internal enum GameMaterialKind
    {
        StaticMesh,
        SkinnedMesh,
        Particles,
        Ui,
        PostProcess,
        Unknown
    }

    internal enum GameMaterialDefineSource
    {
        Material,
        Feature,
        Switch,
        Pass
    }

    internal sealed record GameMaterialDefineData(
        string Name,
        string Value,
        GameMaterialDefineSource Source);

    internal enum GameMaterialTextureSource
    {
        Material,
        ShaderDefault,
        Fallback
    }

    internal sealed record GameMaterialSamplerStateData(
        string SharedSampler,
        MapTextureWrap WrapU,
        MapTextureWrap WrapV,
        MapTextureWrap WrapW,
        bool FilterMin,
        bool FilterMag);

    internal sealed record GameMaterialPassTextureData(
        string Name,
        MapTextureReference Texture,
        GameMaterialTextureSource Source,
        GameMaterialSamplerStateData Sampler);

    internal enum GameMaterialParamSource
    {
        ShaderDefault,
        Material,
        Pass
    }

    internal sealed record GameMaterialPassParamData(
        string Name,
        Vector4 Value,
        GameMaterialParamSource Source);

    internal enum GameMaterialWinding
    {
        Clockwise,
        CounterClockwise
    }

    internal sealed record GameMaterialPassStateData(
        bool BlendEnabled,
        MapBlendFactor SourceColor,
        MapBlendFactor DestinationColor,
        MapBlendFactor SourceAlpha,
        MapBlendFactor DestinationAlpha,
        bool CullEnabled,
        GameMaterialWinding WindingToCull,
        bool DepthEnabled,
        uint DepthCompareFunc,
        uint WriteMask);

    internal sealed record GameResolvedMaterialPassData(
        uint ShaderHash,
        string ShaderPath,
        IReadOnlyList<GameMaterialDefineData> Defines,
        IReadOnlyList<KeyValuePair<string, bool>> RuntimeSwitches,
        IReadOnlyList<GameMaterialPassTextureData> Textures,
        IReadOnlyList<GameMaterialPassParamData> Parameters,
        GameMaterialPassStateData State);

    internal sealed record GameResolvedMaterialProgramData(
        GameMaterialKind Kind,
        bool Animated,
        IReadOnlyList<GameResolvedMaterialPassData> Passes);
}
