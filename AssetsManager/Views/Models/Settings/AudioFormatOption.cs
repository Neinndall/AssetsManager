namespace AssetsManager.Views.Models.Settings
{
    public enum AudioExportFormat
    {
        Ogg,
        Wav,
        Mp3
    }

    public class AudioFormatOption
    {
        public string Name { get; set; }
        public AudioExportFormat Value { get; set; }
    }
}
