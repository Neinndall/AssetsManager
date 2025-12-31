using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace AssetsManager.Utils
{
    public static class PreloadUtils
    {
        public static async Task WritePreloadAsJsonAsync(Stream outputStream, byte[] preloadContent)
        {
            var content = Encoding.UTF8.GetString(preloadContent);
            var parsedObject = ParsePreload(content);

            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            await JsonSerializer.SerializeAsync(outputStream, parsedObject, options);
        }

        private static object ParsePreload(string content)
        {
            content = content.Replace("\r\n", "\n").Trim();

            var mainMatch = Regex.Match(content, @"^\s*(\w+)\s*=\s*{(.*)}\s*$", RegexOptions.Singleline);
            if (!mainMatch.Success)
            {
                return new { Error = "Could not parse .preload file. Unexpected format.", RawContent = content };
            }

            var rootKey = mainMatch.Groups[1].Value;
            var innerContent = mainMatch.Groups[2].Value.Trim();

            var entries = new List<object>();
            var braceLevel = 0;
            var lastSplit = 0;

            for (int i = 0; i < innerContent.Length; i++)
            {
                if (innerContent[i] == '{') braceLevel++;
                else if (innerContent[i] == '}') braceLevel--;
                else if (innerContent[i] == ',' && braceLevel == 0)
                {
                    var entryString = innerContent.Substring(lastSplit, i - lastSplit).Trim();
                    if (!string.IsNullOrEmpty(entryString))
                    {
                        entries.Add(ParseEntry(entryString));
                    }
                    lastSplit = i + 1;
                }
            }

            var lastEntryString = innerContent.Substring(lastSplit).Trim();
            if (!string.IsNullOrEmpty(lastEntryString))
            {
                 entries.Add(ParseEntry(lastEntryString));
            }
            
            var result = new Dictionary<string, object>();
            result[rootKey] = entries;
            return result;
        }

        private static object ParseEntry(string entry)
        {
            entry = entry.Trim();
            if (entry.StartsWith("{") && entry.EndsWith("}"))
            {
                var dict = new Dictionary<string, object>();
                var inner = entry.Substring(1, entry.Length - 2).Trim();

                var braceLevel = 0;
                var quoteLevel = false;
                var lastSplit = 0;

                for (int i = 0; i < inner.Length; i++)
                {
                    if (inner[i] == '"') quoteLevel = !quoteLevel;
                    if (inner[i] == '{') braceLevel++;
                    else if (inner[i] == '}') braceLevel--;
                    else if (inner[i] == ',' && braceLevel == 0 && !quoteLevel)
                    {
                        AddKeyValuePair(dict, inner.Substring(lastSplit, i - lastSplit));
                        lastSplit = i + 1;
                    }
                }
                AddKeyValuePair(dict, inner.Substring(lastSplit));
                return dict;
            }
            return entry;
        }

        private static void AddKeyValuePair(Dictionary<string, object> dict, string pair)
        {
            var parts = pair.Split(new[] { '=' }, 2);
            if (parts.Length == 2)
            {
                var key = parts[0].Trim();
                var value = parts[1].Trim();

                if (value.StartsWith("{") && value.EndsWith("}"))
                {
                    dict[key] = ParseEntry(value);
                }
                else
                {
                    dict[key] = value.Trim('"');
                }
            }
        }
    }
}
