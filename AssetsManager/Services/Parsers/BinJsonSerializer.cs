using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    public sealed class BinJsonSerializer
    {
        private readonly HashResolverService _hashResolver;

        public BinJsonSerializer(HashResolverService hashResolver)
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
                binStream.Position = startPosition;
                var overrideTree = new BinTree(binStream);
                WriteBinTreeAsJsonInternal(outputStream, overrideTree);
                return;
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

            var options = new JsonWriterOptions 
            { 
                Indented = true, 
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
            };

            // Attempt 1: Modern Streaming (Ultra-low RAM)
            try
            {
                using var writer = new Utf8JsonWriter(outputStream, options);
                RunStreamingLoop(writer, br, objectClasses);
            }
            catch
            {
                // Attempt 2: Ultimate Fallback to Proven BinTree (LeagueToolkit)
                // This is 100% reliable and matches version 4.0.0.2 behavior.
                binStream.Position = startPosition;
                outputStream.SetLength(0);
                var binTree = new BinTree(binStream);
                WriteBinTreeAsJsonInternal(outputStream, binTree);
            }
        }

        private void RunStreamingLoop(Utf8JsonWriter writer, BinaryReader br, uint[] objectClasses)
        {
            var resolution = new BinResolutionContext(_hashResolver);

            writer.WriteStartObject();
            for (int i = 0; i < objectClasses.Length; i++)
            {
                WriteObjectStreaming(writer, br, objectClasses[i], resolution);
            }
            writer.WriteEndObject();
            writer.Flush();
        }

        private void WriteObjectStreaming(Utf8JsonWriter writer, BinaryReader br, uint classHash, BinResolutionContext resolution)
        {
            br.ReadUInt32(); // size
            uint pathHash = br.ReadUInt32();
            ushort propertyCount = br.ReadUInt16();

            writer.WritePropertyName(resolution.Entry(pathHash));
            writer.WriteStartObject();
            writer.WriteString("type", resolution.Type(classHash));

            for (int i = 0; i < propertyCount; i++)
            {
                uint nameHash = br.ReadUInt32();
                var type = (BinPropertyType)br.ReadByte();

                writer.WritePropertyName(resolution.Field(nameHash));
                WritePropertyContentStreaming(writer, br, type, resolution);
            }

            writer.WriteEndObject();
        }

        private void WritePropertyContentStreaming(Utf8JsonWriter writer, BinaryReader br, BinPropertyType type, BinResolutionContext resolution)
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
                case BinPropertyType.Hash: writer.WriteStringValue(resolution.Hash(br.ReadUInt32())); break;
                case BinPropertyType.WadChunkLink: writer.WriteStringValue(_hashResolver.ResolveHash(br.ReadUInt64())); break;
                case BinPropertyType.ObjectLink: writer.WriteStringValue(resolution.Entry(br.ReadUInt32())); break;
                case BinPropertyType.BitBool: writer.WriteBooleanValue(br.ReadByte() != 0); break;
                case BinPropertyType.Optional:
                    var optType = (BinPropertyType)br.ReadByte();
                    byte hasValue = br.ReadByte();
                    if (hasValue != 0)
                    {
                        WritePropertyContentStreaming(writer, br, optType, resolution);
                    }
                    else writer.WriteNullValue();
                    break;
                case BinPropertyType.Container:
                case BinPropertyType.UnorderedContainer:
                    var itemType = (BinPropertyType)br.ReadByte();
                    br.ReadUInt32(); // container size
                    uint itemCount = br.ReadUInt32();
                    if (IsPrimitiveType(itemType))
                    {
                        WritePrimitiveContainerStreaming(writer, br, itemType, itemCount, resolution);
                    }
                    else
                    {
                        writer.WriteStartArray();
                        for (uint i = 0; i < itemCount; i++) WritePropertyContentStreaming(writer, br, itemType, resolution);
                        writer.WriteEndArray();
                    }
                    break;
                case BinPropertyType.Struct:
                case BinPropertyType.Embedded:
                    uint structClassHash = br.ReadUInt32();
                    if (structClassHash == 0) { writer.WriteNullValue(); return; }
                    br.ReadUInt32(); // struct size
                    ushort structPropCount = br.ReadUInt16();
                    writer.WriteStartObject();
                    writer.WriteString("type", resolution.Type(structClassHash));
                    for (int i = 0; i < structPropCount; i++)
                    {
                        uint pNameHash = br.ReadUInt32();
                        var pType = (BinPropertyType)br.ReadByte();
                        writer.WritePropertyName(resolution.Field(pNameHash));
                        WritePropertyContentStreaming(writer, br, pType, resolution);
                    }
                    writer.WriteEndObject();
                    break;
                case BinPropertyType.Map:
                    var kType = (BinPropertyType)br.ReadByte();
                    var vType = (BinPropertyType)br.ReadByte();
                    br.ReadUInt32(); // map size
                    uint mapCount = br.ReadUInt32();
                    writer.WriteStartObject();
                    for (uint i = 0; i < mapCount; i++)
                    {
                        string keyStr = ReadPropertyAsKeyStringStreaming(br, kType, resolution);
                        writer.WritePropertyName(keyStr);
                        WritePropertyContentStreaming(writer, br, vType, resolution);
                    }
                    writer.WriteEndObject();
                    break;
                default:
                    writer.WriteStartObject(); writer.WriteString("Type", type.ToString()); writer.WriteEndObject();
                    break;
            }
        }

        private string ReadPropertyAsKeyStringStreaming(BinaryReader br, BinPropertyType type, BinResolutionContext resolution)
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
                BinPropertyType.Hash => resolution.Hash(br.ReadUInt32()),
                _ => "UnknownKey_" + type.ToString()
            };
        }

        #endregion

        #region Fallback Serialization (BinTree)

        public Task<(string OldJson, string NewJson)> WriteBinDiffAsJsonAsync(byte[] oldData, byte[] newData)
        {
            return Task.Run(() =>
            {
                using var oldStream = new MemoryStream(oldData, writable: false);
                using var newStream = new MemoryStream(newData, writable: false);
                var oldTree = new BinTree(oldStream);
                var newTree = new BinTree(newStream);
                if (oldTree.IsOverride || newTree.IsOverride)
                {
                    return (WriteBinTreeAsJsonString(oldTree), WriteBinTreeAsJsonString(newTree));
                }

                BinTreeDiff diff = oldTree.Diff(newTree);

                return (WriteBinDiffSideAsJson(diff, useNewValues: false), WriteBinDiffSideAsJson(diff, useNewValues: true));
            });
        }

        private string WriteBinDiffSideAsJson(BinTreeDiff diff, bool useNewValues)
        {
            using var output = new MemoryStream();
            var options = new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            using var writer = new Utf8JsonWriter(output, options);
            var resolution = new BinResolutionContext(_hashResolver);

            writer.WriteStartObject();
            foreach (BinTreeObjectDiff objectDiff in diff.Objects)
            {
                BinTreeObject treeObject = GetObjectForSide(objectDiff, useNewValues);
                if (treeObject == null) continue;

                writer.WritePropertyName(resolution.Entry(objectDiff.PathHash));
                writer.WriteStartObject();
                writer.WriteString("type", resolution.Type(treeObject.ClassHash));

                if (objectDiff is not ModifiedBinTreeObjectDiff modifiedObject)
                {
                    foreach (var property in treeObject.Properties.OrderBy(x => x.Key))
                    {
                        writer.WritePropertyName(resolution.Field(property.Key));
                        WritePropertyValueInternal(writer, property.Value, resolution);
                    }
                }
                else
                {
                    WriteChangedProperties(
                        writer,
                        treeObject.Properties,
                        modifiedObject.Properties,
                        0,
                        useNewValues,
                        resolution
                    );
                }

                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.Flush();

            return Encoding.UTF8.GetString(output.ToArray());
        }

        private void WriteChangedProperties(
            Utf8JsonWriter writer,
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            IEnumerable<BinTreePropertyDiff> differences,
            int pathIndex,
            bool useNewValues,
            BinResolutionContext resolution)
        {
            foreach (var group in differences.GroupBy(x => x.Path[pathIndex]).OrderBy(x => x.Key))
            {
                if (!properties.TryGetValue(group.Key, out BinTreeProperty property)) continue;

                writer.WritePropertyName(resolution.Field(group.Key));
                if (group.Any(x => x.Path.Count == pathIndex + 1))
                {
                    BinTreePropertyDiff difference = group.Single(x => x.Path.Count == pathIndex + 1);
                    BinTreeProperty changedProperty = GetPropertyForSide(difference, useNewValues);
                    WritePropertyValueInternal(writer, changedProperty, resolution);
                    continue;
                }

                var structure = (BinTreeStruct)property;
                writer.WriteStartObject();
                writer.WriteString("type", resolution.Type(structure.ClassHash));
                WriteChangedProperties(
                    writer,
                    structure.Properties,
                    group,
                    pathIndex + 1,
                    useNewValues,
                    resolution
                );
                writer.WriteEndObject();
            }
        }

        private static BinTreeObject GetObjectForSide(BinTreeObjectDiff diff, bool useNewValue)
        {
            return diff switch
            {
                AddedBinTreeObjectDiff added when useNewValue => added.Object,
                RemovedBinTreeObjectDiff removed when !useNewValue => removed.Object,
                ModifiedBinTreeObjectDiff modified => useNewValue ? modified.NewObject : modified.OldObject,
                _ => null
            };
        }

        private static BinTreeProperty GetPropertyForSide(BinTreePropertyDiff diff, bool useNewValue)
        {
            return diff switch
            {
                AddedBinTreePropertyDiff added when useNewValue => added.Property,
                RemovedBinTreePropertyDiff removed when !useNewValue => removed.Property,
                ModifiedBinTreePropertyDiff modified => useNewValue ? modified.NewProperty : modified.OldProperty,
                _ => null
            };
        }

        private void WriteBinTreeAsJsonInternal(Stream outputStream, BinTree binTree)
        {
            var options = new JsonWriterOptions 
            { 
                Indented = true, 
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping 
            };
            using var writer = new Utf8JsonWriter(outputStream, options);

            var resolution = new BinResolutionContext(_hashResolver);

            writer.WriteStartObject();
            foreach (var kvp in binTree.Objects)
            {
                writer.WritePropertyName(resolution.Entry(kvp.Key));
                writer.WriteStartObject();
                writer.WriteString("type", resolution.Type(kvp.Value.ClassHash));
                foreach (var propKvp in kvp.Value.Properties)
                {
                    writer.WritePropertyName(resolution.Field(propKvp.Key));
                    WritePropertyValueInternal(writer, propKvp.Value, resolution);
                }
                writer.WriteEndObject();
            }
            WriteDataOverrides(writer, binTree.DataOverrides, resolution);
            writer.WriteEndObject();
            writer.Flush();
        }

        private string WriteBinTreeAsJsonString(BinTree binTree)
        {
            using var output = new MemoryStream();
            WriteBinTreeAsJsonInternal(output, binTree);
            return Encoding.UTF8.GetString(output.ToArray());
        }

        private void WriteDataOverrides(
            Utf8JsonWriter writer,
            IReadOnlyList<BinTreeDataOverride> overrides,
            BinResolutionContext resolution)
        {
            if (overrides.Count == 0) return;

            writer.WritePropertyName("$dataOverrides");
            writer.WriteStartArray();
            foreach (BinTreeDataOverride item in overrides)
            {
                writer.WriteStartObject();
                writer.WriteString("object", resolution.Entry(item.ObjectPathHash));
                writer.WriteString("propertyPath", item.PropertyPath);
                writer.WriteString("type", item.Property.Type.ToString());
                writer.WritePropertyName("value");
                WritePropertyValueInternal(writer, item.Property, resolution);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        private void WritePropertyValueInternal(Utf8JsonWriter writer, BinTreeProperty prop, BinResolutionContext resolution)
        {
            if (prop == null) { writer.WriteNullValue(); return; }

            switch (prop.Type)
            {
                case BinPropertyType.String: writer.WriteStringValue(((BinTreeString)prop).Value); break;
                case BinPropertyType.Hash: writer.WriteStringValue(resolution.Hash(((BinTreeHash)prop).Value)); break;
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
                case BinPropertyType.ObjectLink: writer.WriteStringValue(resolution.Entry(((BinTreeObjectLink)prop).Value)); break;
                case BinPropertyType.WadChunkLink: writer.WriteStringValue(_hashResolver.ResolveHash(((BinTreeWadChunkLink)prop).Value)); break;
                case BinPropertyType.Container:
                case BinPropertyType.UnorderedContainer:
                    var container = (BinTreeContainer)prop;
                    bool allPrimitive = true;
                    foreach (var p in container.Elements)
                    {
                        if (p != null && !IsPrimitiveType(p.Type))
                        {
                            allPrimitive = false;
                            break;
                        }
                    }
                    if (allPrimitive)
                    {
                        WritePrimitiveContainerFallback(writer, container, resolution);
                    }
                    else
                    {
                        writer.WriteStartArray();
                        foreach (var p in container.Elements) WritePropertyValueInternal(writer, p, resolution);
                        writer.WriteEndArray();
                    }
                    break;
                case BinPropertyType.Struct:
                case BinPropertyType.Embedded:
                    var structProp = (BinTreeStruct)prop;
                    writer.WriteStartObject();
                    writer.WriteString("type", resolution.Type(structProp.ClassHash));
                    foreach (var kvp in structProp.Properties) { writer.WritePropertyName(resolution.Field(kvp.Key)); WritePropertyValueInternal(writer, kvp.Value, resolution); }
                    writer.WriteEndObject();
                    break;
                case BinPropertyType.Optional: WritePropertyValueInternal(writer, ((BinTreeOptional)prop).Value, resolution); break;
                case BinPropertyType.Map:
                    writer.WriteStartObject();
                    foreach (var kvp in (BinTreeMap)prop) { writer.WritePropertyName(ConvertPropertyToStringInternal(kvp.Key, resolution)); WritePropertyValueInternal(writer, kvp.Value, resolution); }
                    writer.WriteEndObject();
                    break;
                default:
                    writer.WriteStartObject(); writer.WriteString("Type", prop.Type.ToString()); writer.WriteString("NameHash", resolution.Field(prop.NameHash)); writer.WriteEndObject();
                    break;
            }
        }

        private string ConvertPropertyToStringInternal(BinTreeProperty prop, BinResolutionContext resolution)
        {
            if (prop == null) return "null";
            switch (prop.Type)
            {
                case BinPropertyType.String: return ((BinTreeString)prop).Value;
                case BinPropertyType.Hash: return resolution.Hash(((BinTreeHash)prop).Value);
                case BinPropertyType.I8: return ((BinTreeI8)prop).Value.ToString();
                case BinPropertyType.U8: return ((BinTreeU8)prop).Value.ToString();
                case BinPropertyType.I16: return ((BinTreeI16)prop).Value.ToString();
                case BinPropertyType.U16: return ((BinTreeU16)prop).Value.ToString();
                case BinPropertyType.I32: return ((BinTreeI32)prop).Value.ToString();
                case BinPropertyType.U32: return ((BinTreeU32)prop).Value.ToString();
                case BinPropertyType.I64: return ((BinTreeI64)prop).Value.ToString();
                case BinPropertyType.U64: return ((BinTreeU64)prop).Value.ToString();
                default: return resolution.Field(prop.NameHash);
            }
        }

        #endregion

        #region Helpers

        private sealed class BinResolutionContext
        {
            private readonly HashResolverService _resolver;
            private readonly Dictionary<uint, string> _entries = new();
            private readonly Dictionary<uint, string> _fields = new();
            private readonly Dictionary<uint, string> _types = new();
            private readonly Dictionary<uint, string> _hashes = new();

            public BinResolutionContext(HashResolverService resolver)
            {
                _resolver = resolver;
            }

            public string Entry(uint hash) => Resolve(_entries, hash, _resolver.ResolveBinEntry);
            public string Field(uint hash) => Resolve(_fields, hash, _resolver.ResolveBinField);
            public string Type(uint hash) => Resolve(_types, hash, _resolver.ResolveBinType);
            public string Hash(uint hash) => Resolve(_hashes, hash, _resolver.ResolveBinHash);

            private static string Resolve(
                Dictionary<uint, string> cache,
                uint hash,
                Func<uint, string> resolver)
            {
                if (cache.TryGetValue(hash, out string resolved)) return resolved;
                resolved = resolver(hash);
                cache[hash] = resolved;
                return resolved;
            }
        }

        private bool IsPrimitiveType(BinPropertyType type)
        {
            switch (type)
            {
                case BinPropertyType.Bool:
                case BinPropertyType.I8:
                case BinPropertyType.U8:
                case BinPropertyType.I16:
                case BinPropertyType.U16:
                case BinPropertyType.I32:
                case BinPropertyType.U32:
                case BinPropertyType.I64:
                case BinPropertyType.U64:
                case BinPropertyType.F32:
                case BinPropertyType.BitBool:
                    return true;
                default:
                    return false;
            }
        }

        private void WritePrimitiveContainerStreaming(Utf8JsonWriter writer, BinaryReader br, BinPropertyType itemType, uint itemCount, BinResolutionContext resolution)
        {
            var sb = new StringBuilder();
            sb.Append("[");
            for (uint i = 0; i < itemCount; i++)
            {
                if (i > 0) sb.Append(", ");

                switch (itemType)
                {
                    case BinPropertyType.Bool:
                    case BinPropertyType.BitBool:
                        sb.Append(br.ReadByte() != 0 ? "true" : "false");
                        break;
                    case BinPropertyType.I8:
                        sb.Append(br.ReadSByte().ToString(CultureInfo.InvariantCulture));
                        break;
                    case BinPropertyType.U8:
                        sb.Append(br.ReadByte().ToString(CultureInfo.InvariantCulture));
                        break;
                    case BinPropertyType.I16:
                        sb.Append(br.ReadInt16().ToString(CultureInfo.InvariantCulture));
                        break;
                    case BinPropertyType.U16:
                        sb.Append(br.ReadUInt16().ToString(CultureInfo.InvariantCulture));
                        break;
                    case BinPropertyType.I32:
                        sb.Append(br.ReadInt32().ToString(CultureInfo.InvariantCulture));
                        break;
                    case BinPropertyType.U32:
                        sb.Append(br.ReadUInt32().ToString(CultureInfo.InvariantCulture));
                        break;
                    case BinPropertyType.I64:
                        sb.Append(br.ReadInt64().ToString(CultureInfo.InvariantCulture));
                        break;
                    case BinPropertyType.U64:
                        sb.Append(br.ReadUInt64().ToString(CultureInfo.InvariantCulture));
                        break;
                    case BinPropertyType.F32:
                        float f = br.ReadSingle();
                        sb.Append(float.IsFinite(f) ? f.ToString("0.####", CultureInfo.InvariantCulture) : JsonSerializer.Serialize(f.ToString(CultureInfo.InvariantCulture)));
                        break;
                    default:
                        sb.Append("null");
                        break;
                }
            }
            sb.Append("]");
            writer.WriteRawValue(sb.ToString());
        }

        private void WritePrimitiveContainerFallback(Utf8JsonWriter writer, BinTreeContainer container, BinResolutionContext resolution)
        {
            var sb = new StringBuilder();
            sb.Append("[");
            bool first = true;
            foreach (var p in container.Elements)
            {
                if (!first) sb.Append(", ");
                first = false;

                if (p == null) sb.Append("null");
                else if (p is BinTreeBool b) sb.Append(b.Value ? "true" : "false");
                else if (p is BinTreeBitBool bb) sb.Append(bb.Value ? "true" : "false");
                else if (p is BinTreeF32 f) sb.Append(float.IsFinite(f.Value) ? f.Value.ToString("0.####", CultureInfo.InvariantCulture) : JsonSerializer.Serialize(f.Value.ToString(CultureInfo.InvariantCulture)));
                else if (p is BinTreeI8 i8) sb.Append(i8.Value.ToString(CultureInfo.InvariantCulture));
                else if (p is BinTreeU8 u8) sb.Append(u8.Value.ToString(CultureInfo.InvariantCulture));
                else if (p is BinTreeI16 i16) sb.Append(i16.Value.ToString(CultureInfo.InvariantCulture));
                else if (p is BinTreeU16 u16) sb.Append(u16.Value.ToString(CultureInfo.InvariantCulture));
                else if (p is BinTreeI32 i32) sb.Append(i32.Value.ToString(CultureInfo.InvariantCulture));
                else if (p is BinTreeU32 u32) sb.Append(u32.Value.ToString(CultureInfo.InvariantCulture));
                else if (p is BinTreeI64 i64) sb.Append(i64.Value.ToString(CultureInfo.InvariantCulture));
                else if (p is BinTreeU64 u64) sb.Append(u64.Value.ToString(CultureInfo.InvariantCulture));
                else sb.Append("null");
            }
            sb.Append("]");
            writer.WriteRawValue(sb.ToString());
        }

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
