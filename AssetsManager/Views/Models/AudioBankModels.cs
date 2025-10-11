using System.Collections.Generic;

namespace AssetsManager.Views.Models
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
}
