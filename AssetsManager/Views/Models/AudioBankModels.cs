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
        public List<AudioSoundNode> Sounds { get; set; } = new List<AudioSoundNode>();
    }

    public class AudioContainerNode : AudioBankNode
    {
        public List<AudioSoundNode> Sounds { get; set; } = new List<AudioSoundNode>();
    }

    public class AudioSoundNode : AudioBankNode
    {
        public uint Id { get; set; }
    }
}
