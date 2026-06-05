using System;
using System.Collections.Generic;
using System.IO;

namespace AssetsManager.Utils
{
    public static class SupportedFileTypes
    {
        public static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp" };
        public static readonly HashSet<string> Textures = new(StringComparer.OrdinalIgnoreCase) { ".dds", ".tex" };
        public static readonly HashSet<string> VectorImages = new(StringComparer.OrdinalIgnoreCase) { ".svg" };
        public static readonly HashSet<string> Media = new(StringComparer.OrdinalIgnoreCase) { ".ogg", ".wem", ".webm", ".mp3" };
        public static readonly HashSet<string> AudioBank = new(StringComparer.OrdinalIgnoreCase) { ".wpk", ".bnk" };
        public static readonly HashSet<string> Viewer3D = new(StringComparer.OrdinalIgnoreCase) { ".skn", ".sco", ".scb" };
        public static readonly HashSet<string> Json = new(StringComparer.OrdinalIgnoreCase) { ".json" };
        public static readonly HashSet<string> JavaScript = new(StringComparer.OrdinalIgnoreCase) { ".js" };
        public static readonly HashSet<string> Css = new(StringComparer.OrdinalIgnoreCase) { ".css" };
        public static readonly HashSet<string> Bin = new(StringComparer.OrdinalIgnoreCase) { ".bin" };
        public static readonly HashSet<string> StringTable = new(StringComparer.OrdinalIgnoreCase) { ".stringtable" };
        public static readonly HashSet<string> Troybin = new(StringComparer.OrdinalIgnoreCase) { ".troybin" };
        public static readonly HashSet<string> Preload = new(StringComparer.OrdinalIgnoreCase) { ".preload" };
        public static readonly HashSet<string> PlainText = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".xml", ".ini", ".log", ".info" };
        public static readonly HashSet<string> Lua = new(StringComparer.OrdinalIgnoreCase) { ".luabin64" };

        public static bool IsExpandableAudioBank(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string extension = Path.GetExtension(fileName);
            return AudioBank.Contains(extension) && fileName.Contains("_audio", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsAudioDataContainer(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            return fileName.EndsWith(".wpk", StringComparison.OrdinalIgnoreCase) || 
                   (fileName.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase) && fileName.Contains("_audio", StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsImage(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string extension = Path.GetExtension(fileName);
            return Images.Contains(extension) || Textures.Contains(extension);
        }

        public static bool IsAudioBank(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string extension = Path.GetExtension(fileName);
            return AudioBank.Contains(extension);
        }

        public static bool IsText(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string extension = Path.GetExtension(fileName).ToLowerInvariant();
            return Json.Contains(extension) ||
                   JavaScript.Contains(extension) ||
                   Css.Contains(extension) ||
                   Bin.Contains(extension) ||
                   StringTable.Contains(extension) ||
                   Troybin.Contains(extension) ||
                   Preload.Contains(extension) ||
                   PlainText.Contains(extension) ||
                   Lua.Contains(extension);
        }

        public static bool IsDiffSupported(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            string extension = Path.GetExtension(fileName).ToLowerInvariant();

            return IsImage(fileName) || IsText(fileName) || extension == ".bnk";
        }
    }
}
