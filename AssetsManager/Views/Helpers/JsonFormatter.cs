using System;
using System.Collections.Generic;
using DiffPlex.DiffBuilder.Model;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;

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

        public static Task<string> FormatJsonAsync(object jsonInput, JsonSerializerOptions options)
        {
            if (jsonInput == null)
                return Task.FromResult(string.Empty);

            var localOptions = options == null ? new JsonSerializerOptions() : new JsonSerializerOptions(options);
            localOptions.WriteIndented = true;

            return Task.Run(() =>
            {
                try
                {
                    if (jsonInput is string jsonString)
                    {
                        var parsedJson = JsonNode.Parse(jsonString);
                        return parsedJson.ToJsonString(localOptions);
                    }
                    else
                    {
                        return JsonSerializer.Serialize(jsonInput, localOptions);
                    }
                }
                catch (Exception)
                {
                    return jsonInput.ToString();
                }
            });
        }
    }
}
