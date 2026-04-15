using System;
using System.IO;
using System.Linq;
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

        public AudioBankService(LogService logService)
        {
            _logService = logService;
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
            var eventNameMap = BinParser.GetEventsFromBin(binData, baseName, binType, _logService);
            var allWems = ExtractWems(wpkData, audioBnkData);

            // Parse the events and link them to the sounds found above.
            var eventNodes = ParseEventsBank(eventsData, eventNameMap, allWems);
            // Find any sounds that were not linked to an event and group them under "Unknown".
            AddUnknownSoundsNode(eventNodes, allWems);

            return eventNodes;
        }

        public List<AudioEventNode> ParseSfxAudioBank(byte[] audioData, byte[] eventsData, byte[] binData, string baseName, BinType binType)
        {
            return ParseAudioBank(null, audioData, eventsData, binData, baseName, binType);
        }

        public List<AudioEventNode> ParseGenericAudioBank(byte[] wpkData, byte[] audioBnkData, byte[] eventsData)
        {
            return ParseAudioBank(wpkData, audioBnkData, eventsData, null, null, BinType.Unknown);
        }

        private Dictionary<uint, WemSoundInfo> ExtractWems(byte[] wpkData, byte[] audioBnkData)
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

            // Always process the BNK to find potentially missing audios (Common in VO containers)
            if (audioBnkData != null)
            {
                using var audioStream = new MemoryStream(audioBnkData);
                var audioBnk = BnkParser.Parse(audioStream, _logService);
                if (audioBnk?.Didx?.Wems != null && audioBnk.Data != null)
                {
                    long dataOffset = audioBnk.Data.Offset;
                    int bnkWemCount = 0;
                    foreach (var wem in audioBnk.Didx.Wems)
                    {
                        // The offset in DIDX is relative to the start of the DATA section, so we calculate the absolute offset.
                        if (!allWems.ContainsKey(wem.Id))
                        {
                            allWems[wem.Id] = new WemSoundInfo 
                            { 
                                Id = wem.Id, 
                                Offset = (uint)(wem.Offset + dataOffset), 
                                Size = wem.Size,
                                Source = AudioSourceType.Bnk
                            };
                            bnkWemCount++;
                        }
                    }
                    _logService.LogDebug($"[AUDIO] Found {bnkWemCount} ADDITIONAL WEM metadata entries in BNK.");
                }
            }

            return allWems;
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

            var eventObjects = bnkFile.Hirc.Objects.Where(o => o.Type == BnkObjectType.Event || o.Type == BnkObjectType.DialogueEvent);

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
