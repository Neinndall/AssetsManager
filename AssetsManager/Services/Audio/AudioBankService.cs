using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;
using AssetsManager.Services.Parsers.Wwise;
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
            var eventNameMap = GetEventsFromBin(binData);
            var availableWems = ParseAudioBankForWemIds(audioData);
            var audioTree = ParseEventsBank(eventsData, eventNameMap, availableWems);
            return audioTree;
        }

        private Dictionary<uint, string> GetEventsFromBin(byte[] binData)
        {
            var mapEventNames = new Dictionary<uint, string>();
            if (binData == null || binData.Length == 0) return mapEventNames;

            try
            {
                using var stream = new MemoryStream(binData);
                var binTree = new BinTree(stream);
                _logService.Log($"[AUDIO DEBUG] BinTree parsed. Found {binTree.Objects.Count} objects.");

                uint skinCharacterDataPropertiesHash = Fnv1a.HashLower("SkinCharacterDataProperties");
                uint skinAudioPropertiesHash = Fnv1a.HashLower("skinAudioProperties");
                uint bankUnitsHash = Fnv1a.HashLower("bankUnits");
                uint eventsHash = Fnv1a.HashLower("events");
                uint nameHash = Fnv1a.HashLower("name");

                foreach (var obj in binTree.Objects.Values)
                {
                    if (obj.ClassHash == skinCharacterDataPropertiesHash)
                    {
                        _logService.Log("[AUDIO DEBUG] Found SkinCharacterDataProperties object.");
                        if (obj.Properties.TryGetValue(skinAudioPropertiesHash, out var skinAudioProp) && skinAudioProp is BinTreeStruct skinAudioStruct)
                        {
                            _logService.Log("[AUDIO DEBUG] Found skinAudioProperties struct.");
                            if (skinAudioStruct.Properties.TryGetValue(bankUnitsHash, out var bankUnitsProp) && bankUnitsProp is BinTreeContainer bankUnitsContainer)
                            {
                                _logService.Log($"[AUDIO DEBUG] Found bankUnits container with {bankUnitsContainer.Elements.Count} units.");
                                foreach (BinTreeStruct bankUnit in bankUnitsContainer.Elements.OfType<BinTreeStruct>())
                                {
                                    if (bankUnit.Properties.TryGetValue(nameHash, out var nameProp) && nameProp is BinTreeString nameString && nameString.Value.Contains("_VO"))
                                    {
                                        _logService.Log($"[AUDIO DEBUG] Found VO BankUnit: {nameString.Value}");
                                        if (bankUnit.Properties.TryGetValue(eventsHash, out var eventsProp) && eventsProp is BinTreeContainer eventsContainer)
                                        {
                                            foreach (BinTreeString eventNameProp in eventsContainer.Elements.OfType<BinTreeString>())
                                            {
                                                uint eventHash = Fnv1a.HashLower(eventNameProp.Value);
                                                mapEventNames[eventHash] = eventNameProp.Value;
                                            }
                                            _logService.Log($"[AUDIO DEBUG] Extracted {eventsContainer.Elements.Count} events from this unit.");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { _logService.LogError(ex, "[AUDIO DEBUG] Crash during BIN parsing."); }

            _logService.Log($"[AUDIO DEBUG] Finished BIN parsing. Found {mapEventNames.Count} total VO events.");
            return mapEventNames;
        }

        private List<AudioEventNode> ParseEventsBank(byte[] eventsData, Dictionary<uint, string> eventNameMap, List<uint> availableWemIds)
        {
            var eventNodes = new List<AudioEventNode>();
            if (eventsData == null) return eventNodes;

            using var stream = new MemoryStream(eventsData);
            var bnkFile = BnkFile.Parse(stream, _logService);

            if (bnkFile?.Hirc?.Objects == null) 
            {
                _logService.Log("[AUDIO DEBUG] BNK parsing failed or no HIRC objects found.");
                return eventNodes;
            }

            var hircObjects = bnkFile.Hirc.Objects.ToDictionary(o => o.Id);
            _logService.Log($"[AUDIO DEBUG] BNK parsed. Found {hircObjects.Count} HIRC objects.");

            void Traverse(uint objectId, AudioEventNode audioEventNode)
            {
                if (!hircObjects.TryGetValue(objectId, out var currentObject)) return;

                switch (currentObject.Type)
                {
                    case BnkObjectType.Sound:
                        if (currentObject.Data is SoundBnkObjectData soundData)
                        {
                            audioEventNode.Sounds.Add(new AudioSoundNode { Id = soundData.WemId, Name = $"{soundData.WemId}.wem" });
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
            _logService.Log($"[AUDIO DEBUG] Found {eventObjects.Count()} Event objects in BNK.");

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
                    _logService.Log($"[AUDIO DEBUG] Processed event '{eventName}', found {audioEventNode.Sounds.Count} sounds.");
                    eventNodes.Add(audioEventNode);
                }
            }

            return eventNodes;
        }

        private List<uint> ParseAudioBankForWemIds(byte[] audioData)
        {
            var wemIds = new List<uint>();
            if (audioData == null) return wemIds;
            // TODO: Implement WPK/BNK parsing to extract all WEM IDs.
            return wemIds;
        }
    }
}