using System;
using System.IO;
using System.Linq;
using System.IO.Hashing;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Hashing;
using System.Collections.Generic;
using AssetsManager.Services.Parsers;
using AssetsManager.Views.Models.Audio;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Explorer;

namespace AssetsManager.Services.Audio
{
    public class AudioBankService
    {
        private class WemSoundInfo
        {
            public uint Id { get; set; }
            public uint Offset { get; set; }
            public uint Size { get; set; }
            public AudioSourceType Source { get; set; }
        }

        private readonly LogService _logService;
        private readonly BinParser _binParser;

        public AudioBankService(LogService logService, BinParser binParser)
        {
            _logService = logService;
            _binParser = binParser;
        }

        public int GetSoundCount(byte[] wpkData, byte[] audioBnkData)
        {
            // The logic to count is basically what ExtractWems does, but we can make it direct
            if (wpkData != null)
            {
                using var audioStream = new MemoryStream(wpkData);
                var wpkFile = WpkParser.Parse(audioStream, _logService);
                if (wpkFile?.Wems != null && wpkFile.Wems.Any())
                {
                    return wpkFile.Wems.Count;
                }
            }

            if (audioBnkData != null)
            {
                using var audioStream = new MemoryStream(audioBnkData);
                var audioBnk = BnkParser.Parse(audioStream, _logService);
                if (audioBnk?.Didx?.Wems != null)
                {
                    return audioBnk.Didx.Wems.Count;
                }
            }

            return 0;
        }

        public List<AudioEventNode> ParseAudioBank(byte[] wpkData, byte[] audioBnkData, byte[] eventsData, byte[] binData, string baseName, BinType binType)
        {
            var eventNameMap = _binParser.GetEventsFromBin(binData, baseName, binType, _logService);
            
            // Now we include eventsData in WEM extraction in case Riot embedded audios there too
            var allWems = ExtractWems(wpkData, audioBnkData, eventsData);

            // Parse the events and link them to the sounds found above. 
            // We pass both eventsData and audioBnkData to ParseEventsBank to merge HIRC objects.
            var eventNodes = ParseEventsBank(new[] { eventsData, audioBnkData }, eventNameMap, allWems);
            
            // Find any sounds that were not linked to an event and group them under "Unknown".
            AddUnknownSoundsNode(eventNodes, allWems);

            // ADDITION: Add a technical summary node to detect changes in hidden parameters (volume, pitch, etc.)
            AddTechnicalSummaryNode(eventNodes, eventsData, audioBnkData);

            return eventNodes;
        }

        private void AddTechnicalSummaryNode(List<AudioEventNode> eventNodes, byte[] eventsData, byte[] audioBnkData)
        {
            var techNode = new AudioEventNode 
            { 
                Name = "[BNK Technical Summary]",
                TechnicalInfo = new AudioTechnicalMetadata()
            };

            ulong combinedHash = 0;

            if (eventsData != null)
            {
                using var stream = new MemoryStream(eventsData);
                var bnk = BnkParser.Parse(stream, _logService);
                if (bnk?.Hirc != null)
                {
                    techNode.TechnicalInfo.ObjectCount += bnk.Hirc.Objects.Count;
                    techNode.TechnicalInfo.TotalSize += eventsData.Length;
                }
                combinedHash ^= XxHash64.HashToUInt64(eventsData);
            }
            
            if (audioBnkData != null)
            {
                using var stream = new MemoryStream(audioBnkData);
                var bnk = BnkParser.Parse(stream, _logService);
                if (bnk?.Hirc != null)
                {
                    techNode.TechnicalInfo.ObjectCount += bnk.Hirc.Objects.Count;
                    techNode.TechnicalInfo.TotalSize += audioBnkData.Length;
                }
                combinedHash ^= XxHash64.HashToUInt64(audioBnkData);
            }

            techNode.TechnicalInfo.Checksum = combinedHash.ToString("X16");
            eventNodes.Insert(0, techNode);
        }

        public List<AudioEventNode> ParseSfxAudioBank(byte[] audioData, byte[] eventsData, byte[] binData, string baseName, BinType binType)
        {
            return ParseAudioBank(null, audioData, eventsData, binData, baseName, binType);
        }

        public List<AudioEventNode> ParseGenericAudioBank(byte[] wpkData, byte[] audioBnkData, byte[] eventsData)
        {
            return ParseAudioBank(wpkData, audioBnkData, eventsData, null, null, BinType.Unknown);
        }

        private Dictionary<uint, WemSoundInfo> ExtractWems(byte[] wpkData, byte[] audioBnkData, byte[] eventsBnkData = null)
        {
            var allWems = new Dictionary<uint, WemSoundInfo>();

            if (wpkData != null)
            {
                using var audioStream = new MemoryStream(wpkData);
                var wpkFile = WpkParser.Parse(audioStream, _logService);
                if (wpkFile?.Wems != null)
                {
                    foreach (var wem in wpkFile.Wems)
                    {
                        allWems[wem.Id] = new WemSoundInfo 
                        { 
                            Id = wem.Id, 
                            Offset = wem.Offset, 
                            Size = wem.Size,
                            Source = AudioSourceType.Wpk
                        };
                    }
                    _logService.LogDebug($"[AUDIO] Found {allWems.Count} WEM entries in WPK.");
                }
            }

            // Process the audio BNK
            ProcessBnkWems(audioBnkData, allWems, AudioSourceType.Bnk, "AUDIO BNK");

            // Process the events BNK (sometimes has embedded audios too)
            ProcessBnkWems(eventsBnkData, allWems, AudioSourceType.Bnk, "EVENTS BNK");

            return allWems;
        }

        private void ProcessBnkWems(byte[] bnkData, Dictionary<uint, WemSoundInfo> allWems, AudioSourceType source, string logLabel)
        {
            if (bnkData == null) return;

            using var audioStream = new MemoryStream(bnkData);
            var bnk = BnkParser.Parse(audioStream, _logService);
            if (bnk?.Didx?.Wems != null && bnk.Data != null)
            {
                long dataOffset = bnk.Data.Offset;
                int bnkWemCount = 0;
                foreach (var wem in bnk.Didx.Wems)
                {
                    if (!allWems.ContainsKey(wem.Id))
                    {
                        allWems[wem.Id] = new WemSoundInfo 
                        { 
                            Id = wem.Id, 
                            Offset = (uint)(wem.Offset + dataOffset), 
                            Size = wem.Size,
                            Source = source
                        };
                        bnkWemCount++;
                    }
                }
                _logService.LogDebug($"[AUDIO] Found {bnkWemCount} ADDITIONAL WEM metadata entries in {logLabel}.");
            }
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
                        Size = wemInfo.Size,
                        Source = wemInfo.Source
                    });
                }
                eventNodes.Add(unknownNode);
                _logService.LogDebug($"[AUDIO] Added 'Unknown' node with {unlinkedWemIds.Count} unlinked sounds.");
            }
        }

        private List<AudioEventNode> ParseEventsBank(byte[][] bnkDatas, Dictionary<uint, string> eventNameMap, Dictionary<uint, WemSoundInfo> wemMetadata)
        {
            var eventNodes = new List<AudioEventNode>();
            if (bnkDatas == null || bnkDatas.All(d => d == null)) return eventNodes;

            var hircObjects = new Dictionary<uint, BnkObject>();
            var eventObjects = new List<BnkObject>();

            foreach (var bnkData in bnkDatas)
            {
                if (bnkData == null) continue;
                using var stream = new MemoryStream(bnkData);
                var bnkFile = BnkParser.Parse(stream, _logService);

                if (bnkFile?.Hirc?.Objects != null)
                {
                    foreach (var obj in bnkFile.Hirc.Objects)
                    {
                        // Add to global HIRC map for cross-bank resolution
                        if (!hircObjects.ContainsKey(obj.Id)) hircObjects[obj.Id] = obj;
                        
                        // Collect event objects to start traversal from
                        if (obj.Type == BnkObjectType.Event || obj.Type == BnkObjectType.DialogueEvent)
                        {
                            eventObjects.Add(obj);
                        }
                    }
                }
            }

            if (!hircObjects.Any()) return eventNodes;

            void Traverse(uint objectId, AudioEventNode audioEventNode)
            {
                if (!hircObjects.TryGetValue(objectId, out var currentObject)) return;

                if (currentObject.Data is IHircContainer container)
                {
                    _logService.LogDebug($"[AUDIO TRAVERSE] Entering {currentObject.Type} (Type {(byte)currentObject.Type}), ID: {objectId}, Children: {container.Children.Count}");
                    foreach (var childId in container.Children)
                    {
                        Traverse(childId, audioEventNode);
                    }
                    return;
                }

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
                                    Size = wemInfo.Size,
                                    Source = wemInfo.Source
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

                    case BnkObjectType.MusicTrack:
                        if (currentObject.Data is MusicTrackBnkObjectData trackData)
                        {
                            // CRITICAL FIX: MusicTrack.Children contains WEM IDs directly, not HIRC object IDs
                            // These are file IDs that need to be looked up in wemMetadata, not traversed as HIRC objects
                            foreach (var wemId in trackData.Children)
                            {
                                if (wemMetadata != null && wemMetadata.ContainsKey(wemId))
                                {
                                    var wemInfo = wemMetadata[wemId];
                                    _logService.LogDebug($"[AUDIO] Linking WEM from MusicTrack: {wemId}");
                                    audioEventNode.Sounds.Add(new WemFileNode
                                    {
                                        Id = wemId,
                                        Name = $"{wemId}.wem",
                                        Offset = wemInfo.Offset,
                                        Size = wemInfo.Size,
                                        Source = wemInfo.Source
                                    });
                                }
                                else
                                {
                                    _logService.LogDebug($"[AUDIO] WEM {wemId} found in MusicTrack, but not in audio BNK/WPK.");
                                }
                            }
                        }
                        break;
                }
            }

            foreach (var eventObj in eventObjects)
            {
                string eventName = eventNameMap.GetValueOrDefault(eventObj.Id, eventObj.Id.ToString());
                var audioEventNode = new AudioEventNode { Name = eventName };

                if (eventObj.Type == BnkObjectType.Event && eventObj.Data is EventBnkObjectData eventData)
                {
                    foreach (var actionId in eventData.ActionIds)
                    {
                        Traverse(actionId, audioEventNode);
                    }
                }
                else if (eventObj.Type == BnkObjectType.DialogueEvent && eventObj.Data is DialogueEventBnkObjectData dialogueEventData)
                {
                    foreach (var childId in dialogueEventData.Children)
                    {
                        Traverse(childId, audioEventNode);
                    }
                }

                if (audioEventNode.Sounds.Any() || audioEventNode.Containers.Any())
                {
                    eventNodes.Add(audioEventNode);
                }
            }

            _logService.LogDebug($"[AUDIO] Finished parsing events bank. Created {eventNodes.Count} event nodes.");
            return eventNodes.OrderBy(n => n.Name).ToList();
        }
    }
}
