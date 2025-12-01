using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Audio;

namespace AssetsManager.Services.Parsers
{
    public static class BinParser
    {
        public static Dictionary<uint, string> GetEventsFromBin(byte[] binData, string bankName, BinType binType, LogService logService)
        {
            var mapEventNames = new Dictionary<uint, string>();
            if (binData == null || binData.Length == 0) return mapEventNames;

            try
            {
                // Search for 'events' containers
                var eventsMagicBytes = new byte[] { 0x84, 0xE3, 0xD8, 0x12, 0x80, 0x10 };
                SearchAndExtract(binData, eventsMagicBytes, mapEventNames, logService);

                // Search for 'music' embedded objects
                var musicMagicBytes = new byte[] { 0xD4, 0x4F, 0x9C, 0x9F, 0x83 };
                SearchAndExtractMusic(binData, musicMagicBytes, mapEventNames, logService);
            }
            catch (Exception ex)
            {
                logService.LogError(ex, "[AUDIO] Crash during raw byte-based BIN processing in BinParser.");
            }

            if (mapEventNames.Any())
            {
                var eventsToShow = mapEventNames.Take(20).Select(kvp => string.Format("({0}: {1})", kvp.Key, kvp.Value));
                string eventList = string.Join(", ", eventsToShow);
            }
            return mapEventNames;
        }
        
        private static void SearchAndExtract(byte[] haystack, byte[] needle, Dictionary<uint, string> map, LogService logService)
        {
            int currentPosition = 0;
            while (currentPosition < haystack.Length)
            {
                int foundIndex = FindBytes(haystack, needle, currentPosition);
                if (foundIndex == -1) break;

                currentPosition = foundIndex + needle.Length;
                
                // Read amount
                if (currentPosition + 8 > haystack.Length)
                {
                    logService.LogWarning("Malformed/truncated BIN file: Not enough bytes for amount after magic sequence (events).");
                    break;
                }
                // Skipping 4 bytes of object size
                uint amount = BitConverter.ToUInt32(haystack, currentPosition + 4);
                currentPosition += 8;

                for (int i = 0; i < amount; i++)
                {
                    if (currentPosition + 2 > haystack.Length)
                    {
                        logService.LogWarning("Malformed/truncated BIN file: Not enough bytes for string length (events).");
                        break;
                    }
                    
                    // Read length-prefixed string
                    ushort stringLength = BitConverter.ToUInt16(haystack, currentPosition);
                    currentPosition += 2;

                    if (currentPosition + stringLength > haystack.Length)
                    {
                        logService.LogWarning("Malformed/truncated BIN file: Not enough bytes for string data (events).");
                        break;
                    }
                    
                    string eventName = System.Text.Encoding.ASCII.GetString(haystack, currentPosition, stringLength);
                    currentPosition += stringLength;

                    uint eventHash = Fnv1Hash(eventName);
                    if (!map.ContainsKey(eventHash))
                    {
                        map[eventHash] = eventName;
                    }
                }
            }
        }

        private static void SearchAndExtractMusic(byte[] haystack, byte[] needle, Dictionary<uint, string> map, LogService logService)
        {
            int currentPosition = 0;
            while (currentPosition < haystack.Length)
            {
                int foundIndex = FindBytes(haystack, needle, currentPosition);
                if (foundIndex == -1) break;

                currentPosition = foundIndex + needle.Length;

                // Read type_hash
                if (currentPosition + 4 > haystack.Length)
                {
                    logService.LogWarning("Malformed/truncated BIN file: Not enough bytes for type_hash (music).");
                    break;
                }
                uint typeHash = BitConverter.ToUInt32(haystack, currentPosition);
                currentPosition += 4;
                if (typeHash == 0) continue; // Skip if type_hash is 0

                // Skip object size and read amount
                if (currentPosition + 6 > haystack.Length)
                {
                    logService.LogWarning("Malformed/truncated BIN file: Not enough bytes for object size/amount (music).");
                    break;
                }
                currentPosition += 4; // skip object size (uint32)
                ushort amount = BitConverter.ToUInt16(haystack, currentPosition); // amount is uint16
                currentPosition += 2;

                for (int i = 0; i < amount; i++)
                {
                    if (currentPosition + 5 > haystack.Length) // 4 bytes for name (hash) + 1 byte for bin_type
                    {
                        logService.LogWarning("Malformed/truncated BIN file: Not enough bytes for name/bin_type (music).");
                        break;
                    }
                    currentPosition += 4; // skip name (hash) (uint32)
                    byte binTypeByte = haystack[currentPosition]; // read bin_type (uint8)
                    currentPosition++;

                    if (binTypeByte != 0x10 /* string */)
                    {
                        logService.LogWarning($"Malformed BIN file: Expected string type (0x10) for music event, but got 0x{binTypeByte:X2}. Skipping.");
                        // C code used goto error, we break this loop. If we continue, we risk reading garbage.
                        break; 
                    }

                    if (currentPosition + 2 > haystack.Length)
                    {
                        logService.LogWarning("Malformed/truncated BIN file: Not enough bytes for string length (music).");
                        break;
                    }
                    
                    ushort stringLength = BitConverter.ToUInt16(haystack, currentPosition);
                    currentPosition += 2;

                    if (currentPosition + stringLength > haystack.Length)
                    {
                        logService.LogWarning("Malformed/truncated BIN file: Not enough bytes for string data (music).");
                        break;
                    }
                    
                    string eventName = System.Text.Encoding.ASCII.GetString(haystack, currentPosition, stringLength);
                    currentPosition += stringLength;

                    uint eventHash = Fnv1Hash(eventName);
                    if (!map.ContainsKey(eventHash))
                    {
                        map[eventHash] = eventName;
                    }
                }
            }
        }


        private static int FindBytes(byte[] src, byte[] find, int startIndex)
        {
            for (int i = startIndex; i <= src.Length - find.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < find.Length; j++)
                {
                    if (src[i + j] != find[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match)
                {
                    return i; // Found a match, return the starting index
                }
            }
            return -1; // No match found after checking all positions
        }

        private static uint Fnv1Hash(string input)
        {
            const uint offsetBasis = 2166136261;
            const uint prime = 16777619;

            uint hash = offsetBasis;
            foreach (char c in input)
            {
                // Custom tolower logic from the C code
                byte b = (byte)c;
                byte lower_b = (b > 64 && b < 91) ? (byte)(b + 32) : b;
                
                hash *= prime;
                hash ^= lower_b;
            }
            return hash;
        }
    }
}


