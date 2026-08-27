using System;

namespace AssetsManager.Views.Models.Settings
{
    public class FormatOption<T>
    {
        public string Name { get; set; }
        public T Value { get; set; }
    }

    public enum AudioExportFormat
    {
        Ogg,
        Wav,
        Mp3,
        Flac
    }

    public static class AudioExportFormatExtensions
    {
        public static string GetExtension(this AudioExportFormat format) => format switch
        {
            AudioExportFormat.Ogg => ".ogg",
            AudioExportFormat.Wav => ".wav",
            AudioExportFormat.Mp3 => ".mp3",
            AudioExportFormat.Flac => ".flac",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported audio export format.")
        };
    }

    public enum ImageExportFormat
    {
        Original,
        Png,
        Jpeg
    }

    public enum DataExportFormat
    {
        Original,
        Json
    }

    public enum PreferredClient
    {
        PBE,
        LIVE
    }

    public enum ApiClientTarget
    {
        PBE,
        LIVE
    }

    public enum PreferredDirectory
    {
        All,
        Game,
        Plugins
    }
}
