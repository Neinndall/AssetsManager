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
            if (jsonInput == null)
                return Task.FromResult(string.Empty);

            return Task.Run(() =>
            {
                try
                {
                    using (var stringWriter = new StringWriter())
                    using (var jsonWriter = new JsonTextWriter(stringWriter) { Formatting = Formatting.Indented })
                    {
                        if (jsonInput is string jsonString)
                        {
                            // If it's a string, read from it
                            using (var stringReader = new StringReader(jsonString))
                            using (var jsonReader = new JsonTextReader(stringReader))
                            {
                                jsonWriter.WriteToken(jsonReader);
                            }
                        }
                        else
                        {
                            // If it's an object (like a Dictionary), serialize it directly
                            var serializer = new JsonSerializer();
                            serializer.Serialize(jsonWriter, jsonInput);
                        }
                        return stringWriter.ToString();
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