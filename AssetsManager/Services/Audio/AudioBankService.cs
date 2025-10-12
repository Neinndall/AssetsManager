using System.Collections.Generic;
using System.IO;
using System;
using System.Linq;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using AssetsManager.Services.Parsers;
using AssetsManager.Views.Models;
using AssetsManager.Services.Core;

namespace AssetsManager.Services.Audio
{
    public class AudioBankService
    {
        private class WemSoundInfo
        {
            public uint Id { get; set; }
            public uint Offset { get; set; }
            public uint Size { get; set; }
        }

        private readonly LogService _logService;

        public AudioBankService(LogService logService)
        {
            _logService = logService;
        }

        public List<AudioEventNode> ParseAudioBank(byte[] wpkData, byte[] audioBnkData, byte[] eventsData, byte[] binData, string baseName, BinType binType)
        {
            var eventNameMap = BinParser.GetEventsFromBin(binData, baseName, binType, _logService);

            var allWems = new Dictionary<uint, WemSoundInfo>();
            
            // For VO banks, WPK is the primary source of audio. The accompanying BNK might be redundant or supplementary.
            // We prioritize the WPK and only use the BNK as a fallback if the WPK is missing.
            if (wpkData != null)
            {
                using var audioStream = new MemoryStream(wpkData);
                var wpkFile = WpkParser.Parse(audioStream, _logService);
                if (wpkFile?.Wems != null)
                {
                    foreach (var wem in wpkFile.Wems)
                    {
                        allWems[wem.Id] = new WemSoundInfo { Id = wem.Id, Offset = wem.Offset, Size = wem.Size };
                    }
                    _logService.LogDebug($"[AUDIO] Found {allWems.Count} WEM entries in WPK.");
                }
            }
            
            if (audioBnkData != null)
            {
                using var audioStream = new MemoryStream(audioBnkData);
                var audioBnk = BnkParser.Parse(audioStream, _logService);
                if (audioBnk?.Didx?.Wems != null && audioBnk.Data != null)
                {
                    long dataOffset = audioBnk.Data.Offset;
                    foreach(var wem in audioBnk.Didx.Wems)
                    {
                        allWems[wem.Id] = new WemSoundInfo { Id = wem.Id, Offset = (uint)(wem.Offset + dataOffset), Size = wem.Size };
                    }
                    _logService.LogDebug($"[AUDIO] WPK not found for VO bank, using BNK as fallback. Found {allWems.Count} WEM metadata entries.");
                }
            }

            // Parse the events and link them to the sounds found above.
            var eventNodes = ParseEventsBank(eventsData, eventNameMap, allWems);
            // Find any sounds that were not linked to an event and group them under "Unknown".
            AddUnknownSoundsNode(eventNodes, allWems);

            return eventNodes;
        }

        public List<AudioEventNode> ParseSfxAudioBank(byte[] audioData, byte[] eventsData, byte[] binData, string baseName, BinType binType)
        {
            var eventNameMap = BinParser.GetEventsFromBin(binData, baseName, binType, _logService);

            var allWems = new Dictionary<uint, WemSoundInfo>();
            // For SFX banks, the audio data is stored inside the main .bnk file itself.
            // We parse its DIDX and DATA sections to get the offset and size of each WEM.
            if (audioData != null)
            {
                using var audioStream = new MemoryStream(audioData);
                var audioBnk = BnkParser.Parse(audioStream, _logService);
                if (audioBnk?.Didx?.Wems != null && audioBnk.Data != null)
                {
                    long dataOffset = audioBnk.Data.Offset;
                    foreach(var wem in audioBnk.Didx.Wems)
                    {
                        // The offset in DIDX is relative to the start of the DATA section, so we calculate the absolute offset.
                        allWems[wem.Id] = new WemSoundInfo { Id = wem.Id, Offset = (uint)(wem.Offset + dataOffset), Size = wem.Size };
                    }
                    _logService.LogDebug($"[AUDIO] Found {allWems.Count} WEM metadata entries in audio BNK.");
                }
            }

            var eventNodes = ParseEventsBank(eventsData, eventNameMap, allWems);
            AddUnknownSoundsNode(eventNodes, allWems);

            return eventNodes;
        }

        /// <summary>
        /// Finds all sounds that were not associated with any event and groups them into a new "Unknown" node.
        /// </summary>
        private void AddUnknownSoundsNode(List<AudioEventNode> eventNodes, Dictionary<uint, WemSoundInfo> allSounds)
        {
            if (allSounds == null || !allSounds.Any()) return;

            var linkedWemIds = new HashSet<uint>(eventNodes.SelectMany(e => e.Sounds).Select(s => s.Id));
            var unlinkedWemIds = allSounds.Keys.Where(id => !linkedWemIds.Contains(id)).ToList();

            if (unlinkedWemIds.Any())
            {
                var unknownNode = new AudioEventNode { Name = "Unknown" };
                foreach (var wemId in unlinkedWemIds)
                {
                    var wemInfo = allSounds[wemId];
                    unknownNode.Sounds.Add(new WemFileNode
                    {
                        Id = wemId,
                        Name = $"{wemId}.wem",
                        Offset = wemInfo.Offset,
                        Size = wemInfo.Size
                    });
                }
                eventNodes.Add(unknownNode);
                _logService.LogDebug($"[AUDIO] Added 'Unknown' node with {unlinkedWemIds.Count} unlinked sounds.");
            }
        }

        private List<AudioEventNode> ParseEventsBank(byte[] eventsData, Dictionary<uint, string> eventNameMap, Dictionary<uint, WemSoundInfo> wemMetadata)
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
                            if (wemMetadata != null && wemMetadata.ContainsKey(soundData.WemId))
                            {
                                var wemInfo = wemMetadata[soundData.WemId];
                                _logService.LogDebug($"[AUDIO] Linking sound: {soundData.WemId}");
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
                                _logService.LogDebug($"[AUDIO] Sound {soundData.WemId} found in event, but not in audio BNK/WPK.");
                            }
                        }
                        break;
                    case BnkObjectType.Action:
                        if (currentObject.Data is ActionBnkObjectData actionData)
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

            _logService.LogDebug($"[AUDIO] Finished parsing events bank. Created {eventNodes.Count} event nodes.");
            return eventNodes;
        }
    }
}