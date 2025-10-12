namespace AssetsManager.Utils
{
    public static class SupportedFileTypes
    {
        public static readonly string[] Images = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".ico", ".webp" };
        public static readonly string[] Textures = { ".dds", ".tex" };
        public static readonly string[] VectorImages = { ".svg" };
        public static readonly string[] Media = { ".ogg", ".wem", ".webm" };
        public static readonly string[] AudioBank = { ".wpk", ".bnk" };

        public static readonly string[] Json = { ".json" };
        public static readonly string[] JavaScript = { ".js" };
        public static readonly string[] Css = { ".css" };
        public static readonly string[] Bin = { ".bin" };
        public static readonly string[] StringTable = { ".stringtable" };
        public static readonly string[] PlainText = { ".txt", ".xml", ".yaml", ".yml", ".ini", ".log", ".lua" };

        public static bool IsExpandableAudioBank(string fileName)
        {
            string extension = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
            return AudioBank.Contains(extension) && fileName.Contains("_audio");
        }
    }
}
