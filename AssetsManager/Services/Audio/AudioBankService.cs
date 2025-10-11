using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;
using AssetsManager.Services.Parsers;
using AssetsManager.Views.Models;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;

using AssetsManager.Services.Core;

namespace AssetsManager.Services.Audio
{
    public class AudioBankService
    {
        private readonly LogService _logService;

        public AudioBankService(LogService logService)
        {
            _logService = logService;
        }

        public List<AudioEventNode> ParseAudioBank(byte[] audioData, byte[] eventsData, byte[] binData)
        {
            var eventNameMap = GetEventsFromBin(binData, "_VO");
            var eventNodes = ParseEventsBank(eventsData, eventNameMap, null);

            // Find unlinked sounds
            var allWemsInWpk = new List<WpkWem>();
            if (audioData != null)
            {
                using var audioStream = new MemoryStream(audioData);
                var wpkFile = WpkParser.Parse(audioStream, _logService);
                if (wpkFile?.Wems != null) allWemsInWpk.AddRange(wpkFile.Wems);
            }

            var linkedWemIds = new HashSet<uint>(eventNodes.SelectMany(e => e.Sounds).Select(s => s.Id));
            var unlinkedWems = allWemsInWpk.Where(w => !linkedWemIds.Contains(w.Id)).ToList();

            if (unlinkedWems.Any())
            {
                var unknownNode = new AudioEventNode { Name = "Unknown" };
                foreach (var wem in unlinkedWems)
                {
                    unknownNode.Sounds.Add(new WemFileNode { Id = wem.Id, Name = $"{wem.Id}.wem" });
                }
                eventNodes.Add(unknownNode);
            }

            return eventNodes;
        }

        public List<AudioEventNode> ParseSfxAudioBank(byte[] audioData, byte[] eventsData, byte[] binData)
        {
            var eventNameMap = GetEventsFromBin(binData, "_SFX");

            Dictionary<uint, WemInfo> wemMetadata = new Dictionary<uint, WemInfo>();
            if (audioData != null)
            {
                using var audioStream = new MemoryStream(audioData);
                var audioBnk = BnkParser.Parse(audioStream, _logService);
                if (audioBnk?.Didx?.Wems != null)
                {
                    foreach(var wem in audioBnk.Didx.Wems)
                    {
                        wemMetadata[wem.Id] = wem;
                    }
                    _logService.Log($"[AUDIO] Found {wemMetadata.Count} WEM metadata entries in audio BNK.");
                }
            }

            var eventNodes = ParseEventsBank(eventsData, eventNameMap, wemMetadata);

            // Find unlinked sounds
            var linkedWemIds = new HashSet<uint>(eventNodes.SelectMany(e => e.Sounds).Select(s => s.Id));
            var unlinkedWemIds = wemMetadata.Keys.Where(id => !linkedWemIds.Contains(id)).ToList();

            if (unlinkedWemIds.Any())
            {
                var unknownNode = new AudioEventNode { Name = "Unknown" };
                foreach (var wemId in unlinkedWemIds)
                {
                    var wemInfo = wemMetadata[wemId];
                    unknownNode.Sounds.Add(new WemFileNode
                    {
                        Id = wemId,
                        Name = $"{wemId}.wem",
                        Offset = wemInfo.Offset,
                        Size = wemInfo.Size
                    });
                }
                eventNodes.Add(unknownNode);
            }

            return eventNodes;
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

        private Dictionary<uint, string> GetEventsFromBin(byte[] binData, string bankType)
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
                    if (obj.ClassHash == skinCharacterDataPropertiesHash)
                    {
                        if (obj.Properties.TryGetValue(skinAudioPropertiesHash, out var skinAudioProp) && skinAudioProp is BinTreeStruct skinAudioStruct)
                        {
                            if (skinAudioStruct.Properties.TryGetValue(bankUnitsHash, out var bankUnitsProp) && bankUnitsProp is BinTreeContainer bankUnitsContainer)
                            {
                                foreach (BinTreeStruct bankUnit in bankUnitsContainer.Elements.OfType<BinTreeStruct>())
                                {
                                    if (bankUnit.Properties.TryGetValue(nameHash, out var nameProp) && nameProp is BinTreeString nameString && nameString.Value.Contains(bankType))
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
            }
            catch (Exception ex) { _logService.LogError(ex, "[AUDIO] Crash during BIN parsing."); }
            _logService.Log($"[AUDIO] Finished BIN parsing. Found {mapEventNames.Count} total {bankType} events.");
            return mapEventNames;
        }

        private List<AudioEventNode> ParseEventsBank(byte[] eventsData, Dictionary<uint, string> eventNameMap, Dictionary<uint, WemInfo> wemMetadata)
        {
            var eventNodes = new List<AudioEventNode>();
            if (eventsData == null) return eventNodes;

            using var stream = new MemoryStream(eventsData);
            var bnkFile = BnkParser.Parse(stream, _logService);

            if (bnkFile?.Hirc?.Objects == null)
            {
                return eventNodes;
            }

            var hircObjects = bnkFile.Hirc.Objects.ToDictionary(o => o.Id);

            void Traverse(uint objectId, AudioEventNode audioEventNode)
            {
                if (!hircObjects.TryGetValue(objectId, out var currentObject)) return;

                switch (currentObject.Type)
                {
                    case BnkObjectType.Sound:
                        if (currentObject.Data is SoundBnkObjectData soundData)
                        {
                            if (wemMetadata == null) // VO Audio Bank
                            {
                                _logService.Log($"[AUDIO] Linking VO sound: {soundData.WemId}");
                                audioEventNode.Sounds.Add(new WemFileNode { Id = soundData.WemId, Name = $"{soundData.WemId}.wem" });
                            }
                            else if (wemMetadata.ContainsKey(soundData.WemId)) // SFX Audio Bank
                            {
                                var wemInfo = wemMetadata[soundData.WemId];
                                _logService.Log($"[AUDIO] Linking SFX sound: {soundData.WemId}");
                                audioEventNode.Sounds.Add(new WemFileNode 
                                {
                                    Id = soundData.WemId, 
                                    Name = $"{soundData.WemId}.wem",
                                    Offset = wemInfo.Offset,
                                    Size = wemInfo.Size
                                });
                            }
                            else
                            {
                                _logService.Log($"[AUDIO] Sound {soundData.WemId} found in event, but not in audio BNK DIDX.");
                            }
                        }
                        break;
                    case BnkObjectType.Action:
                        if (currentObject.Data is ActionBnkObjectData actionData && (actionData.Type == 2 || actionData.Type == 4))
                        {
                            Traverse(actionData.ObjectId, audioEventNode);
                        }
                        break;
                    case BnkObjectType.RandomOrSequenceContainer:
                        if (currentObject.Data is RandomOrSequenceContainerBnkObjectData containerData)
                        {
                            foreach (var soundId in containerData.Children)
                            {
                                Traverse(soundId, audioEventNode);
                            }
                        }
                        break;
                    case BnkObjectType.SwitchContainer:
                        if (currentObject.Data is SwitchContainerBnkObjectData switchData)
                        {
                            foreach (var childId in switchData.Children)
                            {
                                Traverse(childId, audioEventNode);
                            }
                        }
                        break;
                }
            }

            var eventObjects = bnkFile.Hirc.Objects.Where(o => o.Type == BnkObjectType.Event);

            foreach (var eventObj in eventObjects)
            {
                if (eventObj.Data is not EventBnkObjectData eventData) continue;

                string eventName = eventNameMap.GetValueOrDefault(eventObj.Id, eventObj.Id.ToString());
                var audioEventNode = new AudioEventNode { Name = eventName };

                foreach (var actionId in eventData.ActionIds)
                {
                    Traverse(actionId, audioEventNode);
                }

                if (audioEventNode.Sounds.Any() || audioEventNode.Containers.Any())
                {
                    eventNodes.Add(audioEventNode);
                }
            }

            _logService.Log($"[AUDIO] Finished parsing events bank. Created {eventNodes.Count} event nodes.");
            return eventNodes;
        }
    }
}