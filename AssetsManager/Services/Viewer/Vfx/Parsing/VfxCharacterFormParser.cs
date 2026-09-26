using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using static AssetsManager.Services.Viewer.Vfx.Parsing.VfxValueParser;

namespace AssetsManager.Services.Viewer.Vfx.Parsing
{
    internal sealed record VfxCharacterFormDocumentData(
        IReadOnlyList<uint?> PrimaryGearUpgradePathHashes,
        IReadOnlyDictionary<uint, VfxCharacterFormDataProjection> GearForms);

    internal sealed record VfxCharacterFormDataProjection(
        IReadOnlyList<uint> ShowSubmeshHashes,
        IReadOnlyList<uint> HideSubmeshHashes,
        string MeshPath,
        string SkeletonPath,
        string EquipAnimation,
        IReadOnlyDictionary<uint, uint> ResourceMap,
        IReadOnlyList<VfxIdleEffectDefinition> OverrideIdleEffects,
        bool EnableOverrideIdleEffects,
        bool HasMaterialOverrides);

    /// <summary>Projects authored primary gear links and compact GearData payloads without retaining BIN trees.</summary>
    internal static class VfxCharacterFormParser
    {
        private const uint GearSkinUpgradeClass = 0x27dd6361;
        private const uint GearDataClass = 0xb43441ae;
        private static readonly uint ResourceResolverClass = VfxParsingHash.Fnv1a("ResourceResolver");

        private static readonly uint F_skinUpgradeData = VfxParsingHash.Fnv1a("skinUpgradeData");
        private const uint F_gearSkinUpgrades = 0xcb522723;
        private const uint F_gearData = 0x639b0013;
        private const uint F_showSubmeshes = 0x21b6167e;
        private const uint F_hideSubmeshes = 0xb6c044fb;
        private static readonly uint F_skinMeshProperties = VfxParsingHash.Fnv1a("skinMeshProperties");
        private static readonly uint F_simpleSkin = VfxParsingHash.Fnv1a("simpleSkin");
        private static readonly uint F_skeleton = VfxParsingHash.Fnv1a("skeleton");
        private static readonly uint F_equipAnimation = VfxParsingHash.Fnv1a("mEquipAnimation");
        private static readonly uint F_vfxResourceResolver = VfxParsingHash.Fnv1a("mVFXResourceResolver");
        private static readonly uint F_overrideIdleEffects = VfxParsingHash.Fnv1a("OverrideIdleEffects");
        private static readonly uint F_enableOverrideIdleEffects = VfxParsingHash.Fnv1a("EnableOverrideIdleEffects");

        private static readonly IReadOnlyDictionary<uint, uint> EmptyResourceMap = new Dictionary<uint, uint>();

        internal static VfxCharacterFormDocumentData ParseDocument(BinTree tree)
        {
            ArgumentNullException.ThrowIfNull(tree);

            IReadOnlyList<uint?> primaryLinks = Array.Empty<uint?>();
            foreach (BinTreeObject skin in tree.Objects.Values)
            {
                if (skin.ClassHash != VfxParsingSchema.SkinCharacterDataPropertiesClass ||
                    Get(skin.Properties, F_skinUpgradeData) is not BinTreeEmbedded upgradeData ||
                    upgradeData.ClassHash != VfxParsingHash.Fnv1a("SkinUpgradeData"))
                    continue;

                primaryLinks = ReadLinks(Get(upgradeData.Properties, F_gearSkinUpgrades));
                break;
            }

            var gearForms = new Dictionary<uint, VfxCharacterFormDataProjection>();
            foreach (BinTreeObject gearUpgrade in tree.Objects.Values)
            {
                if (gearUpgrade.ClassHash != GearSkinUpgradeClass ||
                    Get(gearUpgrade.Properties, F_gearData) is not BinTreeStruct gearData ||
                    gearData.ClassHash != GearDataClass)
                    continue;

                gearForms.TryAdd(gearUpgrade.PathHash, ParseGearData(gearData.Properties));
            }

            return new VfxCharacterFormDocumentData(primaryLinks, gearForms);
        }

        internal static IReadOnlyList<VfxCharacterFormDefinition> Resolve(
            IReadOnlyList<VfxCharacterFormDocumentData> documents,
            Func<uint, string> binEntryResolver)
        {
            if (documents == null || documents.Count == 0 || documents[0] == null)
                return Array.Empty<VfxCharacterFormDefinition>();

            var gearForms = new Dictionary<uint, VfxCharacterFormDataProjection>();
            foreach (VfxCharacterFormDocumentData document in documents)
            {
                if (document?.GearForms == null) continue;
                foreach (var pair in document.GearForms)
                    gearForms.TryAdd(pair.Key, pair.Value);
            }

            IReadOnlyList<uint?> links = documents[0].PrimaryGearUpgradePathHashes;
            if (links == null || links.Count == 0)
                return Array.Empty<VfxCharacterFormDefinition>();

            var forms = new List<VfxCharacterFormDefinition>(links.Count);
            for (int index = 0; index < links.Count; index++)
            {
                if (links[index] is not > 0 ||
                    !gearForms.TryGetValue(links[index].Value, out VfxCharacterFormDataProjection data))
                    continue;

                forms.Add(new VfxCharacterFormDefinition(
                    links[index].Value,
                    index,
                    ResolveName(links[index].Value, index, binEntryResolver),
                    data.ShowSubmeshHashes,
                    data.HideSubmeshHashes,
                    data.MeshPath,
                    data.SkeletonPath,
                    data.EquipAnimation,
                    data.ResourceMap,
                    data.OverrideIdleEffects,
                    data.EnableOverrideIdleEffects,
                    data.HasMaterialOverrides));
            }
            return forms.ToArray();
        }

        private static VfxCharacterFormDataProjection ParseGearData(
            IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            BinTreeEmbedded skinMesh = Get(properties, F_skinMeshProperties) as BinTreeEmbedded;
            BinTreeStruct resolver = Get(properties, F_vfxResourceResolver) as BinTreeStruct;
            IReadOnlyDictionary<uint, uint> resourceMap =
                resolver?.ClassHash == ResourceResolverClass
                    ? VfxResourceParser.ExtractResourceMap(resolver.Properties)
                    : EmptyResourceMap;

            bool hasMaterialOverrides = skinMesh != null &&
                (skinMesh.Properties.ContainsKey(VfxParsingHash.Fnv1a("texture")) ||
                 skinMesh.Properties.ContainsKey(VfxParsingHash.Fnv1a("Material")) ||
                 skinMesh.Properties.ContainsKey(VfxParsingHash.Fnv1a("materialOverride")));

            return new VfxCharacterFormDataProjection(
                ReadHashList(Get(properties, F_showSubmeshes)),
                ReadHashList(Get(properties, F_hideSubmeshes)),
                skinMesh == null ? null : ReadAsset(skinMesh.Properties, F_simpleSkin, ".skn"),
                skinMesh == null ? null : ReadAsset(skinMesh.Properties, F_skeleton, ".skl"),
                ReadAsset(Get(properties, F_equipAnimation), ".anm"),
                resourceMap,
                VfxAnimationParser.ExtractIdleEffects(Get(properties, F_overrideIdleEffects)),
                GetBool(properties, F_enableOverrideIdleEffects),
                hasMaterialOverrides);
        }

        private static IReadOnlyList<uint> ReadHashList(BinTreeProperty property)
        {
            if (property is BinTreeOptional optional) property = optional.Value;
            if (property is not BinTreeContainer container) return Array.Empty<uint>();
            return container.Elements.Select(AsU32)
                .Where(static value => value is > 0)
                .Select(static value => value.Value)
                .Distinct()
                .ToArray();
        }

        private static IReadOnlyList<uint?> ReadLinks(BinTreeProperty property)
        {
            if (property is BinTreeOptional optional) property = optional.Value;
            if (property is not BinTreeContainer container) return Array.Empty<uint?>();
            return container.Elements
                .Select(static value => value is BinTreeObjectLink link && link.Value != 0
                    ? (uint?)link.Value
                    : null)
                .ToArray();
        }

        private static string ResolveName(uint pathHash, int index, Func<uint, string> binEntryResolver)
        {
            string resolvedPath = binEntryResolver?.Invoke(pathHash);
            if (!string.IsNullOrWhiteSpace(resolvedPath))
            {
                string name = Path.GetFileNameWithoutExtension(resolvedPath.Replace('\\', '/'));
                bool hashOnly = name.Length is 8 or 16 && name.All(Uri.IsHexDigit);
                if (!string.IsNullOrWhiteSpace(name) && !hashOnly) return name;
            }
            return $"Form {index + 1}";
        }
    }
}