using System;
using System.Collections.Generic;
using DiffPlex.DiffBuilder.Model;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web; // Needed for JavaScriptEncoder

namespace AssetsManager.Services.Formatting
{
    public class JsonFormatterService
    {
        public (string Text, List<ChangeType> LineTypes) NormalizeTextForAlignment(DiffPaneModel paneModel)
        {
            var lineTypes = new List<ChangeType>(paneModel.Lines.Count);
            
            // Estimate capacity: line length + 2 (\r\n) per line
            int estimatedLength = 0;
            foreach (var line in paneModel.Lines)
            {
                lineTypes.Add(line.Type);
                if (line.Type != ChangeType.Imaginary && line.Text != null)
                {
                    estimatedLength += line.Text.Length + 2;
                }
                else
                {
                    estimatedLength += 2;
                }
            }

            var sb = new System.Text.StringBuilder(estimatedLength);
            foreach (var line in paneModel.Lines)
            {
                sb.AppendLine(line.Type == ChangeType.Imaginary ? string.Empty : (line.Text ?? string.Empty));
            }

            // Remove trailing \r\n added by the last AppendLine if any
            if (sb.Length >= 2)
            {
                sb.Length -= 2;
            }

            return (sb.ToString(), lineTypes);
        }

        public Task<string> FormatJsonAsync(object jsonInput)
        {
            return FormatJsonAsync(jsonInput, null);
        }

        public Task<string> FormatJsonAsync(object jsonInput, JsonSerializerOptions options)
        {
            if (jsonInput == null)
                return Task.FromResult(string.Empty);

            var localOptions = options ?? new JsonSerializerOptions();
            localOptions.WriteIndented = true;
            // Ensure Unicode characters are unescaped for readability in the UI
            localOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

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