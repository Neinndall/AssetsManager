using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;
using LeagueToolkit.IO.Inibin;

namespace AssetsManager.Utils
{
    public static class TroybinUtils
    {
        public static Task WriteInibinAsJsonAsync(Stream outputStream, InibinFile inibinFile, HashResolverService hashResolver)
        {
            return Task.Run(() => WriteInibinAsJson(outputStream, inibinFile, hashResolver));
        }

        private static void WriteInibinAsJson(Stream outputStream, InibinFile inibinFile, HashResolverService hashResolver)
        {
            var options = new JsonWriterOptions { Indented = true };
            using var writer = new Utf8JsonWriter(outputStream, options);

            writer.WriteStartObject();
            foreach (var set in inibinFile.Sets)
            {
                writer.WritePropertyName(set.Key.ToString());
                writer.WriteStartObject();
                foreach (var prop in set.Value.Properties)
                {
                    writer.WritePropertyName(hashResolver.ResolveBinHashGeneral(prop.Key));
                    WriteValue(writer, prop.Value);
                }
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.Flush();
        }

        private static void WriteValue(Utf8JsonWriter writer, object value)
        {
            if (value == null)
            {
                writer.WriteNullValue();
                return;
            }

            switch (value)
            {
                case string s: writer.WriteStringValue(s); break;
                case int i: writer.WriteNumberValue(i); break;
                case float f: writer.WriteNumberValue(f); break;
                case bool b: writer.WriteBooleanValue(b); break;
                case byte by: writer.WriteNumberValue(by); break;
                case short sh: writer.WriteNumberValue(sh); break;
                case double d: writer.WriteNumberValue(d); break;
                case uint ui: writer.WriteNumberValue(ui); break;
                case ulong ul: writer.WriteNumberValue(ul); break;
                case long l: writer.WriteNumberValue(l); break;
                case ushort us: writer.WriteNumberValue(us); break;
                case sbyte sb: writer.WriteNumberValue(sb); break;
                case decimal de: writer.WriteNumberValue(de); break;
                case float[] fa:
                    writer.WriteStartArray();
                    foreach (var v in fa) writer.WriteNumberValue(v);
                    writer.WriteEndArray();
                    break;
                case byte[] ba:
                    writer.WriteStartArray();
                    foreach (var v in ba) writer.WriteNumberValue(v);
                    writer.WriteEndArray();
                    break;
                default:
                    writer.WriteStringValue(value.ToString());
                    break;
            }
        }
    }
}
