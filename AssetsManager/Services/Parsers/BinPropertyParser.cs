using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Globalization;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using AssetsManager.Services.Hashes;
using System.Text.Encodings.Web;

namespace AssetsManager.Services.Parsers
{
    public class BinPropertyParser
    {
        private readonly HashResolverService _hashResolver;

        public BinPropertyParser(HashResolverService hashResolver)
        {
            _hashResolver = hashResolver;
        }

        #region Streaming Serialization (Memory Efficient)

        public Task WriteBinTreeAsJsonStreamingAsync(Stream outputStream, Stream binStream)
        {
            return Task.Run(() => WriteBinTreeAsJsonStreaming(outputStream, binStream));
        }

        private void WriteBinTreeAsJsonStreaming(Stream outputStream, Stream binStream)
        {
            long startPosition = binStream.Position;
            using BinaryReader br = new BinaryReader(binStream, Encoding.UTF8, true);

            string magic = Encoding.ASCII.GetString(br.ReadBytes(4));
            if (magic == "PTCH")
            {
                br.ReadUInt32(); // override version
                br.ReadUInt32(); // object count override
                magic = Encoding.ASCII.GetString(br.ReadBytes(4));
            }

            if (magic != "PROP") throw new InvalidDataException("Invalid BIN signature");

            uint version = br.ReadUInt32();
            if (version > 3) throw new InvalidDataException("Unsupported BIN version: " + version);

            if (version >= 2)
            {
                uint dependencyCount = br.ReadUInt32();
                for (int i = 0; i < dependencyCount; i++)
                {
                    short length = br.ReadInt16();
                    br.ReadBytes(length);
                }
            }

            uint objectCount = br.ReadUInt32();
            uint[] objectClasses = new uint[objectCount];
            for (int i = 0; i < objectCount; i++) objectClasses[i] = br.ReadUInt32();

            long objectsOffset = binStream.Position;
            
            var options = new JsonWriterOptions 
            { 
                Indented = true, 
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
            };

            // Attempt 1: Modern Mode
            try
            {
                using var writer = new Utf8JsonWriter(outputStream, options);
                RunStreamingLoop(writer, br, objectClasses, false);
            }
            catch
            {
                // Attempt 2: Legacy Mode Fallback
                binStream.Position = objectsOffset;
                outputStream.SetLength(0);
                
                try
                {
                    using var writer = new Utf8JsonWriter(outputStream, options);
                    RunStreamingLoop(writer, br, objectClasses, true);
                }
                catch
                {
                    // Attempt 3: Ultimate Fallback to Proven BinTree (LeagueToolkit)
                    binStream.Position = startPosition;
                    outputStream.SetLength(0);
                    var binTree = new BinTree(binStream);
                    WriteBinTreeAsJsonInternal(outputStream, binTree);
                }
            }
        }

        private void RunStreamingLoop(Utf8JsonWriter writer, BinaryReader br, uint[] objectClasses, bool useLegacyType)
        {
            var resolutionCache = new Dictionary<uint, string>();
            string Resolve(uint hash)
            {
                if (resolutionCache.TryGetValue(hash, out var resolved)) return resolved;
                var res = _hashResolver.ResolveBinHashGeneral(hash);
                resolutionCache[hash] = res;
                return res;
            }

            writer.WriteStartObject();
            for (int i = 0; i < objectClasses.Length; i++)
            {
                WriteObjectStreaming(writer, br, objectClasses[i], Resolve, useLegacyType);
            }
            writer.WriteEndObject();
            writer.Flush();
        }

        private void WriteObjectStreaming(Utf8JsonWriter writer, BinaryReader br, uint classHash, Func<uint, string> resolve, bool useLegacyType)
        {
            br.ReadUInt32(); // size
            uint pathHash = br.ReadUInt32();
            ushort propertyCount = br.ReadUInt16();

            writer.WritePropertyName(resolve(pathHash));
            writer.WriteStartObject();
            writer.WriteString("type", resolve(classHash));

            for (int i = 0; i < propertyCount; i++)
            {
                uint nameHash = br.ReadUInt32();
                byte typeByte = br.ReadByte();
                var type = (BinPropertyType)typeByte;
                if (useLegacyType && type >= (BinPropertyType)128) type = (BinPropertyType)(typeByte & 0x7F);

                writer.WritePropertyName(resolve(nameHash));
                WritePropertyContentStreaming(writer, br, type, resolve, useLegacyType);
            }

            writer.WriteEndObject();
        }

        private void WritePropertyContentStreaming(Utf8JsonWriter writer, BinaryReader br, BinPropertyType type, Func<uint, string> resolve, bool useLegacyType)
        {
            switch (type)
            {
                case BinPropertyType.Bool: writer.WriteBooleanValue(br.ReadByte() != 0); break;
                case BinPropertyType.I8: writer.WriteNumberValue(br.ReadSByte()); break;
                case BinPropertyType.U8: writer.WriteNumberValue(br.ReadByte()); break;
                case BinPropertyType.I16: writer.WriteNumberValue(br.ReadInt16()); break;
                case BinPropertyType.U16: writer.WriteNumberValue(br.ReadUInt16()); break;
                case BinPropertyType.I32: writer.WriteNumberValue(br.ReadInt32()); break;
                case BinPropertyType.U32: writer.WriteNumberValue(br.ReadUInt32()); break;
                case BinPropertyType.I64: writer.WriteNumberValue(br.ReadInt64()); break;
                case BinPropertyType.U64: writer.WriteNumberValue(br.ReadUInt64()); break;
                case BinPropertyType.F32: WriteSafeNumber(writer, br.ReadSingle()); break;
                case BinPropertyType.Vector2:
                    writer.WriteStartObject(); WriteSafeNumber(writer, "x", br.ReadSingle()); WriteSafeNumber(writer, "y", br.ReadSingle()); writer.WriteEndObject();
                    break;
                case BinPropertyType.Vector3:
                    writer.WriteStartObject(); WriteSafeNumber(writer, "x", br.ReadSingle()); WriteSafeNumber(writer, "y", br.ReadSingle()); WriteSafeNumber(writer, "z", br.ReadSingle()); writer.WriteEndObject();
                    break;
                case BinPropertyType.Vector4:
                    writer.WriteStartObject(); WriteSafeNumber(writer, "x", br.ReadSingle()); WriteSafeNumber(writer, "y", br.ReadSingle()); WriteSafeNumber(writer, "z", br.ReadSingle()); WriteSafeNumber(writer, "w", br.ReadSingle()); writer.WriteEndObject();
                    break;
                case BinPropertyType.Matrix44:
                    writer.WriteStartArray();
                    for (int i = 0; i < 4; i++) { writer.WriteStartArray(); for (int j = 0; j < 4; j++) WriteSafeNumber(writer, br.ReadSingle()); writer.WriteEndArray(); }
                    writer.WriteEndArray();
                    break;
                case BinPropertyType.Color:
                    writer.WriteStartObject(); writer.WriteNumber("r", br.ReadByte()); writer.WriteNumber("g", br.ReadByte()); writer.WriteNumber("b", br.ReadByte()); writer.WriteNumber("a", br.ReadByte()); writer.WriteEndObject();
                    break;
                case BinPropertyType.String:
                    ushort strLen = br.ReadUInt16();
                    writer.WriteStringValue(Encoding.UTF8.GetString(br.ReadBytes(strLen)));
                    break;
                case BinPropertyType.Hash: writer.WriteStringValue(resolve(br.ReadUInt32())); break;
                case BinPropertyType.WadChunkLink: writer.WriteStringValue(_hashResolver.ResolveHash(br.ReadUInt64())); break;
                case BinPropertyType.ObjectLink: writer.WriteStringValue(resolve(br.ReadUInt32())); break;
                case BinPropertyType.BitBool: writer.WriteBooleanValue(br.ReadByte() != 0); break;
                case BinPropertyType.Optional:
                    byte hasValue = br.ReadByte();
                    if (hasValue != 0)
                    {
                        uint optNameHash = br.ReadUInt32();
                        byte optTypeByte = br.ReadByte();
                        var optType = (BinPropertyType)optTypeByte;
                        if (useLegacyType && optType >= (BinPropertyType)128) optType = (BinPropertyType)(optTypeByte & 0x7F);
                        WritePropertyContentStreaming(writer, br, optType, resolve, useLegacyType);
                    }
                    else writer.WriteNullValue();
                    break;
                case BinPropertyType.Container:
                case BinPropertyType.UnorderedContainer:
                    byte itemTypeByte = br.ReadByte();
                    var itemType = (BinPropertyType)itemTypeByte;
                    if (useLegacyType && itemType >= (BinPropertyType)128) itemType = (BinPropertyType)(itemTypeByte & 0x7F);
                    br.ReadUInt32(); // container size
                    uint itemCount = br.ReadUInt32();
                    writer.WriteStartArray();
                    for (uint i = 0; i < itemCount; i++) WritePropertyContentStreaming(writer, br, itemType, resolve, useLegacyType);
                    writer.WriteEndArray();
                    break;
                case BinPropertyType.Struct:
                case BinPropertyType.Embedded:
                    uint structClassHash = br.ReadUInt32();
                    if (structClassHash == 0) { writer.WriteNullValue(); return; }
                    br.ReadUInt32(); // struct size
                    ushort structPropCount = br.ReadUInt16();
                    writer.WriteStartObject();
                    writer.WriteString("type", resolve(structClassHash));
                    for (int i = 0; i < structPropCount; i++)
                    {
                        uint pNameHash = br.ReadUInt32();
                        byte pTypeByte = br.ReadByte();
                        var pType = (BinPropertyType)pTypeByte;
                        if (useLegacyType && pType >= (BinPropertyType)128) pType = (BinPropertyType)(pTypeByte & 0x7F);
                        writer.WritePropertyName(resolve(pNameHash));
                        WritePropertyContentStreaming(writer, br, pType, resolve, useLegacyType);
                    }
                    writer.WriteEndObject();
                    break;
                case BinPropertyType.Map:
                    byte kTypeByte = br.ReadByte();
                    var kType = (BinPropertyType)kTypeByte;
                    if (useLegacyType && kType >= (BinPropertyType)128) kType = (BinPropertyType)(kTypeByte & 0x7F);
                    byte vTypeByte = br.ReadByte();
                    var vType = (BinPropertyType)vTypeByte;
                    if (useLegacyType && vType >= (BinPropertyType)128) vType = (BinPropertyType)(vTypeByte & 0x7F);
                    br.ReadUInt32(); // map size
                    uint mapCount = br.ReadUInt32();
                    writer.WriteStartObject();
                    for (uint i = 0; i < mapCount; i++)
                    {
                        string keyStr = ReadPropertyAsKeyStringStreaming(br, kType, resolve, useLegacyType);
                        writer.WritePropertyName(keyStr);
                        WritePropertyContentStreaming(writer, br, vType, resolve, useLegacyType);
                    }
                    writer.WriteEndObject();
                    break;
                default:
                    writer.WriteStartObject(); writer.WriteString("Type", type.ToString()); writer.WriteEndObject();
                    break;
            }
        }

        private string ReadPropertyAsKeyStringStreaming(BinaryReader br, BinPropertyType type, Func<uint, string> resolve, bool useLegacyType)
        {
            return type switch
            {
                BinPropertyType.I8 => br.ReadSByte().ToString(),
                BinPropertyType.U8 => br.ReadByte().ToString(),
                BinPropertyType.I16 => br.ReadInt16().ToString(),
                BinPropertyType.U16 => br.ReadUInt16().ToString(),
                BinPropertyType.I32 => br.ReadInt32().ToString(),
                BinPropertyType.U32 => br.ReadUInt32().ToString(),
                BinPropertyType.I64 => br.ReadInt64().ToString(),
                BinPropertyType.U64 => br.ReadUInt64().ToString(),
                BinPropertyType.F32 => br.ReadSingle().ToString(CultureInfo.InvariantCulture),
                BinPropertyType.String => Encoding.UTF8.GetString(br.ReadBytes(br.ReadUInt16())),
                BinPropertyType.Hash => resolve(br.ReadUInt32()),
                _ => "UnknownKey_" + type.ToString()
            };
        }

        #endregion

        #region Fallback Serialization (BinTree)

        private void WriteBinTreeAsJsonInternal(Stream outputStream, BinTree binTree)
        {
            var options = new JsonWriterOptions 
            { 
                Indented = true, 
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
            };
            using var writer = new Utf8JsonWriter(outputStream, options);

            var resolutionCache = new Dictionary<uint, string>();
            string Resolve(uint hash)
            {
                if (resolutionCache.TryGetValue(hash, out var resolved)) return resolved;
                var res = _hashResolver.ResolveBinHashGeneral(hash);
                resolutionCache[hash] = res;
                return res;
            }

            writer.WriteStartObject();
            foreach (var kvp in binTree.Objects)
            {
                writer.WritePropertyName(Resolve(kvp.Key));
                writer.WriteStartObject();
                writer.WriteString("type", Resolve(kvp.Value.ClassHash));
                foreach (var propKvp in kvp.Value.Properties)
                {
                    writer.WritePropertyName(Resolve(propKvp.Key));
                    WritePropertyValueInternal(writer, propKvp.Value, Resolve);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.Flush();
        }

        private void WritePropertyValueInternal(Utf8JsonWriter writer, BinTreeProperty prop, Func<uint, string> resolve)
        {
            if (prop == null) { writer.WriteNullValue(); return; }

            switch (prop.Type)
            {
                case BinPropertyType.String: writer.WriteStringValue(((BinTreeString)prop).Value); break;
                case BinPropertyType.Hash: writer.WriteStringValue(_hashResolver.ResolveBinHashGeneral(((BinTreeHash)prop).Value)); break;
                case BinPropertyType.I8: writer.WriteNumberValue(((BinTreeI8)prop).Value); break;
                case BinPropertyType.U8: writer.WriteNumberValue(((BinTreeU8)prop).Value); break;
                case BinPropertyType.I16: writer.WriteNumberValue(((BinTreeI16)prop).Value); break;
                case BinPropertyType.U16: writer.WriteNumberValue(((BinTreeU16)prop).Value); break;
                case BinPropertyType.I32: writer.WriteNumberValue(((BinTreeI32)prop).Value); break;
                case BinPropertyType.U32: writer.WriteNumberValue(((BinTreeU32)prop).Value); break;
                case BinPropertyType.I64: writer.WriteNumberValue(((BinTreeI64)prop).Value); break;
                case BinPropertyType.U64: writer.WriteNumberValue(((BinTreeU64)prop).Value); break;
                case BinPropertyType.F32: WriteSafeNumber(writer, ((BinTreeF32)prop).Value); break;
                case BinPropertyType.Bool: writer.WriteBooleanValue(((BinTreeBool)prop).Value); break;
                case BinPropertyType.BitBool: writer.WriteBooleanValue(((BinTreeBitBool)prop).Value); break;
                case BinPropertyType.Vector2:
                    var v2 = ((BinTreeVector2)prop).Value;
                    writer.WriteStartObject(); WriteSafeNumber(writer, "x", v2.X); WriteSafeNumber(writer, "y", v2.Y); writer.WriteEndObject();
                    break;
                case BinPropertyType.Vector3:
                    var v3 = ((BinTreeVector3)prop).Value;
                    writer.WriteStartObject(); WriteSafeNumber(writer, "x", v3.X); WriteSafeNumber(writer, "y", v3.Y); WriteSafeNumber(writer, "z", v3.Z); writer.WriteEndObject();
                    break;
                case BinPropertyType.Vector4:
                    var v4 = ((BinTreeVector4)prop).Value;
                    writer.WriteStartObject(); WriteSafeNumber(writer, "x", v4.X); WriteSafeNumber(writer, "y", v4.Y); WriteSafeNumber(writer, "z", v4.Z); WriteSafeNumber(writer, "w", v4.W); writer.WriteEndObject();
                    break;
                case BinPropertyType.Matrix44:
                    var m44 = ((BinTreeMatrix44)prop).Value;
                    writer.WriteStartArray();
                    for (int i = 0; i < 4; i++) { writer.WriteStartArray(); WriteSafeNumber(writer, m44[i, 0]); WriteSafeNumber(writer, m44[i, 1]); WriteSafeNumber(writer, m44[i, 2]); WriteSafeNumber(writer, m44[i, 3]); writer.WriteEndArray(); }
                    writer.WriteEndArray();
                    break;
                case BinPropertyType.Color:
                    var c = ((BinTreeColor)prop).Value;
                    writer.WriteStartObject(); writer.WriteNumber("r", c.R); writer.WriteNumber("g", c.G); writer.WriteNumber("b", c.B); writer.WriteNumber("a", c.A); writer.WriteEndObject();
                    break;
                case BinPropertyType.ObjectLink: writer.WriteStringValue(resolve(((BinTreeObjectLink)prop).Value)); break;
                case BinPropertyType.WadChunkLink: writer.WriteStringValue(_hashResolver.ResolveHash(((BinTreeWadChunkLink)prop).Value)); break;
                case BinPropertyType.Container:
                case BinPropertyType.UnorderedContainer:
                    writer.WriteStartArray();
                    foreach (var p in ((BinTreeContainer)prop).Elements) WritePropertyValueInternal(writer, p, resolve);
                    writer.WriteEndArray();
                    break;
                case BinPropertyType.Struct:
                case BinPropertyType.Embedded:
                    var structProp = (BinTreeStruct)prop;
                    writer.WriteStartObject();
                    writer.WriteString("type", resolve(structProp.ClassHash));
                    foreach (var kvp in structProp.Properties) { writer.WritePropertyName(resolve(kvp.Key)); WritePropertyValueInternal(writer, kvp.Value, resolve); }
                    writer.WriteEndObject();
                    break;
                case BinPropertyType.Optional: WritePropertyValueInternal(writer, ((BinTreeOptional)prop).Value, resolve); break;
                case BinPropertyType.Map:
                    writer.WriteStartObject();
                    foreach (var kvp in (BinTreeMap)prop) { writer.WritePropertyName(ConvertPropertyToStringInternal(kvp.Key)); WritePropertyValueInternal(writer, kvp.Value, resolve); }
                    writer.WriteEndObject();
                    break;
                default:
                    writer.WriteStartObject(); writer.WriteString("Type", prop.Type.ToString()); writer.WriteString("NameHash", resolve(prop.NameHash)); writer.WriteEndObject();
                    break;
            }
        }

        private string ConvertPropertyToStringInternal(BinTreeProperty prop)
        {
            if (prop == null) return "null";
            switch (prop.Type)
            {
                case BinPropertyType.String: return ((BinTreeString)prop).Value;
                case BinPropertyType.Hash: return _hashResolver.ResolveBinHashGeneral(((BinTreeHash)prop).Value);
                case BinPropertyType.I8: return ((BinTreeI8)prop).Value.ToString();
                case BinPropertyType.U8: return ((BinTreeU8)prop).Value.ToString();
                case BinPropertyType.I16: return ((BinTreeI16)prop).Value.ToString();
                case BinPropertyType.U16: return ((BinTreeU16)prop).Value.ToString();
                case BinPropertyType.I32: return ((BinTreeI32)prop).Value.ToString();
                case BinPropertyType.U32: return ((BinTreeU32)prop).Value.ToString();
                case BinPropertyType.I64: return ((BinTreeI64)prop).Value.ToString();
                case BinPropertyType.U64: return ((BinTreeU64)prop).Value.ToString();
                default: return _hashResolver.ResolveBinHashGeneral(prop.NameHash);
            }
        }

        #endregion

        #region Helpers

        private void WriteSafeNumber(Utf8JsonWriter writer, float value)
        {
            if (float.IsFinite(value)) 
            {
                string formatted = value.ToString("0.####", CultureInfo.InvariantCulture);
                if (formatted.Contains(".")) writer.WriteRawValue(formatted);
                else writer.WriteNumberValue(value);
            }
            else writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
        }

        private void WriteSafeNumber(Utf8JsonWriter writer, string propertyName, float value)
        {
            if (float.IsFinite(value))
            {
                string formatted = value.ToString("0.####", CultureInfo.InvariantCulture);
                writer.WritePropertyName(propertyName);
                if (formatted.Contains(".")) writer.WriteRawValue(formatted);
                else writer.WriteNumberValue(value);
            }
            else writer.WriteString(propertyName, value.ToString(CultureInfo.InvariantCulture));
        }

        #endregion
    }
}
