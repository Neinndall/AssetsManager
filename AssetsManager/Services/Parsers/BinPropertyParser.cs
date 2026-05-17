using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using AssetsManager.Services.Hashes;

namespace AssetsManager.Services.Parsers
{
    public class BinPropertyParser
    {
        private readonly HashResolverService _hashResolver;

        public BinPropertyParser(HashResolverService hashResolver)
        {
            _hashResolver = hashResolver;
        }

        public Task WriteBinTreeAsJsonAsync(Stream outputStream, BinTree binTree)
        {
            return Task.Run(() => WriteBinTreeAsJson(outputStream, binTree));
        }

        private void WriteBinTreeAsJson(Stream outputStream, BinTree binTree)
        {
            var options = new JsonWriterOptions { Indented = true };
            using var writer = new Utf8JsonWriter(outputStream, options);

            writer.WriteStartObject();
            foreach (var kvp in binTree.Objects)
            {
                writer.WritePropertyName(_hashResolver.ResolveBinHashGeneral(kvp.Key));
                writer.WriteStartObject();
                writer.WriteString("type", _hashResolver.ResolveBinHashGeneral(kvp.Value.ClassHash));
                foreach (var propKvp in kvp.Value.Properties)
                {
                    writer.WritePropertyName(_hashResolver.ResolveBinHashGeneral(propKvp.Key));
                    WritePropertyValue(writer, propKvp.Value);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.Flush();
        }

        private void WritePropertyValue(Utf8JsonWriter writer, BinTreeProperty prop)
        {
            if (prop == null)
            {
                writer.WriteNullValue();
                return;
            }

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
                case BinPropertyType.F32: writer.WriteNumberValue(((BinTreeF32)prop).Value); break;
                case BinPropertyType.Bool: writer.WriteBooleanValue(((BinTreeBool)prop).Value); break;
                case BinPropertyType.BitBool: writer.WriteBooleanValue(((BinTreeBitBool)prop).Value); break;

                case BinPropertyType.Vector2:
                    var v2 = ((BinTreeVector2)prop).Value;
                    writer.WriteStartObject();
                    writer.WriteNumber("x", v2.X);
                    writer.WriteNumber("y", v2.Y);
                    writer.WriteEndObject();
                    break;

                case BinPropertyType.Vector3:
                    var v3 = ((BinTreeVector3)prop).Value;
                    writer.WriteStartObject();
                    writer.WriteNumber("x", v3.X);
                    writer.WriteNumber("y", v3.Y);
                    writer.WriteNumber("z", v3.Z);
                    writer.WriteEndObject();
                    break;

                case BinPropertyType.Vector4:
                    var v4 = ((BinTreeVector4)prop).Value;
                    writer.WriteStartObject();
                    writer.WriteNumber("x", v4.X);
                    writer.WriteNumber("y", v4.Y);
                    writer.WriteNumber("z", v4.Z);
                    writer.WriteNumber("w", v4.W);
                    writer.WriteEndObject();
                    break;

                case BinPropertyType.Matrix44:
                    var m44 = ((BinTreeMatrix44)prop).Value;
                    writer.WriteStartArray();
                    for (int i = 0; i < 4; i++)
                    {
                        writer.WriteStartArray();
                        writer.WriteNumberValue(m44[i, 0]);
                        writer.WriteNumberValue(m44[i, 1]);
                        writer.WriteNumberValue(m44[i, 2]);
                        writer.WriteNumberValue(m44[i, 3]);
                        writer.WriteEndArray();
                    }
                    writer.WriteEndArray();
                    break;

                case BinPropertyType.Color:
                    var c = ((BinTreeColor)prop).Value;
                    writer.WriteStartObject();
                    writer.WriteNumber("r", c.R);
                    writer.WriteNumber("g", c.G);
                    writer.WriteNumber("b", c.B);
                    writer.WriteNumber("a", c.A);
                    writer.WriteEndObject();
                    break;

                case BinPropertyType.ObjectLink: writer.WriteStringValue(_hashResolver.ResolveBinHashGeneral(((BinTreeObjectLink)prop).Value)); break;
                case BinPropertyType.WadChunkLink: writer.WriteStringValue(_hashResolver.ResolveHash(((BinTreeWadChunkLink)prop).Value)); break;

                case BinPropertyType.Container:
                case BinPropertyType.UnorderedContainer:
                    writer.WriteStartArray();
                    var container = (BinTreeContainer)prop;
                    foreach (var p in container.Elements)
                    {
                        WritePropertyValue(writer, p);
                    }
                    writer.WriteEndArray();
                    break;

                case BinPropertyType.Struct:
                case BinPropertyType.Embedded:
                    var structProp = (BinTreeStruct)prop;
                    writer.WriteStartObject();
                    writer.WriteString("type", _hashResolver.ResolveBinHashGeneral(structProp.ClassHash));
                    foreach (var kvp in structProp.Properties)
                    {
                        writer.WritePropertyName(_hashResolver.ResolveBinHashGeneral(kvp.Key));
                        WritePropertyValue(writer, kvp.Value);
                    }
                    writer.WriteEndObject();
                    break;

                case BinPropertyType.Optional:
                    WritePropertyValue(writer, ((BinTreeOptional)prop).Value);
                    break;

                case BinPropertyType.Map:
                    writer.WriteStartObject();
                    foreach (var kvp in (BinTreeMap)prop)
                    {
                        // JSON keys must be strings.
                        var keyString = ConvertPropertyToString(kvp.Key);
                        writer.WritePropertyName(keyString);
                        WritePropertyValue(writer, kvp.Value);
                    }
                    writer.WriteEndObject();
                    break;

                default:
                    writer.WriteStartObject();
                    writer.WriteString("Type", prop.Type.ToString());
                    writer.WriteString("NameHash", _hashResolver.ResolveBinHashGeneral(prop.NameHash));
                    writer.WriteEndObject();
                    break;
            }
        }

        private string ConvertPropertyToString(BinTreeProperty prop)
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
    }
}
