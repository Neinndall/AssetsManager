using AssetsManager.Services.Core;
using AssetsManager.Views.Models;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AssetsManager.Services.Parsers
{
    public static class WpkParser
    {
        public static WpkFile Parse(Stream stream, LogService logService)
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
            logService.LogDebug($"[WPK DEBUG] WPK Version: {wpk.Version}, Found {wemCount} WEM entries.");

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
                    Offset = reader.ReadUInt32(),
                    Size = reader.ReadUInt32()
                };

                uint nameLengthInChars = reader.ReadUInt32();
                int bytesToRead = (int)nameLengthInChars * 2; // UTF-16 uses 2 bytes per character
                byte[] nameBytes = reader.ReadBytes(bytesToRead);
                string wemName = Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');
                logService.LogDebug($"[WPK DEBUG] Read entry: NameLength={nameLengthInChars} chars ({bytesToRead} bytes), Decoded Name='{wemName}'");

                if (uint.TryParse(wemName.Replace(".wem", ""), out uint wemId))
                {
                    wem.Id = wemId;
                    wpk.Wems.Add(wem);
                    logService.LogDebug($"[WPK DEBUG] Success. Parsed ID: {wemId}");
                }
                else
                {
                    logService.LogDebug($"[WPK DEBUG] Failure. Could not parse ID from name: '{wemName}'");
                }
            }

            return wpk;
        }
    }
}
