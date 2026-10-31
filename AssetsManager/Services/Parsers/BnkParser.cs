using System.IO;
using System.Collections.Generic;
using AssetsManager.Services.Core;
using AssetsManager.Views.Models.Audio;

namespace AssetsManager.Services.Parsers
{
    public static class BnkParser
    {
        public static BnkFile Parse(Stream stream, LogService logService)
        {
            logService.LogDebug($"[BNK DIAGNOSTIC] Stream received. Length: {stream.Length} bytes.");
            var bnk = new BnkFile();
            using var sectionReader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);

            while (stream.Position < stream.Length)
            {
                BnkParseHelper.RequireBytes(sectionReader, 8);
                long sectionStartPos = stream.Position;
                string signature = System.Text.Encoding.ASCII.GetString(sectionReader.ReadBytes(4));
                uint sectionSize = sectionReader.ReadUInt32();
                long nextSectionStart = stream.Position + sectionSize;

                logService.LogDebug($"[BNK DEBUG] Found section: {signature}, Size: {sectionSize}, Pos: {sectionStartPos}");
                if (nextSectionStart > stream.Length)
                    throw new InvalidDataException($"Section {signature} exceeds the bank boundary.");

                switch (signature)
                {
                    case "BKHD":
                        if (sectionSize < 8) throw new InvalidDataException("BKHD is shorter than its version and ID.");
                        bnk.Bkhd = new BkhdSectionData
                        {
                            Version = sectionReader.ReadUInt32(),
                            Id = sectionReader.ReadUInt32()
                        };
                        stream.Position = nextSectionStart;
                        break;

                    case "HIRC":
                        if (bnk.Bkhd == null) throw new InvalidDataException("HIRC precedes BKHD.");
                        if (sectionSize < 4) throw new InvalidDataException("HIRC is shorter than its object count.");
                        bnk.Hirc = new HircSectionData();
                        uint objectCount = sectionReader.ReadUInt32();
                        if (objectCount > (nextSectionStart - stream.Position) / 9)
                            throw new InvalidDataException("HIRC object count exceeds its section.");
                        logService.LogDebug($"[BNK DEBUG] HIRC section has {objectCount} objects.");

                        for (int i = 0; i < objectCount; i++)
                        {
                            if (nextSectionStart - stream.Position < 9)
                                throw new InvalidDataException("HIRC object header exceeds its section.");
                            var bnkObject = new BnkObject
                            {
                                Type = (BnkObjectType)sectionReader.ReadByte(),
                                Size = sectionReader.ReadUInt32()
                            };
                            if (bnkObject.Size < 4 || bnkObject.Size > nextSectionStart - stream.Position || bnkObject.Size > int.MaxValue)
                                throw new InvalidDataException("HIRC object payload exceeds its section or lacks an ID.");
                            bnkObject.Id = sectionReader.ReadUInt32();
                            // Typed reads stay inside this object, including when a layout is incomplete.
                            using var payload = new MemoryStream(sectionReader.ReadBytes((int)bnkObject.Size - 4), false);
                            using var reader = new BinaryReader(payload);
                            long objEnd = payload.Length;

                            logService.LogDebug($"[BNK HIRC Object] Type: {(byte)bnkObject.Type} ({bnkObject.Type}), ID: {bnkObject.Id}");

                            switch (bnkObject.Type)
                            {
                                case BnkObjectType.Sound:
                                    var soundData = new SoundBnkObjectData();
                                    reader.BaseStream.Seek(4, SeekOrigin.Current);
                                    soundData.StreamType = reader.ReadByte();
                                    soundData.WemId = reader.ReadUInt32();
                                    soundData.SourceId = reader.ReadUInt32();
                                    reader.BaseStream.Seek(4, SeekOrigin.Current);
                                    soundData.ObjectId = reader.ReadUInt32();
                                    bnkObject.Data = soundData;
                                    break;

                                case BnkObjectType.Action:
                                    var actionData = new ActionBnkObjectData { Scope = reader.ReadByte(), Type = reader.ReadByte() };
                                    if (actionData.Type == 25)
                                    {
                                        reader.BaseStream.Seek(5, SeekOrigin.Current);
                                        BnkParseHelper.SkipInitParams(reader);
                                        actionData.SwitchGroupId = reader.ReadUInt32();
                                        actionData.SwitchId = reader.ReadUInt32();
                                    }
                                    else
                                    {
                                        actionData.ObjectId = reader.ReadUInt32();
                                    }
                                    bnkObject.Data = actionData;
                                    break;

                                case BnkObjectType.Event:
                                    var eventData = new EventBnkObjectData();
                                    uint actionCount = bnk.Bkhd.Version == 58 ? reader.ReadUInt32()
                                        : bnk.Bkhd.Version >= 132 ? BnkParseHelper.ReadVariableCount(reader) : reader.ReadByte();
                                    BnkParseHelper.RequireBytes(reader, (long)actionCount * sizeof(uint));
                                    for (int j = 0; j < actionCount; j++) eventData.ActionIds.Add(reader.ReadUInt32());
                                    bnkObject.Data = eventData;
                                    break;

                                case BnkObjectType.ActorMixer:
                                case BnkObjectType.BlendContainer:
                                    var containerData = bnkObject.Type == BnkObjectType.ActorMixer 
                                        ? (BnkObjectData)new ActorMixerBnkObjectData() 
                                        : new BlendContainerBnkObjectData();
                                    
                                    logService.LogDebug($"[BNK PARSER] Parsing {bnkObject.Type} (Type {(byte)bnkObject.Type}), ID: {bnkObject.Id}");
                                    
                                    var childrenList = containerData is ActorMixerBnkObjectData am2 ? am2.Children : (containerData as BlendContainerBnkObjectData).Children;

                                    uint pId;
                                    (pId, _) = BnkParseHelper.SkipBaseParams(reader, bnk.Bkhd.Version);
                                    
                                    if (containerData is ActorMixerBnkObjectData am3) am3.ParentId = pId;
                                    else (containerData as BlendContainerBnkObjectData).ParentId = pId;

                                    uint childCount = reader.ReadUInt32();
                                    for (int j = 0; j < childCount; j++) childrenList.Add(reader.ReadUInt32());
                                    bnkObject.Data = containerData;
                                    break;

                                case BnkObjectType.DialogueEvent:
                                    var dialogueEventData = new DialogueEventBnkObjectData();
                                    logService.LogDebug($"[BNK PARSER] Parsing DialogueEvent (Type 15), ID: {bnkObject.Id}");
                                    reader.ReadByte(); // Probability
                                    uint treeDepth = reader.ReadUInt32();
                                    reader.BaseStream.Seek(treeDepth * 4, SeekOrigin.Current); // Skip Arguments (uint32 * treeDepth)
                                    uint treeDataSize = reader.ReadUInt32();
                                    reader.ReadByte(); // Mode

                                    // Recursive decision tree parsing
                                    void ParseDecisionTreeRecursive(BinaryReader bReader, List<uint> children)
                                    {
                                        bReader.ReadUInt32(); // key
                                        uint audioNodeId = bReader.ReadUInt32();
                                        bReader.ReadUInt16(); // weight
                                        ushort numChildren = bReader.ReadUInt16();

                                        if (numChildren == 0)
                                        {
                                            if (audioNodeId != 0) children.Add(audioNodeId);
                                        }
                                        else
                                        {
                                            for (int j = 0; j < numChildren; j++) ParseDecisionTreeRecursive(bReader, children);
                                        }
                                    }

                                    if (treeDataSize > 0)
                                    {
                                        ParseDecisionTreeRecursive(reader, dialogueEventData.Children);
                                    }
                                    bnkObject.Data = dialogueEventData;
                                    break;

                                case BnkObjectType.RandomOrSequenceContainer:
                                     var randContainerData = new RandomOrSequenceContainerBnkObjectData();
                                     (randContainerData.ParentId, _) = BnkParseHelper.SkipBaseParams(reader, bnk.Bkhd.Version);
                                     reader.BaseStream.Seek(24, SeekOrigin.Current);
                                     uint childCountRand = reader.ReadUInt32();
                                     var rawChildren = new List<uint>();
                                     for (int j = 0; j < childCountRand; j++) rawChildren.Add(reader.ReadUInt32());
                                     
                                     // Ensure we don't read beyond this HIRC object's boundary
                                     long remainingBytes = objEnd - reader.BaseStream.Position;
                                     bool playlistReadSuccess = false;
                                     if (remainingBytes >= 2)
                                     {
                                         uint randPlaylistItemCount = reader.ReadUInt16();
                                         if (randPlaylistItemCount > 0 && (objEnd - reader.BaseStream.Position) >= randPlaylistItemCount * 8)
                                         {
                                             for (int j = 0; j < randPlaylistItemCount; j++)
                                             {
                                                 uint playId = reader.ReadUInt32();
                                                 reader.ReadUInt32(); // Weight
                                                 randContainerData.Children.Add(playId);
                                             }
                                             playlistReadSuccess = true;
                                         }
                                     }
                                     
                                     if (!playlistReadSuccess)
                                     {
                                         randContainerData.Children.AddRange(rawChildren);
                                     }
                                     
                                     bnkObject.Data = randContainerData;
                                     break;

                                case BnkObjectType.SwitchContainer:
                                    var switchContainerData = new SwitchContainerBnkObjectData();
                                    (switchContainerData.ParentId, _) = BnkParseHelper.SkipBaseParams(reader, bnk.Bkhd.Version);
                                    reader.ReadByte(); // GroupType
                                    if (bnk.Bkhd.Version <= 0x59) reader.BaseStream.Seek(3, SeekOrigin.Current);
                                    reader.ReadUInt32(); // GroupId
                                    reader.BaseStream.Seek(5, SeekOrigin.Current);
                                    uint childCountSwitch = reader.ReadUInt32();
                                    for (int j = 0; j < childCountSwitch; j++) switchContainerData.Children.Add(reader.ReadUInt32());
                                    bnkObject.Data = switchContainerData;
                                    break;

                                case BnkObjectType.MusicSegment:
                                case BnkObjectType.MusicPlaylistContainer:
                                    var musicContainerData = bnkObject.Type == BnkObjectType.MusicSegment 
                                        ? (BnkObjectData)new MusicSegmentBnkObjectData() 
                                        : new MusicPlaylistContainerBnkObjectData();
                                    
                                    var mChildrenList = musicContainerData is MusicSegmentBnkObjectData ms ? ms.Children : (musicContainerData as MusicPlaylistContainerBnkObjectData).Children;

                                    reader.BaseStream.Seek(1, SeekOrigin.Current);
                                    BnkParseHelper.SkipBaseParams(reader, bnk.Bkhd.Version);
                                    uint musicChildCount = reader.ReadUInt32();
                                    for (int j = 0; j < musicChildCount; j++) mChildrenList.Add(reader.ReadUInt32());
                                    bnkObject.Data = musicContainerData;
                                    break;

                                case BnkObjectType.MusicTrack:
                                    var musicTrackData = new MusicTrackBnkObjectData();
                                    reader.BaseStream.Seek(1, SeekOrigin.Current);
                                    uint playlistItemCount = reader.ReadUInt32();
                                    reader.BaseStream.Seek(14 * playlistItemCount, SeekOrigin.Current);
                                    uint sourcesCount = reader.ReadUInt32();
                                    for (int j = 0; j < sourcesCount; j++)
                                    {
                                        reader.BaseStream.Seek(4, SeekOrigin.Current); // track_index
                                        uint fileId = reader.ReadUInt32();
                                        musicTrackData.Children.Add(fileId);
                                        reader.BaseStream.Seek(36, SeekOrigin.Current); // Rest of track_source_info struct (44 - 8)
                                    }
                                    bnkObject.Data = musicTrackData;
                                    break;

                                case BnkObjectType.MusicSwitchContainer:
                                    var musicSwitchContainerData = new MusicSwitchContainerBnkObjectData();
                                    reader.BaseStream.Seek(1, SeekOrigin.Current);
                                    (musicSwitchContainerData.ParentId, _) = BnkParseHelper.SkipBaseParams(reader, bnk.Bkhd.Version);
                                    uint numSwitchChildren = reader.ReadUInt32();
                                    for (int j = 0; j < numSwitchChildren; j++) musicSwitchContainerData.Children.Add(reader.ReadUInt32());
                                    reader.BaseStream.Seek(23, SeekOrigin.Current);
                                    uint numStingers = reader.ReadUInt32();
                                    reader.BaseStream.Seek(24 * numStingers, SeekOrigin.Current);
                                    uint numRules = reader.ReadUInt32();
                                    for (int j = 0; j < numRules; j++)
                                    {
                                        uint numSources = reader.ReadUInt32();
                                        reader.BaseStream.Seek(4 * numSources, SeekOrigin.Current);
                                        uint numDestinations = reader.ReadUInt32();
                                        reader.BaseStream.Seek(4 * numDestinations, SeekOrigin.Current);
                                        reader.BaseStream.Seek(bnk.Bkhd.Version <= 0x84 ? 45 : 47, SeekOrigin.Current);
                                        bool hasTransObject = reader.ReadByte() != 0;
                                        if (hasTransObject) reader.BaseStream.Seek(30, SeekOrigin.Current);
                                    }
                                    bnkObject.Data = musicSwitchContainerData;
                                    break;
                            }
                            bnk.Hirc.Objects.Add(bnkObject);
                            if (payload.Position > objEnd)
                                throw new InvalidDataException($"HIRC object {bnkObject.Id} read beyond its payload.");
                        }
                        if (stream.Position != nextSectionStart)
                            throw new InvalidDataException("HIRC contains bytes outside its declared object list.");
                        break;

                    case "DIDX":
                        if (sectionSize % 12 != 0) throw new InvalidDataException("DIDX contains an incomplete media index entry.");
                        // Data Index: Contains the ID, offset, and size of each WEM file in the DATA section.
                        bnk.Didx = new DidxSectionData();
                        int wemCount = (int)sectionSize / 12;
                        for (int i = 0; i < wemCount; i++)
                        {
                            bnk.Didx.Wems.Add(new WemInfo
                            {
                                Id = sectionReader.ReadUInt32(),
                                Offset = sectionReader.ReadUInt32(),
                                Size = sectionReader.ReadUInt32()
                            });
                        }
                        stream.Position = nextSectionStart;
                        break;

                    case "DATA":
                        // Data: A large block containing all the raw WEM file data, concatenated.
                        // We only need to store the starting offset of this section to calculate absolute WEM offsets.
                        bnk.Data = new DataSectionData { Offset = stream.Position };
                        stream.Position = nextSectionStart;
                        break;

                    default:
                        logService.LogDebug($"[BNK DEBUG] Skipping section: {signature}");
                        stream.Position = nextSectionStart;
                        break;
                }
            }
            logService.LogDebug($"[BNK DIAGNOSTIC] Finished parsing. Final stream position: {stream.Position}");
            return bnk;
        }
    }

    internal static class BnkParseHelper
    {
        internal static void RequireBytes(BinaryReader reader, long count)
        {
            if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("BNK field exceeds its containing payload.");
        }

        internal static uint ReadVariableCount(BinaryReader reader)
        {
            uint value = 0;
            for (int i = 0; i < 5; i++)
            {
                byte next = reader.ReadByte();
                if (value > uint.MaxValue >> 7)
                    throw new InvalidDataException("BNK variable-length count overflows UInt32.");
                value = (value << 7) | (uint)(next & 0x7F);
                if ((next & 0x80) == 0) return value;
            }
            throw new InvalidDataException("BNK variable-length count is unterminated.");
        }

        public static void SkipFx(BinaryReader reader, uint bkhdVersion)
        {
            reader.BaseStream.Seek(1, SeekOrigin.Current);
            byte fxCount = reader.ReadByte();
            if (fxCount > 0)
            {
                reader.BaseStream.Seek(1 + fxCount * (bkhdVersion <= 145 ? 7 : 6), SeekOrigin.Current);
            }
            if (bkhdVersion > 136)
            {
                reader.BaseStream.Seek(1, SeekOrigin.Current);
                fxCount = reader.ReadByte();
                reader.BaseStream.Seek(fxCount * 6, SeekOrigin.Current);
            }
            if (bkhdVersion > 89 && bkhdVersion <= 145)
            {
                reader.BaseStream.Seek(1, SeekOrigin.Current);
            }
        }

        public static void SkipInitParams(BinaryReader reader)
        {
            reader.BaseStream.Seek(reader.ReadByte() * 5, SeekOrigin.Current);
            reader.BaseStream.Seek(reader.ReadByte() * 9, SeekOrigin.Current);
        }

        public static void SkipPosParams(BinaryReader reader, uint bkhdVersion)
        {
            byte posBits = reader.ReadByte();
            bool hasPos = (posBits & 1) != 0;
            bool has3d = false;
            bool hasAutomation = false;

            if (hasPos)
            {
                has3d = (posBits & 2) != 0;
            }
            if (hasPos && has3d)
            {
                int mode = (posBits >> 5) & 3;
                hasAutomation = bkhdVersion >= 132 ? mode is 1 or 2 : mode != 0;
                reader.BaseStream.Seek(1, SeekOrigin.Current);
            }
            if (hasAutomation)
            {
                reader.BaseStream.Seek(5, SeekOrigin.Current);
                reader.BaseStream.Seek(16 * reader.ReadUInt32(), SeekOrigin.Current);
                reader.BaseStream.Seek(20 * reader.ReadUInt32(), SeekOrigin.Current);
            }
        }

        public static void SkipAux(BinaryReader reader, uint bkhdVersion)
        {
            bool hasAux = (reader.ReadByte() >> 3 & 1) != 0;
            if (hasAux) reader.BaseStream.Seek(16, SeekOrigin.Current);
            if (bkhdVersion > 135) reader.BaseStream.Seek(4, SeekOrigin.Current);
        }

        public static void SkipStateGroups(BinaryReader reader, uint bkhdVersion)
        {
            reader.BaseStream.Seek(6, SeekOrigin.Current);
            if (bkhdVersion >= 132)
            {
                uint groups = ReadVariableCount(reader);
                for (uint i = 0; i < groups; i++)
                {
                    ReadVariableCount(reader);
                    RequireBytes(reader, 2);
                    reader.BaseStream.Seek(2, SeekOrigin.Current);
                }
                uint properties = ReadVariableCount(reader);
                for (uint i = 0; i < properties; i++)
                {
                    RequireBytes(reader, 5);
                    reader.BaseStream.Seek(5, SeekOrigin.Current);
                    long valuesSize = (long)ReadVariableCount(reader) * 8;
                    RequireBytes(reader, valuesSize);
                    reader.BaseStream.Seek(valuesSize, SeekOrigin.Current);
                }
                return;
            }
            reader.BaseStream.Seek(3 * reader.ReadByte(), SeekOrigin.Current);
            byte count = reader.ReadByte();
            for (int i = 0; i < count; i++)
            {
                reader.BaseStream.Seek(5, SeekOrigin.Current);
                reader.BaseStream.Seek(8 * reader.ReadByte(), SeekOrigin.Current);
            }
        }

        public static void SkipRtpc(BinaryReader reader, uint bkhdVersion)
        {
            ushort rtpcCount = reader.ReadUInt16();
            for (int i = 0; i < rtpcCount; i++)
            {
                if (bkhdVersion >= 132)
                {
                    reader.BaseStream.Seek(6, SeekOrigin.Current);
                    ReadVariableCount(reader);
                    reader.BaseStream.Seek(5, SeekOrigin.Current);
                }
                else reader.BaseStream.Seek(bkhdVersion <= 89 ? 13 : 12, SeekOrigin.Current);
                reader.BaseStream.Seek(12 * reader.ReadUInt16(), SeekOrigin.Current);
            }
        }

        public static (uint parentId, uint busId) SkipBaseParams(BinaryReader reader, uint bkhdVersion)
        {
            SkipFx(reader, bkhdVersion);
            uint busId = reader.ReadUInt32();
            uint parentId = reader.ReadUInt32();
            reader.BaseStream.Seek(bkhdVersion <= 89 ? 2 : 1, SeekOrigin.Current);
            SkipInitParams(reader);
            SkipPosParams(reader, bkhdVersion);
            SkipAux(reader, bkhdVersion);
            SkipStateGroups(reader, bkhdVersion);
            SkipRtpc(reader, bkhdVersion);
            return (parentId, busId);
        }
    }
}
