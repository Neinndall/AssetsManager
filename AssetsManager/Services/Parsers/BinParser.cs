using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Audio;

namespace AssetsManager.Services.Parsers
{
    public class BinParser
    {
        private readonly HashResolverService _hashResolver;
        private readonly BinPropertyParser _propertyParser;

        public BinParser(HashResolverService hashResolver, BinPropertyParser propertyParser)
        {
            _hashResolver = hashResolver;
            _propertyParser = propertyParser;
        }

        #region Get Events (Audio)

        public Dictionary<uint, string> GetEventsFromBin(byte[] binData, string bankName, BinType binType, LogService logService)
        {
            var mapEventNames = new Dictionary<uint, string>();
            if (binData == null || binData.Length == 0) return mapEventNames;

            try
            {
                using var stream = new MemoryStream(binData);
                var binTree = new BinTree(stream);

                foreach (var kvp in binTree.Objects)
                {
                    foreach (var propKvp in kvp.Value.Properties)
                    {
                        ExtractStrings(propKvp.Value, mapEventNames);
                    }
                }
            }
            catch (Exception ex)
            {
                logService.LogError(ex, "[AUDIO] Crash during BinTree processing in BinParser.");
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
                    if (!string.IsNullOrEmpty(str)) map[Fnv1Hash(str)] = str;
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

        private static uint Fnv1Hash(string input)
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

        #endregion

        #region JSON Serialization

        public Task WriteBinTreeAsJsonAsync(Stream outputStream, Stream binStream)
        {
            return _propertyParser.WriteBinTreeAsJsonStreamingAsync(outputStream, binStream);
        }

        #endregion
    }
}
