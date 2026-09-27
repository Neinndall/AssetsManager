using System;
using System.IO;
using AssetsManager.Views.Models.Viewer;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace AssetsManager.Services.Viewer.Parsing
{
    internal static class MapLightGridParser
    {
        internal static string FindPath(BinTree materials, MapPath map)
        {
            BinTreeStruct bake = MapPostEffectsParser.FindComponent(materials, map, 0x6a4a3409);
            return bake?.Properties.TryGetValue(0x7561b09e, out var property) == true
                ? (property as BinTreeString)?.Value : null;
        }

        internal static MapLightGridData Decode(Stream stream)
        {
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt32() != 3) throw new InvalidDataException("Unsupported light grid version.");
            uint offset = reader.ReadUInt32();
            int width = reader.ReadInt32(), height = reader.ReadInt32();
            float extentX = reader.ReadSingle(), extentZ = reader.ReadSingle();
            float scale = reader.ReadSingle() * 4f, fullBright = reader.ReadSingle();
            if (width < 1 || height < 1 || !float.IsFinite(extentX) || !float.IsFinite(extentZ) ||
                extentX < 1f || extentZ < 1f || !float.IsFinite(scale) || !float.IsFinite(fullBright))
                throw new InvalidDataException("Invalid light grid dimensions or lighting.");
            long count = (long)width * height;
            if (count > int.MaxValue / 24) throw new InvalidDataException("Light grid is too large.");
            long length = count * 24;
            if (offset < 32 || offset > stream.Length ||
                length > stream.Length - offset)
                throw new InvalidDataException("Truncated light grid cells.");
            stream.Position = offset;
            byte[] cells = reader.ReadBytes((int)length);
            if (cells.Length != length) throw new EndOfStreamException("Truncated light grid cells.");
            return new MapLightGridData(width, height, extentX, extentZ, scale, fullBright, cells);
        }
    }
}
