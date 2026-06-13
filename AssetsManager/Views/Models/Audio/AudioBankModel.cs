using System.Collections.Generic;
using AssetsManager.Views.Models.Explorer;

namespace AssetsManager.Views.Models.Audio
{
    public abstract class AudioBankNode
    {
        public string Name { get; set; }
    }

    public class AudioEventNode : AudioBankNode
    {
        public List<AudioContainerNode> Containers { get; set; } = new List<AudioContainerNode>();
        public List<WemFileNode> Sounds { get; set; } = new List<WemFileNode>();
        
        // Technical metadata to detect changes in hidden parameters
        public AudioTechnicalMetadata TechnicalInfo { get; set; }
        public bool IsTechnicalNode { get; set; }
    }

    public class AudioTechnicalMetadata
    {
        public int ObjectCount { get; set; }
        public long TotalSize { get; set; }
        public string Checksum { get; set; }
    }

    public class AudioContainerNode : AudioBankNode
    {
        public List<WemFileNode> Sounds { get; set; } = new List<WemFileNode>();
    }

    public class WemFileNode : AudioBankNode
    {
        public uint Id { get; set; }
        public uint Offset { get; set; }
        public uint Size { get; set; }
        public AudioSourceType Source { get; set; }
    }

    public enum BinType { Champion, Map, Companion, Unknown }

    // Describes the .bin file that backs an audio bank (e.g. a skin's root.bin
    // for a champion, common.bin for a map). Used by AudioBankLinkerService
    // and any consumer that needs to locate the matching .bin without touching
    // the disk.
    public record BinFileStrategy(string BinPath, string TargetWadName, BinType Type);

    // Kind of file a dependency refers to inside the bank family.
    public enum AudioDependencyType { Bin, EventsBnk, AudioBnk, AudioWpk }

    // Lightweight DTO describing a single dependency of an audio bank
    // (the .bin sibling, the _events.bnk / _audio.bnk / _audio.wpk companions).
    // Path hash is pre-computed for convenience so consumers do not need to
    // import the hashing layer.
    public class AudioDependencyInfo
    {
        public string Path { get; set; }
        public string SourceWad { get; set; }
        public ulong PathHash { get; set; }
        public AudioDependencyType Type { get; set; }
    }

    public class LinkedAudioBank
    {
        public FileSystemNodeModel WpkNode { get; set; }
        public FileSystemNodeModel AudioBnkNode { get; set; }
        public FileSystemNodeModel EventsBnkNode { get; set; }
        public byte[] BinData { get; set; }
        public string BaseName { get; set; }
        public BinType BinType { get; set; }
    }
}
