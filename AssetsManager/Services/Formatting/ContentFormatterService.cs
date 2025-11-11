using AssetsManager.Services.Audio;
using AssetsManager.Services.Explorer;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AssetsManager.Services.Core;
using AssetsManager.Services.Hashes;
using AssetsManager.Services.Parsers;
using AssetsManager.Utils;
using AssetsManager.Views.Helpers;
using AssetsManager.Views.Models;
using LeagueToolkit.Core.Meta;

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

        public ContentFormatterService(LogService logService, JsBeautifierService jsBeautifierService, CSSParserService cssParserService, HashResolverService hashResolverService, AudioBankService audioBankService, WadExtractionService wadExtractionService)
        {
            _logService = logService;
            _jsBeautifierService = jsBeautifierService;
            _cssParserService = cssParserService;
            _hashResolverService = hashResolverService;
            _audioBankService = audioBankService;
            _wadExtractionService = wadExtractionService;
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

                var settings = new JsonSerializerSettings
                {
                    Formatting = Newtonsoft.Json.Formatting.Indented,
                    NullValueHandling = NullValueHandling.Ignore
                };
                return JsonConvert.SerializeObject(result, settings);
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
            try
            {
                using var bnkStream = new MemoryStream(data);
                var bnkFile = BnkParser.Parse(bnkStream, _logService);
                return await JsonFormatter.FormatJsonAsync(bnkFile);
            }
            catch (Exception ex)
            {
                _logService.LogError(ex, "Failed to parse .bnk file content.");
                return "Error parsing .bnk file. See logs for details.";
            }
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

