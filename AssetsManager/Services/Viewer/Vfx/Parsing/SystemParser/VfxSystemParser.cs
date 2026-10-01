using System;
using System.Collections.Generic;
using System.Numerics;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using static AssetsManager.Services.Viewer.Vfx.Parsing.VfxParsingSchema;
using static AssetsManager.Services.Viewer.Vfx.Parsing.VfxValueParser;

namespace AssetsManager.Services.Viewer.Vfx.Parsing
{
    internal static partial class VfxSystemParser
    {
        internal static IReadOnlyDictionary<uint, VfxSystemDefinition> ExtractAll(BinTree bin)
        {
            var map = new Dictionary<uint, VfxSystemDefinition>();

            foreach (var o in bin.Objects.Values)
            {
                if (o.ClassHash != SystemClass) continue;
                var system = ParseSystem(o);
                if (system is not null) map[o.PathHash] = system;
            }
            return map;
        }

        internal static VfxSystemDefinition Extract(BinTree bin, uint pathHash)
        {
            if (bin?.Objects == null ||
                pathHash == 0 ||
                !bin.Objects.TryGetValue(pathHash, out BinTreeObject systemObject) ||
                systemObject.ClassHash != SystemClass)
            {
                return null;
            }

            return ParseSystem(systemObject);
        }

        private static VfxSystemDefinition ParseSystem(BinTreeObject o)
        {
            string name = GetString(o.Properties, F_particleName) ?? $"0x{o.PathHash:x8}";
            string path = GetString(o.Properties, F_particlePath) ?? "";

            var emitters = new List<VfxEmitterDefinition>();
            foreach (uint listHash in EmitterLists)
            {
                if (Get(o.Properties, listHash) is not BinTreeContainer c) continue;
                bool simple = listHash == EmitterLists[1];
                foreach (var el in c.Elements)
                    if (el is BinTreeStruct s && s.ClassHash == EmitterClass)
                        emitters.Add(ParseEmitter(s, simple));
            }
            if (Get(o.Properties, F_shimmerEmitterDefinitionData) is BinTreeContainer shimmerContainer)
            {
                for (int i = 0; i < shimmerContainer.Elements.Count; i++)
                {
                    if (shimmerContainer.Elements[i] is BinTreeStruct shimmerStruct)
                    {
                        var shimmerEmitter = ParseShimmerEmitter(shimmerStruct, i);
                        if (shimmerEmitter != null)
                            emitters.Add(shimmerEmitter);
                    }
                }
            }
            float radius = GetF32(o.Properties, F_visibilityRadius) ?? 0f;
            Matrix4x4? transform = Get(o.Properties, F_transform) is BinTreeMatrix44 matrix
                ? matrix.Value
                : null;
            int systemFlags = GetI32(o.Properties, F_systemFlags)
                ?? (int?)(AsU32(Get(o.Properties, F_systemFlags)))
                ?? DefaultSystemFlags;
            float buildUpTime = MathF.Max(0f, GetF32(o.Properties, F_buildUpTime) ?? 0f);
            return new VfxSystemDefinition(
                o.PathHash,
                name,
                path,
                emitters,
                radius,
                transform,
                new VfxSystemAuthoredFeatures(
                    HasMaterialOverrides: HasElements(o.Properties, F_materialOverrideDefinitions),
                    HasAssetRemapping: HasElements(o.Properties, F_assetRemappingTable)),
                (systemFlags & AnalyticDragMotionFlag) != 0 ? VfxDragMotion.Analytic : VfxDragMotion.Stepped,
                buildUpTime);
        }

    }
}
