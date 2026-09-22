using System;
using System.Collections.Generic;
using System.Linq;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Viewer;

namespace AssetsManager.Services.Viewer.Semantics
{
    /// <summary>
    /// Stable MAP outliner identity, classification and visibility semantics shared by UI and runtime.
    /// The hierarchy is deliberately only two levels deep: chunk -> authored placeables.
    /// </summary>
    internal static class MapOutlineSemantics
    {
        private const uint LocatorClass = 0xa844df61;
        private const uint ScriptLocatorClass = 0x091c0b1c;
        private const uint GroupClass = 0xf3726d48;
        private const uint AudioClass = 0xa783cfd5;

        internal static string ChunkId(uint chunkHash) => $"0x{chunkHash:x8}";

        internal static string ItemId(uint chunkHash, uint keyHash) =>
            $"{ChunkId(chunkHash)}/0x{keyHash:x8}";

        internal static bool IsHidden(
            IReadOnlySet<string> hidden,
            uint chunkHash,
            uint keyHash)
        {
            if (hidden == null || hidden.Count == 0)
                return false;

            return hidden.Contains(ChunkId(chunkHash)) ||
                   hidden.Contains(ItemId(chunkHash, keyHash));
        }

        internal static IReadOnlyList<MapOutlineChunkData> Build(
            IReadOnlyList<MapPlaceableChunkData> chunks,
            IReadOnlyList<MapCharacterData> characters,
            IReadOnlyList<MapParticleData> particles,
            HashResolverService hashResolver)
        {
            if (chunks == null || chunks.Count == 0)
                return Array.Empty<MapOutlineChunkData>();

            var characterIds = new HashSet<string>(
                characters?.Where(item => item != null)
                    .Select(item => ItemId(item.ChunkHash, item.KeyHash)) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var particleIds = new HashSet<string>(
                particles?.Where(item => item != null)
                    .Select(item => ItemId(item.ChunkHash, item.KeyHash)) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            return chunks
                .Where(chunk => chunk != null && chunk.Items != null && chunk.Items.Count > 0)
                .Select(chunk => BuildChunk(chunk, characterIds, particleIds, hashResolver))
                .ToArray();
        }

        private static MapOutlineChunkData BuildChunk(
            MapPlaceableChunkData chunk,
            IReadOnlySet<string> characterIds,
            IReadOnlySet<string> particleIds,
            HashResolverService hashResolver)
        {
            string id = ChunkId(chunk.EntryHash);
            string name = string.IsNullOrWhiteSpace(chunk.Name) ? null : chunk.Name;
            string label = name == null
                ? id
                : name[(name.LastIndexOf('/') + 1)..];

            MapOutlineItemData[] items = chunk.Items
                .Where(item => item != null)
                .Select(item =>
                {
                    string itemId = ItemId(item.ChunkHash, item.KeyHash);
                    return new MapOutlineItemData(
                        itemId,
                        item.ChunkHash,
                        item.KeyHash,
                        string.IsNullOrWhiteSpace(item.Name) ? $"0x{item.KeyHash:x8}" : item.Name,
                        ResolveClassName(item.ClassHash, hashResolver),
                        KindOf(item, itemId, characterIds, particleIds),
                        item.Position,
                        item.Visibility,
                        item.VisibilityController);
                })
                .ToArray();

            return new MapOutlineChunkData(id, chunk.EntryHash, name, label, items);
        }

        private static MapOutlineItemKind KindOf(
            MapPlaceableData item,
            string itemId,
            IReadOnlySet<string> characterIds,
            IReadOnlySet<string> particleIds)
        {
            if (particleIds.Contains(itemId)) return MapOutlineItemKind.Particle;
            if (characterIds.Contains(itemId)) return MapOutlineItemKind.Character;

            return item.ClassHash switch
            {
                LocatorClass or ScriptLocatorClass => MapOutlineItemKind.Locator,
                GroupClass => MapOutlineItemKind.Group,
                AudioClass => MapOutlineItemKind.Audio,
                _ => MapOutlineItemKind.Other
            };
        }

        private static string ResolveClassName(uint classHash, HashResolverService resolver)
        {
            string resolved = resolver?.ResolveBinType(classHash);
            return IsFallbackHash(resolved, classHash)
                ? $"0x{classHash:x8}"
                : resolved;
        }

        private static bool IsFallbackHash(string value, uint hash) =>
            string.IsNullOrWhiteSpace(value) ||
            value.Equals(hash.ToString("x8"), StringComparison.OrdinalIgnoreCase) ||
            value.Equals($"0x{hash:x8}", StringComparison.OrdinalIgnoreCase);
    }
}
