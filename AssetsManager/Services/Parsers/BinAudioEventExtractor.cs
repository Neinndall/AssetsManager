using System;
using System.Collections.Generic;
using System.IO;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using AssetsManager.Services.Core;

namespace AssetsManager.Services.Parsers
{
    public sealed class BinAudioEventExtractor
    {
        public Dictionary<uint, string> Extract(byte[] binData, string bankName, LogService logService)
        {
            var mapEventNames = new Dictionary<uint, string>();
            if (binData == null || binData.Length == 0) return mapEventNames;

            try
            {
                using var stream = new MemoryStream(binData);
                var binTree = new BinTree(stream);

                // Clean the bank name to guess the champion name prefix (e.g. "Ahri_audio" -> "Ahri")
                string champName = bankName ?? string.Empty;
                int suffixIndex = champName.IndexOf('_');
                if (suffixIndex != -1)
                {
                    champName = champName.Substring(0, suffixIndex);
                }

                // Try to resolve target properties directly to skip parsing unrelated objects
                string[] commonPaths = new[]
                {
                    $"Characters/{champName}/Audio/mAudioEvents",
                    $"Characters/{champName.ToLowerInvariant()}/Audio/mAudioEvents",
                    $"Characters/{champName}/Audio",
                    "mAudioEvents"
                };

                bool foundDirectly = false;
                foreach (var path in commonPaths)
                {
                    if (binTree.TryGetProperty(path, out var audioEventsProp))
                    {
                        ExtractStrings(audioEventsProp, mapEventNames);
                        foundDirectly = true;
                        logService.LogDebug($"[AUDIO] Successfully resolved audio events directly using path: '{path}'");
                        break;
                    }
                }

                // Safe fallback: Scan the entire tree recursively if direct lookup fails
                if (!foundDirectly)
                {
                    logService.LogDebug($"[AUDIO] Direct path lookup missed for '{bankName}'. Falling back to full recursive scan.");
                    foreach (var kvp in binTree.Objects)
                    {
                        foreach (var propKvp in kvp.Value.Properties)
                        {
                            ExtractStrings(propKvp.Value, mapEventNames);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logService.LogError(ex, "[AUDIO] Failed to extract events from BIN metadata.");
            }
            return mapEventNames;
        }

        private void ExtractStrings(BinTreeProperty prop, Dictionary<uint, string> map)
        {
            if (prop == null) return;
            switch (prop.Type)
            {
                case BinPropertyType.String:
                    var str = ((BinTreeString)prop).Value;
                    if (!string.IsNullOrEmpty(str)) map[ComputeWwiseEventHash(str)] = str;
                    break;
                case BinPropertyType.Container:
                case BinPropertyType.UnorderedContainer:
                    foreach (var p in ((BinTreeContainer)prop).Elements) ExtractStrings(p, map);
                    break;
                case BinPropertyType.Struct:
                case BinPropertyType.Embedded:
                    foreach (var p in ((BinTreeStruct)prop).Properties.Values) ExtractStrings(p, map);
                    break;
                case BinPropertyType.Optional:
                    ExtractStrings(((BinTreeOptional)prop).Value, map);
                    break;
                case BinPropertyType.Map:
                    foreach (var kvp in ((BinTreeMap)prop)) { ExtractStrings(kvp.Key, map); ExtractStrings(kvp.Value, map); }
                    break;
            }
        }

        private static uint ComputeWwiseEventHash(string input)
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;
            uint hash = offsetBasis;
            foreach (char c in input)
            {
                byte b = (byte)c;
                byte lower_b = (b > 64 && b < 91) ? (byte)(b + 32) : b;
                hash *= prime;
                hash ^= lower_b;
            }
            return hash;
        }

    }
}
