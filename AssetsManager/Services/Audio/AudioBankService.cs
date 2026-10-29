using System;
using System.IO;
using System.Linq;
using System.IO.Hashing;
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
        private readonly BinAudioEventExtractor _binAudioEventExtractor;

        public AudioBankService(LogService logService, BinAudioEventExtractor binAudioEventExtractor)
        {
            _logService = logService;
            _binAudioEventExtractor = binAudioEventExtractor;
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

        public List<AudioEventNode> ParseAudioBank(byte[] wpkData, byte[] audioBnkData, byte[] eventsData, byte[] binData, string baseName)
        {
            var eventNameMap = _binAudioEventExtractor.Extract(binData, baseName, _logService);
            
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
                IsTechnicalNode = true,
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

        public List<AudioEventNode> ParseSfxAudioBank(byte[] audioData, byte[] eventsData, byte[] binData, string baseName)
        {
            return ParseAudioBank(null, audioData, eventsData, binData, baseName);
        }

        public List<AudioEventNode> ParseGenericAudioBank(byte[] wpkData, byte[] audioBnkData, byte[] eventsData)
        {
            return ParseAudioBank(wpkData, audioBnkData, eventsData, null, null);
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
            ProcessBnkWems(eventsBnkData, allWems, AudioSourceType.EventsBnk, "EVENTS BNK");

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

            var linkedWemIds = eventNodes.SelectMany(e => e.EnumerateSounds()).Select(s => s.Id).ToHashSet();
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
            var addedEventIds = new HashSet<uint>();

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
                            if (addedEventIds.Add(obj.Id))
                            {
                                eventObjects.Add(obj);
                            }
                        }
                    }
                }
            }

            if (!hircObjects.Any()) return eventNodes;

            void AddSound(uint wemId, AudioEventNode audioEventNode, AudioContainerNode parent)
            {
                if (wemMetadata == null || !wemMetadata.TryGetValue(wemId, out var info)) return;
                var sounds = parent?.Sounds ?? audioEventNode.Sounds;
                if (sounds.Any(sound => sound.Id == wemId)) return;
                sounds.Add(new WemFileNode
                {
                    Id = wemId,
                    Name = $"{wemId}.wem",
                    Offset = info.Offset,
                    Size = info.Size,
                    Source = info.Source
                });
            }

            void Traverse(uint objectId, AudioEventNode audioEventNode)
            {
                // Track the active path: a shared sound may belong to two separate families.
                var path = new HashSet<uint>();
                var pending = new Stack<(uint Id, AudioContainerNode Parent, bool Exit)>();
                pending.Push((objectId, null, false));
                while (pending.TryPop(out var step))
                {
                    if (step.Exit) { path.Remove(step.Id); continue; }
                    if (!path.Add(step.Id)) continue;
                    pending.Push((step.Id, step.Parent, true));
                    if (!hircObjects.TryGetValue(step.Id, out var currentObject)) continue;

                    if (currentObject.Data is IHircContainer container)
                    {
                        string name = $"[{currentObject.Type.GetAbbreviatedName()}] {step.Id}";
                        var siblings = step.Parent?.Containers ?? audioEventNode.Containers;
                        if (siblings.Any(family => family.Name == name)) continue;
                        var family = new AudioContainerNode { Name = name };
                        siblings.Add(family);
                        for (int i = container.Children.Count - 1; i >= 0; i--)
                            pending.Push((container.Children[i], family, false));
                        continue;
                    }

                    switch (currentObject.Data)
                    {
                        case SoundBnkObjectData sound:
                            AddSound(sound.WemId, audioEventNode, step.Parent);
                            break;
                        case ActionBnkObjectData { Type: 4 } action:
                            pending.Push((action.ObjectId, step.Parent, false));
                            break;
                        case MusicTrackBnkObjectData track:
                            // MusicTrack children are media IDs, not hierarchy object IDs.
                            foreach (uint wemId in track.Children)
                                AddSound(wemId, audioEventNode, step.Parent);
                            break;
                    }
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
