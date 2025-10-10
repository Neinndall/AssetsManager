using AssetsManager.Services.Core;
using AssetsManager.Views.Models;
using System.IO;

namespace AssetsManager.Services.Parsers
{
    public static class BnkParser
    {
        public static BnkFile Parse(Stream stream, LogService logService)
        {
            logService.LogDebug($"[BNK DIAGNOSTIC] Stream received. Length: {stream.Length} bytes.");
            var bnk = new BnkFile();
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, true);

            while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
            {
                long sectionStartPos = reader.BaseStream.Position;
                string signature = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
                uint sectionSize = reader.ReadUInt32();
                long nextSectionStart = reader.BaseStream.Position + sectionSize;

                logService.LogDebug($"[BNK DEBUG] Found section: {signature}, Size: {sectionSize}, Pos: {sectionStartPos}");

                if (nextSectionStart > reader.BaseStream.Length)
                {
                    logService.LogDebug($"[BNK ERROR] Invalid section size for {signature}. Aborting.");
                    break;
                }

                switch (signature)
                {
                    case "BKHD":
                        bnk.Bkhd = new BkhdSectionData
                        {
                            Version = reader.ReadUInt32(),
                            Id = reader.ReadUInt32()
                        };
                        reader.BaseStream.Seek(nextSectionStart, SeekOrigin.Begin);
                        break;

                    case "HIRC":
                        bnk.Hirc = new HircSectionData();
                        uint objectCount = reader.ReadUInt32();
                        logService.LogDebug($"[BNK DEBUG] HIRC section has {objectCount} objects.");

                        for (int i = 0; i < objectCount; i++)
                        {
                            long objPos = reader.BaseStream.Position;
                            var bnkObject = new BnkObject
                            {
                                Type = (BnkObjectType)reader.ReadByte(),
                                Size = reader.ReadUInt32(),
                                Id = reader.ReadUInt32()
                            };
                            long objEnd = reader.BaseStream.Position + bnkObject.Size - 4;

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
                                    uint actionCount = bnk.Bkhd.Version == 58 ? reader.ReadUInt32() : reader.ReadByte();
                                    for (int j = 0; j < actionCount; j++) eventData.ActionIds.Add(reader.ReadUInt32());
                                    bnkObject.Data = eventData;
                                    break;

                                case BnkObjectType.RandomOrSequenceContainer:
                                    var randContainerData = new RandomOrSequenceContainerBnkObjectData();
                                    (randContainerData.ParentId, _) = BnkParseHelper.SkipBaseParams(reader, bnk.Bkhd.Version);
                                    reader.BaseStream.Seek(24, SeekOrigin.Current);
                                    uint childCountRand = reader.ReadUInt32();
                                    for (int j = 0; j < childCountRand; j++) randContainerData.Children.Add(reader.ReadUInt32());
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
                            }
                            bnk.Hirc.Objects.Add(bnkObject);
                            if (reader.BaseStream.Position != objEnd) reader.BaseStream.Seek(objEnd, SeekOrigin.Begin);
                        }
                        reader.BaseStream.Seek(nextSectionStart, SeekOrigin.Begin);
                        break;

                    default:
                        logService.LogDebug($"[BNK DEBUG] Skipping section: {signature}");
                        reader.BaseStream.Seek(nextSectionStart, SeekOrigin.Begin);
                        break;
                }
            }
            logService.LogDebug($"[BNK DIAGNOSTIC] Finished parsing. Final stream position: {reader.BaseStream.Position}");
            return bnk;
        }
    }

    internal static class BnkParseHelper
    {
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
                hasAutomation = ((posBits >> 5) & 3) != 0;
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

        public static void SkipStateGroups(BinaryReader reader)
        {
            reader.BaseStream.Seek(6, SeekOrigin.Current);
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
                reader.BaseStream.Seek(bkhdVersion <= 89 ? 13 : 12, SeekOrigin.Current);
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
            SkipStateGroups(reader);
            SkipRtpc(reader, bkhdVersion);
            return (parentId, busId);
        }
    }
}
