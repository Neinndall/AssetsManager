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
        Mp3
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

    public enum PreferredDirectory
    {
        All,
        Game,
        Plugins
    }
}
