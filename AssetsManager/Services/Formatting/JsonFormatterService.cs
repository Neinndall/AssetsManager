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
        public string NormalizeTextForAlignment(DiffPaneModel paneModel, string[] fallbackLines = null)
        {
            if (paneModel == null || paneModel.Lines.Count == 0) return string.Empty;

            // Estimate capacity to reduce StringBuilder reallocations
            int estimatedLength = 0;
            foreach (var line in paneModel.Lines)
            {
                estimatedLength += (line.Text?.Length ?? (fallbackLines != null && line.Position.HasValue && line.Position.Value > 0 && line.Position.Value <= fallbackLines.Length ? fallbackLines[line.Position.Value - 1].Length : 0)) + 2;
            }

            var sb = new System.Text.StringBuilder(estimatedLength);
            foreach (var line in paneModel.Lines)
            {
                if (line.Type == ChangeType.Imaginary)
                {
                    sb.AppendLine(string.Empty);
                }
                else
                {
                    string text = line.Text;
                    if (text == null && fallbackLines != null && line.Position.HasValue && line.Position.Value > 0 && line.Position.Value <= fallbackLines.Length)
                    {
                        text = fallbackLines[line.Position.Value - 1];
                    }
                    sb.AppendLine(text ?? string.Empty);
                }
            }

            if (sb.Length >= 2)
            {
                sb.Length -= 2;
            }

            return sb.ToString();
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