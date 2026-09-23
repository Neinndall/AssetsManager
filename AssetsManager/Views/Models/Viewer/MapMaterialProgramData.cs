using System.Collections.Generic;
using System.Numerics;

namespace AssetsManager.Views.Models.Viewer
{
    internal enum MapMaterialKind
    {
        StaticMesh,
        SkinnedMesh,
        Particles,
        Ui,
        PostProcess,
        Unknown
    }

    internal enum MapMaterialDefineSource
    {
        Material,
        Feature,
        Switch,
        Pass
    }

    internal sealed record MapMaterialDefineData(
        string Name,
        string Value,
        MapMaterialDefineSource Source);

    internal enum MapMaterialTextureSource
    {
        Material,
        ShaderDefault,
        Fallback
    }

    internal sealed record MapMaterialSamplerStateData(
        string SharedSampler,
        MapTextureWrap WrapU,
        MapTextureWrap WrapV,
        MapTextureWrap WrapW,
        bool FilterMin,
        bool FilterMag);

    internal sealed record MapMaterialPassTextureData(
        string Name,
        MapTextureReference Texture,
        MapMaterialTextureSource Source,
        MapMaterialSamplerStateData Sampler);

    internal enum MapMaterialParamSource
    {
        ShaderDefault,
        Material,
        Pass
    }

    internal sealed record MapMaterialPassParamData(
        string Name,
        Vector4 Value,
        MapMaterialParamSource Source);

    internal enum MapMaterialWinding
    {
        Clockwise,
        CounterClockwise
    }

    internal sealed record MapMaterialPassStateData(
        bool BlendEnabled,
        MapBlendFactor SourceColor,
        MapBlendFactor DestinationColor,
        MapBlendFactor SourceAlpha,
        MapBlendFactor DestinationAlpha,
        bool CullEnabled,
        MapMaterialWinding WindingToCull,
        bool DepthEnabled,
        uint DepthCompareFunc,
        uint WriteMask);

    internal sealed record MapResolvedMaterialPassData(
        uint ShaderHash,
        string ShaderPath,
        IReadOnlyList<MapMaterialDefineData> Defines,
        IReadOnlyList<KeyValuePair<string, bool>> RuntimeSwitches,
        IReadOnlyList<MapMaterialPassTextureData> Textures,
        IReadOnlyList<MapMaterialPassParamData> Parameters,
        MapMaterialPassStateData State);

    internal sealed record MapResolvedMaterialProgramData(
        MapMaterialKind Kind,
        bool Animated,
        IReadOnlyList<MapResolvedMaterialPassData> Passes);
}
