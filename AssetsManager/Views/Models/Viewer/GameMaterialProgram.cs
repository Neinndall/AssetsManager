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

    internal sealed record GameMaterialDefine(
        string Name,
        string Value,
        GameMaterialDefineSource Source);

    internal enum GameMaterialTextureSource
    {
        Material,
        ShaderDefault,
        Fallback
    }

    internal sealed record GameMaterialSamplerState(
        string SharedSampler,
        MapTextureWrap WrapU,
        MapTextureWrap WrapV,
        MapTextureWrap WrapW,
        bool FilterMin,
        bool FilterMag);

    internal sealed record GameMaterialTexture(
        string Name,
        MapTextureReference Texture,
        GameMaterialTextureSource Source,
        GameMaterialSamplerState Sampler);

    internal enum GameMaterialParamSource
    {
        ShaderDefault,
        Material,
        Pass
    }

    internal sealed record GameMaterialParameter(
        string Name,
        Vector4 Value,
        GameMaterialParamSource Source);

    internal enum GameMaterialWinding
    {
        Clockwise,
        CounterClockwise
    }

    internal sealed record GameMaterialPassState(
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

    internal sealed record GameMaterialPass(
        uint ShaderHash,
        string ShaderPath,
        IReadOnlyList<GameMaterialDefine> Defines,
        IReadOnlyList<KeyValuePair<string, bool>> RuntimeSwitches,
        IReadOnlyList<GameMaterialTexture> Textures,
        IReadOnlyList<GameMaterialParameter> Parameters,
        GameMaterialPassState State);

    internal sealed record GameMaterialProgram(
        GameMaterialKind Kind,
        bool Animated,
        IReadOnlyList<GameMaterialPass> Passes);
}
