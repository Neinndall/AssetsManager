namespace AssetsManager.Views.Models.Settings
{
    public class ImageFormatOption
    {
        public string Name { get; set; }
        public ImageExportFormat Value { get; set; }
    }

    public enum ImageExportFormat
    {
        Png
    }
}