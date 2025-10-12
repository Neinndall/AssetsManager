using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Services.Core;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

namespace AssetsManager.Services.Parsers
{
    public static class BinParser
    {
        public static Dictionary<uint, string> GetEventsFromBin(byte[] binData, string bankName, LogService logService)
        {
            var mapEventNames = new Dictionary<uint, string>();
            if (binData == null || binData.Length == 0) return mapEventNames;

            try
            {
                using var stream = new MemoryStream(binData);
                var binTree = new BinTree(stream);

                uint skinCharacterDataPropertiesHash = Fnv1a.HashLower("SkinCharacterDataProperties");
                uint skinAudioPropertiesHash = Fnv1a.HashLower("skinAudioProperties");
                uint bankUnitsHash = Fnv1a.HashLower("bankUnits");
                uint eventsHash = Fnv1a.HashLower("events");
                uint nameHash = Fnv1a.HashLower("name");

                foreach (var obj in binTree.Objects.Values)
                {
                    BinTreeContainer bankUnitsContainer = null;

                    if (obj.ClassHash == skinCharacterDataPropertiesHash)
                    {
                        if (obj.Properties.TryGetValue(skinAudioPropertiesHash, out var skinAudioProp) && skinAudioProp is BinTreeStruct skinAudioStruct)
                        {
                            if (skinAudioStruct.Properties.TryGetValue(bankUnitsHash, out var bankUnitsProp) && bankUnitsProp is BinTreeContainer container)
                            {
                                bankUnitsContainer = container;
                            }
                        }
                    }

                    if (bankUnitsContainer != null)
                    {
                        foreach (BinTreeStruct bankUnit in bankUnitsContainer.Elements.OfType<BinTreeStruct>())
                        {
                            if (bankUnit.Properties.TryGetValue(nameHash, out var nameProp) && nameProp is BinTreeString nameString)
                            {
                                if (string.Equals(nameString.Value, bankName, StringComparison.OrdinalIgnoreCase))
                                {
                                    if (bankUnit.Properties.TryGetValue(eventsHash, out var eventsProp) && eventsProp is BinTreeContainer eventsContainer)
                                    {
                                        foreach (BinTreeString eventNameProp in eventsContainer.Elements.OfType<BinTreeString>())
                                        {
                                            uint eventHash = Fnv1Hash(eventNameProp.Value);
                                            mapEventNames[eventHash] = eventNameProp.Value;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { logService.LogError(ex, "[AUDIO] Crash during BIN parsing."); }
            logService.Log($"[AUDIO] Finished BIN parsing. Found {mapEventNames.Count} total events for bank '{bankName}'.");
            return mapEventNames;
        }

        private static uint Fnv1Hash(string input)
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;

            uint hash = offsetBasis;
            foreach (byte b in System.Text.Encoding.ASCII.GetBytes(input.ToLowerInvariant()))
            {
                hash *= prime;
                hash ^= b;
            }
            return hash;
        }
    }
}
