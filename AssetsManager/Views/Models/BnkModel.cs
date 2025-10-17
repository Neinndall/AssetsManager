using System.Collections.Generic;

namespace AssetsManager.Views.Models
{
    public enum BnkObjectType
    {
        Settings = 1,
        Sound = 2,
        Action = 3,
        Event = 4,
        RandomOrSequenceContainer = 5,
        SwitchContainer = 6,
        ActorMixer = 7,
        AudioBus = 8,
        BlendContainer = 9,
        MusicSegment = 10,
        MusicTrack = 11,
        MusicSwitchContainer = 12,
        MusicPlaylistContainer = 13,
        Attenuation = 14,
        DialogueEvent = 15,
        MotionBus = 16,
        MotionFX = 17,
        Effect = 18,
        AuxiliaryBus = 19
    }

    public class BnkObjectData { }

    public class SoundBnkObjectData : BnkObjectData
    {
        public uint WemId { get; set; }
        public uint SourceId { get; set; }
        public uint ObjectId { get; set; }
        public byte StreamType { get; set; }
    }

    public class EventBnkObjectData : BnkObjectData
    {
        public List<uint> ActionIds { get; set; } = new List<uint>();
    }

    public class ActionBnkObjectData : BnkObjectData
    {
        public byte Scope { get; set; }
        public byte Type { get; set; }
        public uint ObjectId { get; set; }
        public uint SwitchGroupId { get; set; }
        public uint SwitchId { get; set; }
    }

    public class RandomOrSequenceContainerBnkObjectData : BnkObjectData
    {
        public uint ParentId { get; set; }
        public List<uint> Children { get; set; } = new List<uint>();
    }

    public class SwitchContainerBnkObjectData : BnkObjectData
    {
        public uint ParentId { get; set; }
        public List<uint> Children { get; set; } = new List<uint>();
    }

    public class MusicTrackBnkObjectData : BnkObjectData
    {
        public List<uint> Children { get; set; } = new List<uint>();
    }

    public class MusicSegmentBnkObjectData : BnkObjectData
    {
        public List<uint> Children { get; set; } = new List<uint>();
    }

    public class MusicSwitchContainerBnkObjectData : BnkObjectData
    {
        public uint ParentId { get; set; }
        public List<uint> Children { get; set; } = new List<uint>();
    }

    public class MusicPlaylistContainerBnkObjectData : BnkObjectData
    {
        public List<uint> Children { get; set; } = new List<uint>();
    }

    public class BnkObject
    {
        public BnkObjectType Type { get; set; }
        public uint Size { get; set; }
        public uint Id { get; set; }
        public BnkObjectData Data { get; set; }
    }

    public class BnkSectionData { }

    public class BkhdSectionData : BnkSectionData
    {
        public uint Version { get; set; }
        public uint Id { get; set; }
    }

    public class HircSectionData : BnkSectionData
    {
        public List<BnkObject> Objects { get; set; } = new List<BnkObject>();
    }

    public class WemInfo
    {
        public uint Id { get; set; }
        public uint Offset { get; set; }
        public uint Size { get; set; }
    }

    public class DidxSectionData : BnkSectionData
    {
        public List<WemInfo> Wems { get; set; } = new List<WemInfo>();
    }

    public class DataSectionData : BnkSectionData
    {
        public long Offset { get; set; }
    }

    public class BnkFile
    {
        public BkhdSectionData Bkhd { get; set; }
        public HircSectionData Hirc { get; set; }
        public DidxSectionData Didx { get; set; }
        public DataSectionData Data { get; set; }
    }
}
