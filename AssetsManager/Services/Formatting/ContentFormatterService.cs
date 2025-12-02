using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LeagueToolkit.Core.Meta;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using AssetsManager.Utils;
using AssetsManager.Services.Audio;
using AssetsManager.Services.Explorer;
using AssetsManager.Views.Models.Audio;

namespace AssetsManager.Services.Formatting
{
    public class ContentFormatterService
    {
        private readonly LogService _logService;
        private readonly JsBeautifierService _jsBeautifierService;
        private readonly CSSParserService _cssParserService;
        private readonly HashResolverService _hashResolverService;
        private readonly AudioBankService _audioBankService;
        private readonly WadExtractionService _wadExtractionService;
        private readonly JsonFormatterService _jsonFormatterService;

        public ContentFormatterService(LogService logService, JsBeautifierService jsBeautifierService, CSSParserService cssParserService, HashResolverService hashResolverService, AudioBankService audioBankService, WadExtractionService wadExtractionService, JsonFormatterService jsonFormatterService)
        {
            _logService = logService;
            _jsBeautifierService = jsBeautifierService;
            _cssParserService = cssParserService;
            _hashResolverService = hashResolverService;
            _audioBankService = audioBankService;
            _wadExtractionService = wadExtractionService;
            _jsonFormatterService = jsonFormatterService;
        }

        public async Task<string> FormatAudioBankAsync(LinkedAudioBank linkedBank)
        {
            if (linkedBank == null) return "{}";

            try
            {
                var wpkData = linkedBank.WpkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.WpkNode) : null;
                var audioBnkData = linkedBank.AudioBnkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.AudioBnkNode) : null;
                var eventsBnkData = linkedBank.EventsBnkNode != null ? await _wadExtractionService.GetVirtualFileBytesAsync(linkedBank.EventsBnkNode) : null;

                List<AudioEventNode> result;
                if (linkedBank.BinData != null)
                {
                    result = _audioBankService.ParseAudioBank(wpkData, audioBnkData, eventsBnkData, linkedBank.BinData, linkedBank.BaseName, linkedBank.BinType);
                }
                else
                {
                    result = _audioBankService.ParseGenericAudioBank(wpkData, audioBnkData, eventsBnkData);
                }

                var options = new System.Text.Json.JsonSerializerOptions
                {
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                };
                return await _jsonFormatterService.FormatJsonAsync(result, options);
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to format audio bank with resolved names.");
                return "Error formatting audio bank. See logs for details.";
            }
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
                case "troybin":
                    formattedContent = await GetTroybinJsonStringAsync(data);
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
                case "bnk":
                    formattedContent = await GetBnkJsonStringAsync(data);
                    break;
                case "text":
                default:
                    formattedContent = Encoding.UTF8.GetString(data);
                    break;
            }
            return formattedContent;
        }

        private async Task<string> GetBnkJsonStringAsync(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return "{}";
            }

            try
            {
                using var bnkStream = new MemoryStream(data);
                var bnkFile = BnkParser.Parse(bnkStream, _logService);
                return await _jsonFormatterService.FormatJsonAsync(bnkFile);
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to parse .bnk file content.");
                return "Error parsing .bnk file. See logs for details.";
            }
        }

        private async Task<string> GetBinJsonStringAsync(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return "{}";
            }

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
            if (data == null || data.Length == 0)
            {
                return "{}";
            }

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
            if (data == null || data.Length == 0)
            {
                return "{}";
            }

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
            return await _jsonFormatterService.FormatJsonAsync(rawJson);
        }

        private async Task<string> GetTroybinJsonStringAsync(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return "{}";
            }

            try
            {
                using var troybinStream = new MemoryStream(data);
                using var jsonStream = new MemoryStream();
                var inibinFile = new LeagueToolkit.IO.Inibin.InibinFile(troybinStream);
                await TroybinUtils.WriteInibinAsJsonAsync(jsonStream, inibinFile, _hashResolverService);
                jsonStream.Position = 0;
                using var reader = new StreamReader(jsonStream);
                return await reader.ReadToEndAsync();
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to parse .troybin file content.");
                return "Error parsing .troybin file. See logs for details.";
            }
        }
    }
}

