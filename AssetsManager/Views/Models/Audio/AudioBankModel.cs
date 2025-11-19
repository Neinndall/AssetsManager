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
    }

    public enum BinType { Champion, Map, Unknown }

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
