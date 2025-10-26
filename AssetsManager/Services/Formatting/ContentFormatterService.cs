using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Utils;
using AssetsManager.Views.Helpers;
using LeagueToolkit.Core.Meta;

namespace AssetsManager.Services.Formatting
{
    public class ContentFormatterService
    {
        private readonly LogService _logService;
        private readonly JsBeautifierService _jsBeautifierService;
        private readonly CSSParserService _cssParserService;
        private readonly HashResolverService _hashResolverService;

        public ContentFormatterService(LogService logService, JsBeautifierService jsBeautifierService, CSSParserService cssParserService, HashResolverService hashResolverService)
        {
            _logService = logService;
            _jsBeautifierService = jsBeautifierService;
            _cssParserService = cssParserService;
            _hashResolverService = hashResolverService;
        }

        public async Task<string> GetFormattedStringAsync(string dataType, byte[] data)
        {
            if (data == null) return string.Empty;

            string formattedContent;

            switch (dataType)
            {
                case "bin":
                    formattedContent = await GetBinJsonStringAsync(data);
                    break;
                case "stringtable":
                    formattedContent = await GetStringTableJsonStringAsync(data);
                    break;
                case "css":
                    formattedContent = await GetCssJsonStringAsync(data);
                    break;
                case "json":
                    formattedContent = await GetFormattedJsonStringAsync(data);
                    break;
                case "js":
                    try
                    {
                        var jsText = Encoding.UTF8.GetString(data);
                        formattedContent = await _jsBeautifierService.BeautifyAsync(jsText);
                    }
                    catch (Exception ex)
                    {
                        _logService.LogWarning($"JS Beautifier failed: {ex.Message}");
                        formattedContent = Encoding.UTF8.GetString(data);
                    }
                    break;
                case "text":
                default:
                    formattedContent = Encoding.UTF8.GetString(data);
                    break;
            }
            return formattedContent;
        }

        private async Task<string> GetBinJsonStringAsync(byte[] data)
        {
            try
            {
                using var binStream = new MemoryStream(data);
                using var jsonStream = new MemoryStream();
                var binTree = new BinTree(binStream);
                await BinUtils.WriteBinTreeAsJsonAsync(jsonStream, binTree, _hashResolverService);
                jsonStream.Position = 0;
                using var reader = new StreamReader(jsonStream);
                return await reader.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to parse .bin file content.");
                return "Error parsing .bin file. See logs for details.";
            }
        }

        private async Task<string> GetStringTableJsonStringAsync(byte[] data)
        {
            try
            {
                using var inputStream = new MemoryStream(data);
                using var jsonStream = new MemoryStream();
                await StringTableUtils.WriteStringTableAsJsonAsync(jsonStream, inputStream, _hashResolverService);
                jsonStream.Position = 0;
                using var reader = new StreamReader(jsonStream, Encoding.UTF8);
                return await reader.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to parse .stringtable file content.");
                return "Error parsing .stringtable file. See logs for details.";
            }
        }

        private async Task<string> GetCssJsonStringAsync(byte[] data)
        {
            try
            {
                var cssText = Encoding.UTF8.GetString(data);
                return await _cssParserService.ConvertToJsonAsync(cssText);
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to parse .css file content.");
                return "Error parsing .css file. See logs for details.";
            }
        }

        private async Task<string> GetFormattedJsonStringAsync(byte[] data)
        {
            var rawJson = Encoding.UTF8.GetString(data);
            return await JsonFormatter.FormatJsonAsync(rawJson);
        }
    }
}
