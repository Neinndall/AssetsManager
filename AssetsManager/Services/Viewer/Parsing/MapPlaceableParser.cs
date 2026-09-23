using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace AssetsManager.Services.Viewer.Parsing
{
    /// <summary>
    /// Reads MapPlaceableContainer chunks and their common placeable contract like current LTK Manager MAIN.
    /// Class-specific interpretation belongs to the character/particle stages that consume this output.
    /// </summary>
    internal sealed class MapPlaceableParser
    {
        internal const uint PlaceableContainerClass = 0xb25c0a3f;
        internal const uint MapContainerClass = 0xdde8c114;

        internal const uint ItemsField = 0x3a79338f;
        internal const uint TransformField = 0xe1ad931b;
        internal const uint NameField = 0x8d39bde6;
        internal const uint VisibilityFlagsField = 0xccf79327;
        internal const uint VisibilityControllerField = 0x5150a6a1;
        internal const uint ChunksField = 0x5e0e1da3;

        private readonly HashResolverService _hashResolver;

        public MapPlaceableParser(HashResolverService hashResolver = null)
        {
            _hashResolver = hashResolver;
        }

        public IReadOnlyList<MapPlaceableChunkData> Parse(BinTree materials)
        {
            if (materials?.Objects == null || materials.Objects.Count == 0)
                return Array.Empty<MapPlaceableChunkData>();

            IReadOnlyDictionary<uint, uint> listedKeys = ReadListedChunkKeys(materials);
            var chunks = new List<MapPlaceableChunkData>();

            foreach (BinTreeObject entry in materials.Objects.Values)
            {
                if (entry.ClassHash != PlaceableContainerClass ||
                    !entry.Properties.TryGetValue(ItemsField, out BinTreeProperty itemsProperty) ||
                    itemsProperty is not BinTreeMap items)
                {
                    continue;
                }

                var placeables = new List<MapPlaceableData>(items.Count);
                foreach ((BinTreeProperty keyProperty, BinTreeProperty valueProperty) in items)
                {
                    if (keyProperty is not BinTreeHash key || valueProperty is not BinTreeStruct placed)
                        continue;

                    placeables.Add(new MapPlaceableData(
                        entry.PathHash,
                        key.Value,
                        placed.ClassHash,
                        ReadName(placed.Properties),
                        ReadTransform(placed.Properties),
                        ReadVisibility(placed.Properties),
                        ReadController(placed.Properties),
                        placed.Properties));
                }

                // LTK omits empty chunks from the outline because placeables() never yields them.
                if (placeables.Count == 0)
                    continue;

                chunks.Add(new MapPlaceableChunkData(
                    entry.PathHash,
                    ResolveChunkName(entry.PathHash, listedKeys),
                    placeables));
            }

            return chunks;
        }

        internal string ReadName(IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            if (properties == null || !properties.TryGetValue(NameField, out BinTreeProperty property))
                return string.Empty;

            if (property is BinTreeString text)
                return text.Value ?? string.Empty;
            if (property is BinTreeHash hash)
            {
                string resolved = _hashResolver?.ResolveBinHash(hash.Value);
                return IsFallbackHash(resolved, hash.Value) ? Hex(hash.Value) : resolved;
            }

            return string.Empty;
        }

        internal static Matrix4x4 ReadTransform(IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            if (properties != null &&
                properties.TryGetValue(TransformField, out BinTreeProperty property) &&
                property is BinTreeMatrix44 matrix)
            {
                // Riot stores the BIN matrix by rows. LTK's Rust side transposes that matrix and
                // exports it column-major; System.Numerics already exposes the same 16 values in
                // row-major field order, so another transpose here would move translation out of M41-M43.
                return matrix.Value;
            }

            return Matrix4x4.Identity;
        }

        internal static byte ReadVisibility(IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            if (properties != null &&
                properties.TryGetValue(VisibilityFlagsField, out BinTreeProperty property) &&
                property is BinTreeU8 visibility)
            {
                return visibility.Value;
            }

            return MapPlaceableData.EveryLayer;
        }

        internal static uint? ReadController(IReadOnlyDictionary<uint, BinTreeProperty> properties)
        {
            if (properties != null &&
                properties.TryGetValue(VisibilityControllerField, out BinTreeProperty property) &&
                property is BinTreeObjectLink controller)
            {
                return controller.Value;
            }

            return null;
        }

        internal static string Hex(uint value) => $"0x{value:x8}";

        private string ResolveChunkName(uint chunkHash, IReadOnlyDictionary<uint, uint> listedKeys)
        {
            string direct = _hashResolver?.ResolveBinEntry(chunkHash);
            if (!IsFallbackHash(direct, chunkHash))
                return direct;

            if (listedKeys.TryGetValue(chunkHash, out uint listedKey))
            {
                string listed = _hashResolver?.ResolveBinHash(listedKey);
                if (!IsFallbackHash(listed, listedKey))
                    return listed;
            }

            return null;
        }

        private static IReadOnlyDictionary<uint, uint> ReadListedChunkKeys(BinTree materials)
        {
            var result = new Dictionary<uint, uint>();
            foreach (BinTreeObject entry in materials.Objects.Values)
            {
                if (entry.ClassHash != MapContainerClass ||
                    !entry.Properties.TryGetValue(ChunksField, out BinTreeProperty chunksProperty) ||
                    chunksProperty is not BinTreeMap chunks)
                {
                    continue;
                }

                foreach ((BinTreeProperty keyProperty, BinTreeProperty valueProperty) in chunks)
                {
                    if (keyProperty is BinTreeHash key &&
                        valueProperty is BinTreeObjectLink chunk &&
                        !result.ContainsKey(chunk.Value))
                    {
                        result.Add(chunk.Value, key.Value);
                    }
                }
            }

            return result;
        }

        private static bool IsFallbackHash(string value, uint hash) =>
            string.IsNullOrWhiteSpace(value) ||
            value.Equals(hash.ToString("x8"), StringComparison.OrdinalIgnoreCase) ||
            value.Equals(Hex(hash), StringComparison.OrdinalIgnoreCase);
    }
}
