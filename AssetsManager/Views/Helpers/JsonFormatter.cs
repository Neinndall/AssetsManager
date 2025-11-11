using System;
using Newtonsoft.Json;
using System.Collections.Generic;
using DiffPlex.DiffBuilder.Model;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AssetsManager.Views.Helpers
{
    public static class JsonFormatter
    {
        public static (string Text, List<ChangeType> LineTypes) NormalizeTextForAlignment(DiffPaneModel paneModel)
        {
            var lines = new List<string>();
            var lineTypes = new List<ChangeType>();

            foreach (var line in paneModel.Lines)
            {
                lines.Add(line.Type == ChangeType.Imaginary ? "" : line.Text ?? "");
                lineTypes.Add(line.Type);
            }

            return (string.Join("\r\n", lines), lineTypes);
        }

        public static Task<string> FormatJsonAsync(object jsonInput)
        {
            return FormatJsonAsync(jsonInput, null);
        }

        public static Task<string> FormatJsonAsync(object jsonInput, JsonSerializerSettings settings)
        {
            if (jsonInput == null)
                return Task.FromResult(string.Empty);

            settings ??= new JsonSerializerSettings();
            // Ensure Indented formatting is always applied for consistency, but allow other settings to be customized.
            settings.Formatting = Formatting.Indented;

            return Task.Run(() =>
            {
                try
                {
                    if (jsonInput is string jsonString)
                    {
                        // If it's a string, parse and re-serialize with settings
                        var parsedJson = JToken.Parse(jsonString);
                        return parsedJson.ToString(settings.Formatting);
                    }
                    else
                    {
                        // If it's an object, serialize it directly with the given settings
                        return JsonConvert.SerializeObject(jsonInput, settings);
                    }
                }
                catch (Exception)
                {
                    // Fallback for invalid JSON strings or serialization errors
                    return jsonInput.ToString();
                }
            });
        }
    }
}