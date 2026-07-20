using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;

namespace AssetsManager.Services.Viewer.Vfx
{
    /// <summary>
    /// A fault-tolerant PROP/.bin reader for malformed files that LeagueToolkit's strict reader rejects.
    /// Replicates container framing and de-duplicates properties.
    /// </summary>
    public static class TolerantBinReader
    {
        private static readonly Func<BinaryReader, bool, BinTreeProperty> ReadProperty = BuildReader();

        private static Func<BinaryReader, bool, BinTreeProperty> BuildReader()
        {
            var m = typeof(BinTreeProperty).GetMethod("Read",
                BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(BinaryReader), typeof(bool) }, null)
                ?? throw new MissingMethodException("LeagueToolkit BinTreeProperty.Read(BinaryReader, bool) not found.");
            return (Func<BinaryReader, bool, BinTreeProperty>)Delegate.CreateDelegate(
                typeof(Func<BinaryReader, bool, BinTreeProperty>), m);
        }

        public static BinTree Read(byte[] data)
        {
            using var ms = new MemoryStream(data, writable: false);
            using var br = new BinaryReader(ms);

            string magic = Encoding.ASCII.GetString(br.ReadBytes(4));
            if (magic == "PTCH")
            {
                br.ReadUInt64();                                  // patch header (unknown)
                magic = Encoding.ASCII.GetString(br.ReadBytes(4));
            }
            if (magic != "PROP") throw new InvalidDataException($"Not a PROP bin (magic '{magic}').");

            uint version = br.ReadUInt32();

            var dependencies = new List<string>();
            if (version >= 2)
            {
                uint depCount = br.ReadUInt32();
                for (int i = 0; i < depCount; i++)
                {
                    ushort len = br.ReadUInt16();
                    dependencies.Add(Encoding.UTF8.GetString(br.ReadBytes(len)));
                }
            }

            uint objectCount = br.ReadUInt32();
            var classHashes = new uint[objectCount];
            for (int i = 0; i < objectCount; i++) classHashes[i] = br.ReadUInt32();

            var objects = new List<BinTreeObject>((int)objectCount);
            for (int i = 0; i < objectCount; i++)
            {
                uint size = br.ReadUInt32();
                long end = ms.Position + size;
                uint pathHash = br.ReadUInt32();
                ushort propCount = br.ReadUInt16();

                var props = new Dictionary<uint, BinTreeProperty>(propCount);
                for (int p = 0; p < propCount && ms.Position < end; p++)
                {
                    BinTreeProperty prop;
                    try { prop = ReadProperty(br, false); }
                    catch { break; } // give up on this object's tail; keep what parsed
                    props[prop.NameHash] = prop; // tolerant: last value wins on duplicate name
                }

                ms.Position = end; // resync to the object boundary regardless
                objects.Add(new BinTreeObject(pathHash, classHashes[i], props.Values));
            }

            var byHash = new Dictionary<uint, BinTreeObject>(objects.Count);
            foreach (var o in objects) byHash[o.PathHash] = o;
            return new BinTree(byHash.Values, dependencies);
        }
    }

    /// <summary>
    /// Parse a .bin with LeagueToolkit, transparently falling back to the tolerant reader when
    /// the file is malformed.
    /// </summary>
    public static class SafeBinTree
    {
        public static BinTree Parse(byte[] data)
        {
            try { return new BinTree(new MemoryStream(data, writable: false)); }
            catch { return TolerantBinReader.Read(data); }
        }
    }
}
