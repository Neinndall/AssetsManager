using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Viewer.Parsing
{
    /// <summary>
    /// Reads the map visibility contract: IMapVisibilityController objects of a container materials BIN
    /// and the MapVisibilityFlagDefinitions domains of the owning Map object.
    /// </summary>
    internal sealed class MapVisibilityParser
    {
        internal static readonly uint MutatorControllerClass = Fnv1a.HashLower("MutatorMapVisibilityController");
        internal static readonly uint ChildControllerClass = Fnv1a.HashLower("ChildMapVisibilityController");
        internal static readonly uint NamedControllerClass = 0xe07edfa4;
        internal static readonly uint VisFlagsControllerClass = 0x6b863734;
        internal static readonly uint LogicDriverControllerClass = Fnv1a.HashLower("LogicDriverVisibilityController");
        internal const uint ProviderControllerClass = 0xf9cfefd4;

        // Unnamed Riot classes whose u8 field is a bit set of the primary (0xc406a533) or
        // secondary (0xec733fe2) domain; both share the name/DefaultVisible layout of 0xe07edfa4.
        internal const uint PrimaryFlagsControllerClass = 0xc406a533;
        internal const uint SecondaryFlagsControllerClass = 0xec733fe2;
        internal const uint PrimaryFlagsField = 0x27639032;
        internal const uint SecondaryFlagsField = 0x8bff8cdf;

        // Secondary MapVisibilityFlagDefinitions of the Map object and its initial mask.
        internal const uint SecondaryDefinitionsField = 0xd31ac6ce;
        internal const uint SecondaryInitialMaskField = 0x30eafcaa;

        private static readonly uint MutatorNameField = Fnv1a.HashLower("MutatorName");
        private static readonly uint ParentsField = Fnv1a.HashLower("Parents");
        private static readonly uint ParentModeField = Fnv1a.HashLower("ParentMode");
        private static readonly uint DefaultVisibleField = Fnv1a.HashLower("DefaultVisible");
        private static readonly uint VisFlagsField = Fnv1a.HashLower("VisFlags");
        private static readonly uint InitialVisibilityMaskField = Fnv1a.HashLower("InitialVisibilityMask");
        private static readonly uint VisibilityFlagDefinesField = Fnv1a.HashLower("VisibilityFlagDefines");
        private static readonly uint FlagDefinitionsField = Fnv1a.HashLower("FlagDefinitions");
        private static readonly uint FlagRangeField = Fnv1a.HashLower("FlagRange");
        private static readonly uint MinIndexField = Fnv1a.HashLower("minIndex");
        private static readonly uint MaxIndexField = Fnv1a.HashLower("maxIndex");
        private static readonly uint NameField = Fnv1a.HashLower("name");
        private static readonly uint PublicNameField = Fnv1a.HashLower("PublicName");
        private static readonly uint BitIndexField = Fnv1a.HashLower("BitIndex");

        public IReadOnlyDictionary<uint, MapVisibilityControllerData> ParseControllers(BinTree materials,
            Func<uint, string> resolveName = null, Func<uint, string> resolveEntry = null)
        {
            var result = new Dictionary<uint, MapVisibilityControllerData>();
            if (materials?.Objects == null)
                return result;

            foreach (BinTreeObject entry in materials.Objects.Values)
            {
                MapVisibilityControllerData controller = ReadController(entry);
                if (controller != null)
                    result[entry.PathHash] = controller with
                    {
                        Name = ReadHashName(entry.Properties, NameField, resolveName) ?? ResolvedName(entry.PathHash, resolveEntry)
                    };
            }
            return result;
        }

        public MapVisibilityDefinitions ParseDefinitions(BinTree mapDocument, Func<uint, string> resolveHash = null)
        {
            BinTreeObject map = mapDocument?.Objects?.Values.FirstOrDefault(entry =>
                entry.ClassHash == MapVariantParser.MapClass);
            if (map == null)
                return MapVisibilityDefinitions.Empty;

            MapVisibilityDomainData primary = ReadDomain(
                map.Properties, VisibilityFlagDefinesField, InitialVisibilityMaskField, resolveHash);
            MapVisibilityDomainData secondary = ReadDomain(
                map.Properties, SecondaryDefinitionsField, SecondaryInitialMaskField, resolveHash);
            return new MapVisibilityDefinitions(primary, secondary);
        }

        private static MapVisibilityControllerData ReadController(BinTreeObject entry)
        {
            IReadOnlyDictionary<uint, BinTreeProperty> properties = entry.Properties;
            uint type = entry.ClassHash;
            if (type == MutatorControllerClass)
            {
                return new MapVisibilityControllerData(
                    entry.PathHash,
                    MapVisibilityControllerKind.Mutator,
                    MutatorName: ReadString(properties, MutatorNameField));
            }
            if (type == ChildControllerClass)
            {
                ulong mode = ReadUnsigned(properties, ParentModeField) ?? 0;
                return new MapVisibilityControllerData(
                    entry.PathHash,
                    mode <= 3 ? MapVisibilityControllerKind.Child : MapVisibilityControllerKind.Driven,
                    Parents: ReadLinks(properties, ParentsField),
                    ParentMode: (MapVisibilityParentMode)mode);
            }
            if (type == PrimaryFlagsControllerClass)
            {
                return new MapVisibilityControllerData(
                    entry.PathHash,
                    MapVisibilityControllerKind.Terrain,
                    Mask: ReadByte(properties, PrimaryFlagsField),
                    DefaultVisible: ReadBool(properties, DefaultVisibleField, fallback: true),
                    StageMask: ReadByte(properties, SecondaryFlagsField));
            }
            if (type == VisFlagsControllerClass)
            {
                return new MapVisibilityControllerData(
                    entry.PathHash,
                    MapVisibilityControllerKind.PrimaryFlags,
                    Mask: ReadByte(properties, VisFlagsField));
            }
            if (type == SecondaryFlagsControllerClass)
            {
                return new MapVisibilityControllerData(
                    entry.PathHash,
                    MapVisibilityControllerKind.SecondaryFlags,
                    Mask: ReadByte(properties, SecondaryFlagsField),
                    DefaultVisible: ReadBool(properties, DefaultVisibleField, fallback: true),
                    TerrainMask: ReadByte(properties, PrimaryFlagsField));
            }
            if (type == NamedControllerClass)
            {
                return new MapVisibilityControllerData(
                    entry.PathHash,
                    MapVisibilityControllerKind.Named,
                    DefaultVisible: ReadBool(properties, DefaultVisibleField, fallback: true),
                    TerrainMask: ReadByte(properties, PrimaryFlagsField),
                    StageMask: ReadByte(properties, SecondaryFlagsField));
            }
            if (type == LogicDriverControllerClass || type == ProviderControllerClass)
                return new MapVisibilityControllerData(entry.PathHash, MapVisibilityControllerKind.Driven);
            return null;
        }

        private static MapVisibilityDomainData ReadDomain(
            IReadOnlyDictionary<uint, BinTreeProperty> map,
            uint definitionsField,
            uint initialField,
            Func<uint, string> resolveHash)
        {
            if (!map.TryGetValue(definitionsField, out BinTreeProperty property) ||
                property is not BinTreeStruct definitions)
            {
                return null;
            }

            var flags = new List<MapVisibilityFlagData>();
            if (definitions.Properties.TryGetValue(FlagDefinitionsField, out BinTreeProperty listProperty) &&
                listProperty is BinTreeContainer list)
            {
                foreach (BinTreeProperty item in list.Elements)
                {
                    if (item is not BinTreeStruct definition)
                        continue;
                    int bit = ReadByte(definition.Properties, BitIndexField);
                    if (bit is < 0 or > 7 || flags.Any(flag => flag.BitIndex == bit))
                        continue;
                    flags.Add(new MapVisibilityFlagData(
                        bit,
                        ReadHashName(definition.Properties, NameField, resolveHash),
                        ReadString(definition.Properties, PublicNameField)));
                }
            }

            int min = 0;
            int max = 7;
            if (definitions.Properties.TryGetValue(FlagRangeField, out BinTreeProperty rangeProperty) &&
                rangeProperty is BinTreeStruct range)
            {
                min = ReadByte(range.Properties, MinIndexField);
                max = ReadByte(range.Properties, MaxIndexField);
            }

            int initial = map.TryGetValue(initialField, out BinTreeProperty initialProperty) &&
                          initialProperty is BinTreeU8 initialMask
                ? initialMask.Value
                : 1;
            return new MapVisibilityDomainData(
                flags.OrderBy(flag => flag.BitIndex).ToArray(),
                initial == 0 ? 1 : initial,
                min,
                max);
        }

        private static IReadOnlyList<uint> ReadLinks(IReadOnlyDictionary<uint, BinTreeProperty> properties, uint field)
        {
            if (!properties.TryGetValue(field, out BinTreeProperty property) ||
                property is not BinTreeContainer container)
            {
                return Array.Empty<uint>();
            }

            return container.Elements
                .OfType<BinTreeObjectLink>()
                .Select(link => link.Value)
                .ToArray();
        }

        private static string ReadString(IReadOnlyDictionary<uint, BinTreeProperty> properties, uint field) =>
            properties.TryGetValue(field, out BinTreeProperty property) && property is BinTreeString text
                ? text.Value
                : null;

        private static string ReadHashName(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            uint field,
            Func<uint, string> resolveHash)
        {
            if (!properties.TryGetValue(field, out BinTreeProperty property) || property is not BinTreeHash hash)
                return null;
            return ResolvedName(hash.Value, resolveHash);
        }

        private static string ResolvedName(uint hash, Func<uint, string> resolveHash)
        {
            string resolved = resolveHash?.Invoke(hash);
            return string.IsNullOrWhiteSpace(resolved) ||
                   resolved.Equals(hash.ToString("x8"), StringComparison.OrdinalIgnoreCase) ||
                   resolved.Equals($"0x{hash:x8}", StringComparison.OrdinalIgnoreCase)
                ? null
                : resolved;
        }

        private static int ReadByte(IReadOnlyDictionary<uint, BinTreeProperty> properties, uint field)
            => ReadUnsigned(properties, field) is ulong value && value <= byte.MaxValue ? (int)value : 0;

        private static ulong? ReadUnsigned(IReadOnlyDictionary<uint, BinTreeProperty> properties, uint field)
            => properties.TryGetValue(field, out BinTreeProperty property) ? property switch
            {
                BinTreeU8 value => value.Value,
                BinTreeU16 value => value.Value,
                BinTreeU32 value => value.Value,
                BinTreeU64 value => value.Value,
                _ => null
            } : null;

        private static bool ReadBool(IReadOnlyDictionary<uint, BinTreeProperty> properties, uint field, bool fallback) =>
            properties.TryGetValue(field, out BinTreeProperty property)
                ? property switch
                {
                    BinTreeBool value => value.Value,
                    BinTreeBitBool value => value.Value,
                    _ => fallback
                }
                : fallback;
    }
}
