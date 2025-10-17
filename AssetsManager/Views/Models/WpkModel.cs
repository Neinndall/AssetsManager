using System.Collections.Generic;

namespace AssetsManager.Views.Models
{
    public class WpkWem
    {
        public uint Id { get; set; }
        public uint Offset { get; set; }
        public uint Size { get; set; }
    }

    public class WpkFile
    {
        public uint Version { get; set; }
        public List<WpkWem> Wems { get; } = new List<WpkWem>();
    }
}
