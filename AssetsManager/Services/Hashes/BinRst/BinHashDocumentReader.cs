using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Utils.Exceptions;

namespace AssetsManager.Services.Hashes;

/// <summary>Recovers bounded BIN entries for hash research without changing the editor's parser.</summary>
internal static class BinHashDocumentReader
{
    internal static BinTree Read(Stream stream, Action<string> report, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long origin = stream.Position;
        try
        {
            var tree = new BinTree(stream);
            cancellationToken.ThrowIfCancellationRequested();
            return tree;
        }
        catch (Exception exception) when (IsParseError(exception))
        {
            stream.Position = origin;
            return Recover(stream, report, cancellationToken);
        }
    }

    private static BinTree Recover(Stream stream, Action<string> report, CancellationToken cancellationToken)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        string magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        bool patch = magic == "PTCH";
        if (patch)
        {
            if (reader.ReadUInt32() != 1) throw new InvalidDataException("Unsupported BIN patch version.");
            reader.ReadUInt32();
            magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        }
        if (magic != "PROP") throw new InvalidDataException("Invalid BIN signature.");
        uint version = reader.ReadUInt32();
        if (version is not (1 or 2 or 3)) throw new InvalidDataException("Unsupported BIN version.");
        var dependencies = new List<string>();
        if (version >= 2)
        {
            uint count = ReadCount(reader, 2);
            for (uint index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ushort length = reader.ReadUInt16();
                RequireBytes(reader, length);
                dependencies.Add(Encoding.ASCII.GetString(reader.ReadBytes(length)));
            }
        }
        uint objectCount = ReadCount(reader, 4);
        var classes = new uint[objectCount];
        for (int index = 0; index < classes.Length; index++) classes[index] = reader.ReadUInt32();
        var objects = new Dictionary<uint, BinTreeObject>();
        bool complete = true;
        for (int index = 0; index < classes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long offset = stream.Position;
            if (!TryReadBlock(reader, patch: false, out byte[] block))
            {
                report?.Invoke($"Stopped at entry {index}, offset {offset}: invalid or truncated boundary; {classes.Length - index} entries unavailable.");
                complete = false;
                break;
            }
            try
            {
                using var fragment = Document(classes[index], block);
                var tree = new BinTree(fragment);
                if (fragment.Position != fragment.Length) throw new InvalidDataException("Entry has unconsumed bytes.");
                BinTreeObject entry = tree.Objects.Values.Single();
                if (!objects.TryAdd(entry.PathHash, entry)) throw new InvalidDataException($"Duplicate entry 0x{entry.PathHash:x8}.");
            }
            catch (Exception exception) when (IsParseError(exception))
            {
                report?.Invoke($"Skipped entry {index}, class 0x{classes[index]:x8}, offset {offset}: {exception.Message}");
            }
        }

        var overrides = new List<byte[]>();
        if (complete && patch && version >= 3)
        {
            if (stream.Length - stream.Position < 4)
                report?.Invoke("Patch override table is truncated.");
            else
            {
                uint count = reader.ReadUInt32();
                for (uint index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long offset = stream.Position;
                    if (!TryReadBlock(reader, patch: true, out byte[] block))
                    {
                        report?.Invoke($"Stopped at patch override {index}, offset {offset}: invalid or truncated boundary.");
                        break;
                    }
                    try
                    {
                        using var fragment = Document(overrides: new[] { block });
                        _ = new BinTree(fragment);
                        if (fragment.Position != fragment.Length) throw new InvalidDataException("Override has unconsumed bytes.");
                        overrides.Add(block);
                    }
                    catch (Exception exception) when (IsParseError(exception))
                    {
                        report?.Invoke($"Skipped patch override {index}, offset {offset}: {exception.Message}");
                    }
                }
            }
        }

        if (objects.Count == 0 && overrides.Count == 0)
            throw new InvalidDataException("No valid BIN entries or patch overrides could be recovered.");
        using var recovered = patch ? Document(overrides: overrides) : Document();
        var result = new BinTree(recovered);
        result.Dependencies.AddRange(dependencies);
        foreach (var entry in objects) result.Objects.Add(entry.Key, entry.Value);
        report?.Invoke($"Recovered {objects.Count}/{objectCount} BIN entries and {overrides.Count} patch overrides.");
        return result;
    }

    private static MemoryStream Document(uint classHash = 0, byte[] entry = null, IReadOnlyList<byte[]> overrides = null)
    {
        var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        if (overrides != null) { writer.Write("PTCH"u8); writer.Write(1u); writer.Write(0u); }
        writer.Write("PROP"u8); writer.Write(3u); writer.Write(0u);
        writer.Write(entry == null ? 0u : 1u);
        if (entry != null) { writer.Write(classHash); writer.Write(entry); }
        if (overrides != null)
        {
            writer.Write((uint)overrides.Count);
            foreach (byte[] block in overrides) writer.Write(block);
        }
        stream.Position = 0;
        return stream;
    }

    private static bool TryReadBlock(BinaryReader reader, bool patch, out byte[] block)
    {
        block = null;
        Stream stream = reader.BaseStream;
        long start = stream.Position;
        int header = patch ? 8 : 4;
        if (stream.Length - start < header) return false;
        if (patch) reader.ReadUInt32();
        uint size = reader.ReadUInt32();
        if (size < (patch ? 3 : 6) || size > int.MaxValue - header || size > stream.Length - stream.Position)
            return false;
        stream.Position = start;
        block = reader.ReadBytes(checked((int)size + header));
        return true;
    }

    private static uint ReadCount(BinaryReader reader, int minimumBytes)
    {
        uint count = reader.ReadUInt32();
        if (count > (reader.BaseStream.Length - reader.BaseStream.Position) / minimumBytes)
            throw new InvalidDataException("BIN table count exceeds the available bytes.");
        return count;
    }

    private static void RequireBytes(BinaryReader reader, int count)
    {
        if (count > reader.BaseStream.Length - reader.BaseStream.Position) throw new EndOfStreamException();
    }

    private static bool IsParseError(Exception exception)
        => exception is IOException or ArgumentException or InvalidPropertyTypeException or
            InvalidFileSignatureException or InvalidFileVersionException or OverflowException;
}
