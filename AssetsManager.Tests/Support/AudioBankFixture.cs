using System;
using System.IO;
using System.Linq;
using System.Text;
using AssetsManager.Views.Models.Audio;

namespace AssetsManager.Tests.Support;

internal static class AudioBankFixture
{
    internal static byte[] Payload(uint id, Action<BinaryWriter> write) => Bytes(writer => { writer.Write(id); write(writer); });

    internal static byte[] Bytes(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true)) write(writer);
        return stream.ToArray();
    }

    internal static void Variable(BinaryWriter writer, uint value)
    {
        int group = 0;
        while (group < 4 && value >> (7 * (group + 1)) != 0) group++;
        for (; group >= 0; group--)
            writer.Write((byte)(((value >> (group * 7)) & 127) | (group == 0 ? 0 : 128)));
    }

    internal static (BnkObjectType, byte[]) Event(uint id, params uint[] actions) => (BnkObjectType.Event,
        Payload(id, writer => { Variable(writer, (uint)actions.Length); foreach (uint action in actions) writer.Write(action); }));

    internal static (BnkObjectType, byte[]) Action(uint id, uint target, byte type = 4) => (BnkObjectType.Action,
        Payload(id, writer =>
        {
            writer.Write((byte)3); writer.Write(type); writer.Write(target); writer.Write((byte)0);
            writer.Write((byte)0); writer.Write((byte)0);
            if (type == 4) { writer.Write((byte)4); writer.Write(1u); writer.Write(0u); }
            else { writer.Write((ushort)0); writer.Write((byte)0); }
        }));

    internal static (BnkObjectType, byte[]) Container(uint id, BnkObjectType kind, params uint[] children) => (kind,
        Payload(id, writer =>
        {
            Node(writer);
            if (kind == BnkObjectType.RandomOrSequenceContainer) writer.Write(new byte[24]);
            writer.Write((uint)children.Length);
            foreach (uint child in children) writer.Write(child);
            if (kind == BnkObjectType.RandomOrSequenceContainer)
            {
                writer.Write((ushort)children.Length);
                foreach (uint child in children) { writer.Write(child); writer.Write(1u); }
            }
        }));

    private static void Node(BinaryWriter writer)
    {
        // v145 NodeBase: FX, metadata, attachment, bus, parent, flags, property bundles,
        // positioning, aux, reflections, advanced settings, state counts and RTPC count.
        writer.Write(new byte[5]); writer.Write(0u); writer.Write(0u);
        writer.Write(new byte[5]); writer.Write(0u); writer.Write(new byte[8]); writer.Write((ushort)0);
    }

    internal static (BnkObjectType, byte[]) Sound(uint id, uint wem) => (BnkObjectType.Sound, Payload(id, writer =>
    {
        writer.Write(0x00040001u); writer.Write((byte)0); writer.Write(wem);
        writer.Write(1u); writer.Write((byte)0); Node(writer);
    }));

    internal static byte[] Bank(params (BnkObjectType Kind, byte[] Payload)[] objects) => Bytes(writer =>
    {
        writer.Write("BKHD"u8); writer.Write(8u); writer.Write(145u); writer.Write(1u);
        writer.Write("HIRC"u8); writer.Write((uint)(4 + objects.Sum(o => 5 + o.Payload.Length)));
        writer.Write((uint)objects.Length);
        foreach (var obj in objects) { writer.Write((byte)obj.Kind); writer.Write((uint)obj.Payload.Length); writer.Write(obj.Payload); }
    });

    internal static byte[] Media(params uint[] ids) => Bytes(writer =>
    {
        writer.Write("BKHD"u8); writer.Write(8u); writer.Write(145u); writer.Write(2u);
        writer.Write("DIDX"u8); writer.Write((uint)(ids.Length * 12));
        for (int i = 0; i < ids.Length; i++) { writer.Write(ids[i]); writer.Write((uint)i); writer.Write(1u); }
        writer.Write("DATA"u8); writer.Write((uint)ids.Length);
        foreach (uint id in ids) writer.Write((byte)id);
    });

    internal static byte[] Package(uint id) => Bytes(writer =>
    {
        byte[] name = Encoding.Unicode.GetBytes(id + ".wem\0");
        writer.Write("r3d2"u8); writer.Write(1u); writer.Write(1u); writer.Write(16u);
        writer.Write((uint)(28 + name.Length)); writer.Write(1u); writer.Write((uint)(name.Length / 2));
        writer.Write(name); writer.Write((byte)id);
    });
}
