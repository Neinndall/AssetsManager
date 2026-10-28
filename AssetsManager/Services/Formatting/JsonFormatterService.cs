using System;
using System.Globalization;
using DiffPlex.DiffBuilder.Model;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;

namespace AssetsManager.Services.Formatting
{
    public class JsonFormatterService
    {
        // Match escaped backslashes first so literal Unicode escape text is preserved.
        private static readonly Regex UnicodeEscape = new(@"\\\\|\\u([0-9A-Fa-f]{4})", RegexOptions.Compiled);
        private static readonly JsonSerializerOptions DisplayOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

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

        private static string RestoreUnicodeSpaces(string json)
        {
            // Even the relaxed encoder escapes Unicode spaces such as NBSP.
            return UnicodeEscape.Replace(json, match =>
            {
                if (!match.Groups[1].Success) return match.Value;
                char character = (char)ushort.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return char.GetUnicodeCategory(character) == UnicodeCategory.SpaceSeparator
                    ? character.ToString()
                    : match.Value;
            });
        }

        public Task<string> FormatJsonAsync(object jsonInput)
        {
            return FormatJsonAsync(jsonInput, null);
        }

        public Task<string> FormatJsonAsync(object jsonInput, JsonSerializerOptions options)
        {
            if (jsonInput == null)
                return Task.FromResult(string.Empty);

            var localOptions = options == null ? DisplayOptions : new JsonSerializerOptions(options)
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            return Task.Run(() =>
            {
                try
                {
                    if (jsonInput is string jsonString)
                    {
                        using var document = JsonDocument.Parse(jsonString);
                        return RestoreUnicodeSpaces(JsonSerializer.Serialize(document, localOptions));
                    }

                    return RestoreUnicodeSpaces(JsonSerializer.Serialize(jsonInput, localOptions));
                }
                catch (Exception)
                {
                    return jsonInput.ToString();
                }
            });
        }
    }
}