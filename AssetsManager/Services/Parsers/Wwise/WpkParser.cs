using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetsManager.Services.Parsers.Wwise
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

        public static WpkFile Parse(Stream stream)
        {
            var wpk = new WpkFile();
            using var reader = new BinaryReader(stream, Encoding.ASCII, true);

            string signature = new string(reader.ReadChars(4));
            if (signature != "r3d2")
            {
                throw new InvalidDataException("Invalid WPK file signature.");
            }

            wpk.Version = reader.ReadUInt32();
            uint wemCount = reader.ReadUInt32();

            var wemInfoOffsets = new List<uint>();
            for (int i = 0; i < wemCount; i++)
            {
                wemInfoOffsets.Add(reader.ReadUInt32());
            }

            foreach (var offset in wemInfoOffsets.Where(o => o != 0))
            {
                reader.BaseStream.Seek(offset, SeekOrigin.Begin);

                var wem = new WpkWem
                {
                    Id = reader.ReadUInt32(),
                    Offset = reader.ReadUInt32(),
                    Size = reader.ReadUInt32()
                };

                wpk.Wems.Add(wem);
            }

            return wpk;
        }

    }
}
